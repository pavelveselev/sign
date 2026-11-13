using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;
using static Invest.Sign.Router.Tests.Engine.EngineHarness;

namespace Invest.Sign.Router.Tests.Engine;

/// <summary>Scenarios 1–13 from stage 2 of the plan, plus related edge cases.</summary>
public class EngineScenarioTests
{
    private readonly EngineHarness _h = new();

    private InMemoryDatabase Db => _h.Db;

    // ---- 1. A multi-stage route reaches COMPLETED. ----

    [Fact]
    public async Task S01_multi_stage_route_completes()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");

        var a = await _h.EnterAndRequestNextStageAsync(id);
        Assert.Equal("a", a.StageKey);
        Db.Sign(a.RequestId);
        await _h.StepAdvancedAsync(id);
        Assert.Equal(StageState.Done, Db.Stages[a.Id].State);

        var b = await _h.EnterAndRequestNextStageAsync(id);
        Assert.Equal("b", b.StageKey);
        Db.Sign(b.RequestId);
        await _h.StepAdvancedAsync(id);

        await _h.StepAdvancedAsync(id);

        var route = Db.Routes[id];
        Assert.Equal(RouteStatus.Completed, route.Status);
        Assert.Null(route.StatusReason);
        Assert.Null(route.LockedBy);
        Assert.All(Db.StagesOf(id), s => Assert.Equal(StageState.Done, s.State));
        Assert.Equal(
            [
                new SignRequest(a.RequestId, "doc-1", SigningKind.Manual, "alice"),
                new SignRequest(b.RequestId, "doc-1", SigningKind.Auto, "seal"),
            ],
            Db.SignRequests.Values.Select(r => r.Request));
    }

    [Fact]
    public async Task S01_tick_drives_route_by_due_times()
    {
        _h.Incoming.Add("doc-1", "two-stage");

        var tick = await _h.Engine.TickAsync(CancellationToken.None);   // discover + enter "a"
        Assert.Equal(1, tick.Discovery.Created);
        var id = Db.RouteByDoc("doc-1").Id;
        Assert.Equal(StepOutcome.Advanced, Assert.Single(tick.Steps).Outcome);

        await _h.Engine.TickAsync(CancellationToken.None);              // request "a"
        var a = Db.CurrentStage(id);
        Assert.Equal(StageState.Requested, a.State);
        Assert.Equal(Db.Now + _h.Options.PollInterval, Db.Routes[id].NextAttemptAt);

        tick = await _h.Engine.TickAsync(CancellationToken.None);       // not due yet
        Assert.Empty(tick.Steps);

        Db.Sign(a.RequestId);
        Db.Advance(_h.Options.PollInterval);
        await _h.Engine.TickAsync(CancellationToken.None);              // "a" done
        Assert.Equal(StageState.Done, Db.Stages[a.Id].State);
    }

    // ---- 2. A stage whose condition does not hold is skipped. ----

    [Fact]
    public async Task S02_stage_with_false_condition_is_skipped()
    {
        var id = await _h.StartDocumentAsync("doc-1", "conditional", """{"needs-b": false}""");

        var a = await _h.EnterAndRequestNextStageAsync(id);
        Db.Sign(a.RequestId);
        await _h.StepAdvancedAsync(id);

        var next = await _h.EnterAndRequestNextStageAsync(id);

        Assert.Equal("c", next.StageKey);
        Assert.Equal(["a", "c"], Db.StagesOf(id).Select(s => s.StageKey));
        var skipped = Assert.Single(Db.Audit, e => e.Entry.EventType == AuditEventTypes.StageSkipped);
        Assert.Contains("\"b\"", skipped.Entry.Payload);
    }

    [Fact]
    public async Task S02_stage_with_true_condition_is_entered()
    {
        var id = await _h.StartDocumentAsync("doc-1", "conditional", """{"needs-b": true}""");

        var a = await _h.EnterAndRequestNextStageAsync(id);
        Db.Sign(a.RequestId);
        await _h.StepAdvancedAsync(id);

        Assert.Equal("b", (await _h.EnterAndRequestNextStageAsync(id)).StageKey);
    }

    [Fact]
    public async Task Condition_error_fails_route_without_retries()
    {
        var id = await _h.StartDocumentAsync("doc-1", "conditional", """{"other": 1}""");

        var a = await _h.EnterAndRequestNextStageAsync(id);
        Db.Sign(a.RequestId);
        await _h.StepAdvancedAsync(id);
        await _h.StepAdvancedAsync(id);

        var route = Db.Routes[id];
        Assert.Equal(RouteStatus.Failed, route.Status);
        Assert.Equal(StatusReasons.ConditionError, route.StatusReason);
        Assert.Equal(0, route.Attempts);
        Assert.Equal(StageState.Done, Db.Stages[a.Id].State);
        Assert.Equal(AuditEventTypes.RouteFinished, Db.AuditTypes(id)[^1]);
        Assert.Contains("needs-b", Db.Audit[^1].Entry.Payload);
    }

    [Fact]
    public async Task Malformed_attributes_fail_route_on_condition_evaluation()
    {
        var id = await _h.StartDocumentAsync("doc-1", "conditional", "not json");

        await _h.StepAdvancedAsync(id);

        Assert.Equal(StatusReasons.ConditionError, Db.Routes[id].StatusReason);
    }

    // ---- 3. Rejection completes the route as REJECTED. ----

    [Fact]
    public async Task S03_rejection_rejects_route()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);

        Db.Reject(a.RequestId, "wrong amount");
        await _h.StepAdvancedAsync(id);

        var route = Db.Routes[id];
        Assert.Equal(RouteStatus.Rejected, route.Status);
        Assert.Null(route.LockedBy);
        var stage = Db.Stages[a.Id];
        Assert.Equal(StageState.Rejected, stage.State);
        Assert.Equal("wrong amount", stage.Reason);
        Assert.Equal(Db.Now, stage.FinishedAt);
        Assert.Single(Db.StagesOf(id));
        Assert.Equal([AuditEventTypes.StageRejected, AuditEventTypes.RouteFinished], Db.AuditTypes(id)[^2..]);
    }

    [Fact]
    public async Task Signing_error_fails_route()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);

        Db.Error(a.RequestId, "certificate expired");
        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Failed, Db.Routes[id].Status);
        Assert.Equal(StatusReasons.SigningError, Db.Routes[id].StatusReason);
        Assert.Equal(StageState.Failed, Db.Stages[a.Id].State);
        Assert.Equal("certificate expired", Db.Stages[a.Id].Reason);
    }

    // ---- 4. Timeout expires the route and cancels the request. ----

    [Fact]
    public async Task S04_timeout_expires_route_and_cancels_request()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        Assert.Equal(a.RequestedAt + TimeSpan.FromDays(1), a.DeadlineAt);

        Db.Advance(TimeSpan.FromDays(1));
        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Expired, Db.Routes[id].Status);
        Assert.Equal(StageState.Expired, Db.Stages[a.Id].State);
        Assert.True(Db.SignRequests[a.RequestId].Cancelled);
        Assert.Equal([AuditEventTypes.StageExpired, AuditEventTypes.RouteFinished], Db.AuditTypes(id)[^2..]);
    }

    [Fact]
    public async Task Pending_before_deadline_reschedules_poll_capped_at_deadline()
    {
        _h.Options.PollInterval = TimeSpan.FromHours(10);
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);

        Db.Advance(TimeSpan.FromHours(20));
        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Requested, Db.Stages[a.Id].State);
        Assert.Equal(a.DeadlineAt, Db.Routes[id].NextAttemptAt);
        Assert.False(Db.SignRequests[a.RequestId].Cancelled);
    }

    // ---- 5. A signature that arrived after the deadline but before the check counts. ----

    [Fact]
    public async Task S05_signature_after_deadline_before_check_counts()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);

        Db.Advance(TimeSpan.FromDays(2));
        Db.Sign(a.RequestId);
        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Active, Db.Routes[id].Status);
        Assert.Equal(StageState.Done, Db.Stages[a.Id].State);
        Assert.False(Db.SignRequests[a.RequestId].Cancelled);
    }

    // ---- 6. A technical failure is retried with a growing delay; success resets the counter. ----

    [Fact]
    public async Task S06_technical_failure_retries_with_backoff_and_success_resets()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        await _h.EnterAndRequestNextStageAsync(id);
        Db.FailOn(Ops.GetRequest, times: 2);

        var first = await _h.StepAsync(id);
        Assert.Equal(StepOutcome.TechnicalFailure, first.Outcome);
        Assert.IsType<IOException>(first.Error);
        var route = Db.Routes[id];
        Assert.Equal(1, route.Attempts);
        Assert.Equal(Db.Now + TimeSpan.FromSeconds(10), route.NextAttemptAt);
        Assert.Null(route.LockedBy);

        Db.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(StepOutcome.TechnicalFailure, (await _h.StepAsync(id)).Outcome);
        Assert.Equal(2, Db.Routes[id].Attempts);
        Assert.Equal(Db.Now + TimeSpan.FromSeconds(20), Db.Routes[id].NextAttemptAt);

        await _h.StepAdvancedAsync(id);
        Assert.Equal(0, Db.Routes[id].Attempts);
        Assert.Equal(RouteStatus.Active, Db.Routes[id].Status);
        Assert.Equal(2, Db.AuditTypes(id).Count(t => t == AuditEventTypes.TechnicalFailure));
    }

    [Fact]
    public void Retry_delay_doubles_and_is_capped()
    {
        var options = new EngineOptions { RetryBaseDelay = TimeSpan.FromSeconds(10), RetryMaxDelay = TimeSpan.FromSeconds(40) };

        Assert.Equal(
            [10, 20, 40, 40, 40],
            new[] { 1, 2, 3, 4, 100 }.Select(n => options.GetRetryDelay(n).TotalSeconds));
    }

    // ---- 7. Exhausted attempts fail the route. ----

    [Fact]
    public async Task S07_exhausted_attempts_fail_route()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        Db.FailOn(Ops.GetRequest, times: int.MaxValue);

        for (var i = 0; i < _h.Options.MaxAttempts; i++)
        {
            Assert.Equal(StepOutcome.TechnicalFailure, (await _h.StepAsync(id)).Outcome);
        }

        var route = Db.Routes[id];
        Assert.Equal(RouteStatus.Failed, route.Status);
        Assert.Equal(StatusReasons.AttemptsExhausted, route.StatusReason);
        Assert.Equal(_h.Options.MaxAttempts, route.Attempts);
        Assert.Equal(StageState.Failed, Db.Stages[a.Id].State);
        Assert.Equal(StatusReasons.AttemptsExhausted, Db.Stages[a.Id].Reason);
        Assert.Equal(
            [AuditEventTypes.TechnicalFailure, AuditEventTypes.StageFailed, AuditEventTypes.RouteFinished],
            Db.AuditTypes(id)[^3..]);
        Assert.Equal(StepOutcome.NotAcquired, (await _h.StepAsync(id)).Outcome);
    }

    // ---- 8. A lost lease leaves no changes. ----

    [Fact]
    public async Task S08_lease_lost_when_entering_stage_leaves_no_changes()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var stolenUntil = StealLeaseOnNextWrite(id);
        var before = Db.Routes[id];
        var auditBefore = Db.Audit.Count;

        var result = await _h.StepAsync(id);

        Assert.Equal(StepOutcome.LeaseLost, result.Outcome);
        Assert.Equal(before with { LockedBy = ReplicaB, LockedUntil = stolenUntil }, Db.Routes[id]);
        Assert.Empty(Db.Stages);
        Assert.Equal(auditBefore, Db.Audit.Count);
    }

    [Fact]
    public async Task S08_lease_lost_when_starting_stage_does_not_reach_signing_software()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        await _h.StepAdvancedAsync(id);
        var stage = Db.CurrentStage(id);
        StealLeaseOnNextWrite(id);
        var auditBefore = Db.Audit.Count;

        var result = await _h.StepAsync(id);

        Assert.Equal(StepOutcome.LeaseLost, result.Outcome);
        Assert.Equal(stage, Db.Stages[stage.Id]);
        Assert.Empty(Db.CreateRequestCalls);
        Assert.Equal(0, Db.Routes[id].Attempts);
        Assert.Equal(auditBefore, Db.Audit.Count);
    }

    [Fact]
    public async Task S08_lease_lost_on_completion_keeps_artifact_out()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        var content = Encoding.UTF8.GetBytes("signed");
        _h.Files.Files["a.pdf"] = content;
        Db.Sign(a.RequestId, "a.pdf");
        StealLeaseOnNextWrite(id);

        Assert.Equal(StepOutcome.LeaseLost, (await _h.StepAsync(id)).Outcome);

        Assert.Empty(Db.Artifacts);
        Assert.Equal(StageState.Requested, Db.Stages[a.Id].State);
    }

    [Fact]
    public async Task Leased_instance_is_not_taken_until_lease_expires()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        Db.Routes[id] = Db.Routes[id] with { LockedBy = ReplicaB, LockedUntil = Db.Now + TimeSpan.FromMinutes(5) };

        Assert.Equal(StepOutcome.NotAcquired, (await _h.StepAsync(id)).Outcome);
        Assert.Empty(await Candidates());

        Db.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.Equal([id], await Candidates());
        await _h.StepAdvancedAsync(id);
    }

    // ---- 9. Re-running a PENDING step does not create a second request. ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S09_retried_start_does_not_create_second_request(bool gatewayTransactional)
    {
        Db.GatewayTransactional = gatewayTransactional;
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        await _h.StepAdvancedAsync(id);
        var stage = Db.CurrentStage(id);
        Db.FailOn(Ops.UpdateStage);   // fails after create_sign_request in the same transaction

        Assert.Equal(StepOutcome.TechnicalFailure, (await _h.StepAsync(id)).Outcome);
        Assert.Equal(StageState.Pending, Db.Stages[stage.Id].State);

        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Requested, Db.Stages[stage.Id].State);
        Assert.Equal(2, Db.CreateRequestCalls.Count);
        Assert.All(Db.CreateRequestCalls, c => Assert.Equal(stage.RequestId, c.RequestId));
        Assert.Equal(stage.RequestId, Assert.Single(Db.SignRequests).Key);
    }

    // ---- 10. A document discovered by two replicas is created once. ----

    [Fact]
    public async Task S10_document_discovered_by_two_replicas_is_created_once()
    {
        _h.Incoming.Add("doc-1", "two-stage");
        var staleView = new FakeIncomingDocumentSource(Db) { Stale = true };
        staleView.Add("doc-1", "two-stage");
        var replicaB = _h.CreateEngine(ReplicaB, incoming: staleView);

        var first = await _h.Engine.DiscoverAsync(CancellationToken.None);
        var second = await replicaB.DiscoverAsync(CancellationToken.None);

        Assert.Equal((1, 0), (first.Created, first.AlreadyExisting));
        Assert.Equal((0, 1), (second.Created, second.AlreadyExisting));
        Assert.Empty(second.Errors);
        var route = Assert.Single(Db.Routes.Values);
        Assert.Equal([AuditEventTypes.RouteCreated], Db.AuditTypes(route.Id));
    }

    // ---- 11. An unknown document type creates a FAILED instance. ----

    [Fact]
    public async Task S11_unknown_document_type_creates_failed_instance()
    {
        _h.Incoming.Add("doc-1", "unknown-type");

        var tick = await _h.Engine.TickAsync(CancellationToken.None);

        Assert.Equal(1, tick.Discovery.Created);
        Assert.Empty(tick.Steps);
        var route = Db.RouteByDoc("doc-1");
        Assert.Equal(RouteStatus.Failed, route.Status);
        Assert.Equal(StatusReasons.UnknownDocType, route.StatusReason);
        Assert.Null(route.RouteCode);
        Assert.Equal([AuditEventTypes.RouteCreated, AuditEventTypes.RouteFinished], Db.AuditTypes(route.Id));
        Assert.Empty((await _h.Engine.DiscoverAsync(CancellationToken.None)).Errors);
    }

    // ---- 12. Missing file is retried; hash mismatch fails without retries. ----

    [Fact]
    public async Task S12_missing_file_is_retried_then_stored()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        var content = Encoding.UTF8.GetBytes("signed content");
        var sha = Sha256(content);
        Db.Sign(a.RequestId, "out/a.pdf", sha.ToUpperInvariant(), content.Length);

        var failed = await _h.StepAsync(id);

        Assert.Equal(StepOutcome.TechnicalFailure, failed.Outcome);
        Assert.IsType<FileNotFoundException>(failed.Error);
        Assert.Equal(StageState.Requested, Db.Stages[a.Id].State);
        Assert.Equal(1, Db.Routes[id].Attempts);

        _h.Files.Files["out/a.pdf"] = content;
        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Done, Db.Stages[a.Id].State);
        Assert.Equal(0, Db.Routes[id].Attempts);
        var artifact = Assert.Single(Db.Artifacts).Artifact;
        Assert.Equal(new NewSignedArtifact(a.Id, content, sha, "out/a.pdf"), artifact with { Content = content });
        Assert.Equal(content, artifact.Content);
    }

    [Theory]
    [InlineData("0000", null)]
    [InlineData(null, 999L)]
    public async Task S12_hash_or_size_mismatch_fails_without_retries(string? hash, long? size)
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        _h.Files.Files["out/a.pdf"] = Encoding.UTF8.GetBytes("signed content");
        Db.Sign(a.RequestId, "out/a.pdf", hash, size);

        await _h.StepAdvancedAsync(id);

        var route = Db.Routes[id];
        Assert.Equal(RouteStatus.Failed, route.Status);
        Assert.Equal(StatusReasons.ArtifactMismatch, route.StatusReason);
        Assert.Equal(0, route.Attempts);
        Assert.Equal(StageState.Failed, Db.Stages[a.Id].State);
        Assert.Empty(Db.Artifacts);
    }

    [Fact]
    public async Task Signed_without_file_ref_completes_stage_without_artifact()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        Db.Sign(a.RequestId);

        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Done, Db.Stages[a.Id].State);
        Assert.Empty(Db.Artifacts);
    }

    // ---- 13. Every transition leaves an audit entry. ----

    [Fact]
    public async Task S13_every_transition_is_audited_in_order()
    {
        var id = await _h.StartDocumentAsync("doc-1", "conditional", """{"needs-b": false}""");
        var a = await _h.EnterAndRequestNextStageAsync(id);
        Db.Sign(a.RequestId);
        await _h.StepAdvancedAsync(id);
        var c = await _h.EnterAndRequestNextStageAsync(id);
        Db.Sign(c.RequestId);
        await _h.StepAdvancedAsync(id);
        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Completed, Db.Routes[id].Status);
        Assert.Equal(
            [
                AuditEventTypes.RouteCreated,
                AuditEventTypes.StageEntered, AuditEventTypes.StageRequested, AuditEventTypes.StageDone,
                AuditEventTypes.StageSkipped,
                AuditEventTypes.StageEntered, AuditEventTypes.StageRequested, AuditEventTypes.StageDone,
                AuditEventTypes.RouteFinished,
            ],
            Db.AuditTypes(id));

        var entries = Db.Audit.Select(e => e.Entry).ToList();
        Assert.All(entries, e => Assert.Equal(ReplicaA, e.Actor));
        Assert.All(entries, e => JsonDocument.Parse(e.Payload!).Dispose());
        Assert.All(
            entries.Where(e => e.EventType is AuditEventTypes.StageEntered or AuditEventTypes.StageRequested or AuditEventTypes.StageDone),
            e => Assert.NotNull(e.StageInstanceId));
        Assert.Contains("\"COMPLETED\"", entries[^1].Payload);
    }

    [Fact]
    public async Task Polling_without_change_is_not_audited()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        await _h.EnterAndRequestNextStageAsync(id);
        var auditBefore = Db.Audit.Count;

        await _h.StepAdvancedAsync(id);

        Assert.Equal(auditBefore, Db.Audit.Count);
    }

    // ---- Route versions and registry. ----

    [Fact]
    public async Task Missing_route_version_fails_route()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var engineWithoutV1 = _h.CreateEngine(ReplicaA, new RouteRegistry([Conditional]));

        await _h.StepAdvancedAsync(id, engineWithoutV1);

        Assert.Equal(RouteStatus.Failed, Db.Routes[id].Status);
        Assert.Equal(StatusReasons.RouteNotFound, Db.Routes[id].StatusReason);
    }

    [Fact]
    public async Task Started_documents_keep_their_version_new_ones_get_latest()
    {
        var id1 = await _h.StartDocumentAsync("doc-1", "two-stage");
        var v2 = Route.For("two-stage", 2)
            .Stage("only", s => s.Automatic("seal").Timeout(TimeSpan.FromHours(1)))
            .Build();
        var engine = _h.CreateEngine(ReplicaA, new RouteRegistry([TwoStage, v2]));

        _h.Incoming.Add("doc-2", "two-stage");
        await engine.DiscoverAsync(CancellationToken.None);
        var id2 = Db.RouteByDoc("doc-2").Id;
        await _h.StepAdvancedAsync(id1, engine);
        await _h.StepAdvancedAsync(id2, engine);

        Assert.Equal(1, Db.Routes[id1].RouteVersion);
        Assert.Equal("a", Db.CurrentStage(id1).StageKey);
        Assert.Equal(2, Db.Routes[id2].RouteVersion);
        Assert.Equal("only", Db.CurrentStage(id2).StageKey);
    }

    // ---- Discovery robustness. ----

    [Fact]
    public async Task Source_failure_does_not_stop_processing()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");
        var engine = _h.CreateEngine(ReplicaA, incoming: new ThrowingSource());

        var tick = await engine.TickAsync(CancellationToken.None);

        Assert.IsType<IOException>(tick.Discovery.SourceError);
        Assert.Equal(id, Assert.Single(tick.Steps).RouteId);
    }

    [Fact]
    public async Task Failed_insert_of_one_document_does_not_block_others()
    {
        _h.Incoming.Add("doc-1", "two-stage");
        _h.Incoming.Add("doc-2", "two-stage");
        Db.FailOn(Ops.TryCreateRoute);

        var result = await _h.Engine.DiscoverAsync(CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal("doc-1", Assert.Single(result.Errors).DocId);
        Assert.Single(Db.Routes);
    }

    private DateTimeOffset StealLeaseOnNextWrite(long routeId)
    {
        var stolenUntil = Db.Now + TimeSpan.FromMinutes(10);
        Db.BeforeOperation = op =>
        {
            if (op != Ops.UpdateAndRelease)
            {
                return;
            }

            Db.BeforeOperation = null;
            Db.Routes[routeId] = Db.Routes[routeId] with { LockedBy = ReplicaB, LockedUntil = stolenUntil };
        };
        return stolenUntil;
    }

    private async Task<IReadOnlyList<long>> Candidates()
    {
        await using var uow = new FakeUnitOfWork(Db);
        return await uow.FindCandidatesAsync(10, CancellationToken.None);
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private sealed class ThrowingSource : IIncomingDocumentSource
    {
        public Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken) =>
            throw new IOException("view unavailable");
    }
}
