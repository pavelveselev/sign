using System.Security.Cryptography;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;
using static Invest.Sign.Router.Tests.Engine.EngineHarness;

namespace Invest.Sign.Router.Tests.Engine;

/// <summary>Этапы со своим обработчиком: исходы, данные этапа, транзакция перехода, срок, проверка при запуске.</summary>
public class StageHandlerTests
{
    private readonly EngineHarness _h = new();

    private InMemoryDatabase Db => _h.Db;

    private ScriptedStageHandler Handler => _h.Scripted;

    [Fact]
    public async Task Done_on_start_completes_stage_in_one_step_and_moves_to_next_stage()
    {
        Handler.OnStart = _ => new StageOutcome.Done(Data: "{\"target\":\"out/doc-1.pdf\"}");
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);   // вход в "x"
        var x = Db.CurrentStage(id);

        await _h.StepAdvancedAsync(id);   // запуск "x": сразу DONE

        var done = Db.Stages[x.Id];
        Assert.Equal(StageState.Done, done.State);
        Assert.Equal("{\"target\":\"out/doc-1.pdf\"}", done.Data);
        Assert.NotNull(done.RequestedAt);
        Assert.NotNull(done.FinishedAt);
        Assert.Empty(Handler.Checks);

        var context = Assert.Single(Handler.Starts);
        Assert.Equal(x.RequestId, context.RequestId);
        Assert.Equal("x-settings", context.Settings);
        Assert.Equal("doc-1", context.Document.ExternalDocId);

        var transition = Assert.Single(Handler.Transitions);
        Assert.Equal((StageState.Pending, StageState.Done), (transition.From, transition.To));

        await _h.StepAdvancedAsync(id);   // вход в "y"
        Assert.Equal("y", Db.CurrentStage(id).StageKey);
    }

    [Fact]
    public async Task Waiting_stage_keeps_and_updates_its_data_until_done()
    {
        Handler.OnStart = _ => new StageOutcome.Waiting("{\"id\":\"ext-1\"}");
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        var x = await _h.EnterAndRequestNextStageAsync(id);

        Assert.Equal("{\"id\":\"ext-1\"}", x.Data);
        Assert.Equal(x.RequestedAt + TimeSpan.FromHours(1), x.DeadlineAt);
        Assert.Contains(Db.Audit, a => a.Entry.EventType == AuditEventTypes.StageRequested
            && a.Entry.Payload!.Contains("ScriptedStageHandler") && a.Entry.Payload.Contains("x-settings"));

        Handler.OnCheck = _ => new StageOutcome.Waiting("{\"id\":\"ext-1\",\"progress\":50}");
        Db.Advance(TimeSpan.FromMinutes(1));
        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Requested, Db.Stages[x.Id].State);
        Assert.Equal("{\"id\":\"ext-1\",\"progress\":50}", Db.Stages[x.Id].Data);

        Handler.OnCheck = _ => new StageOutcome.Done();
        Db.Advance(TimeSpan.FromMinutes(1));
        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Done, Db.Stages[x.Id].State);
        Assert.Equal("{\"id\":\"ext-1\",\"progress\":50}", Db.Stages[x.Id].Data);
        Assert.Equal(2, Handler.Checks.Count);
        Assert.Equal("{\"id\":\"ext-1\",\"progress\":50}", Handler.Checks[^1].Data);
        Assert.Equal(
            [(StageState.Pending, StageState.Requested), (StageState.Requested, StageState.Done)],
            Handler.Transitions.Select(t => (t.From, t.To)));
    }

    [Fact]
    public async Task Rejected_outcome_rejects_route()
    {
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        var x = await _h.EnterAndRequestNextStageAsync(id);
        Handler.OnCheck = _ => new StageOutcome.Rejected("declined by service");

        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Rejected, Db.Routes[id].Status);
        Assert.Equal(StageState.Rejected, Db.Stages[x.Id].State);
        Assert.Equal("declined by service", Db.Stages[x.Id].Reason);
        var transition = Handler.Transitions[^1];
        Assert.Equal(StageState.Rejected, transition.To);
        Assert.Equal("declined by service", transition.Reason);
    }

    [Fact]
    public async Task Failed_outcome_on_start_fails_route_with_stage_error()
    {
        Handler.OnStart = _ => new StageOutcome.Failed("HTTP 400: bad request");
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);
        var x = Db.CurrentStage(id);

        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Failed, Db.Routes[id].Status);
        Assert.Equal(StatusReasons.StageError, Db.Routes[id].StatusReason);
        Assert.Equal(StageState.Failed, Db.Stages[x.Id].State);
        Assert.Equal("HTTP 400: bad request", Db.Stages[x.Id].Reason);
        Assert.Equal((StageState.Pending, StageState.Failed), (Handler.Transitions[^1].From, Handler.Transitions[^1].To));
    }

    [Fact]
    public async Task Done_with_file_saves_artifact()
    {
        var content = "result"u8.ToArray();
        _h.Files.Files["out/x.pdf"] = content;
        Handler.OnStart = _ => new StageOutcome.Done(
            File: new StageFile("out/x.pdf", Convert.ToHexStringLower(SHA256.HashData(content)), content.Length));
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);

        await _h.StepAdvancedAsync(id);

        var artifact = Assert.Single(Db.Artifacts).Artifact;
        Assert.Equal(content, artifact.Content);
        Assert.Equal("out/x.pdf", artifact.SourceRef);
    }

    [Fact]
    public async Task Done_with_file_content_saves_artifact_without_reading_the_share()
    {
        var content = "signature"u8.ToArray();
        Handler.OnStart = _ => new StageOutcome.Done(File: new StageFile("doc.pdf.sig", Content: content));
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);

        await _h.StepAdvancedAsync(id);

        var artifact = Assert.Single(Db.Artifacts).Artifact;
        Assert.Equal(content, artifact.Content);
        Assert.Equal("doc.pdf.sig", artifact.SourceRef);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), artifact.Sha256);
        Assert.Empty(_h.Files.Files);
    }

    [Fact]
    public async Task Done_with_file_content_still_verifies_expected_hash()
    {
        Handler.OnStart = _ => new StageOutcome.Done(
            File: new StageFile("doc.pdf.sig", Hash: "00", Content: "signature"u8.ToArray()));
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);

        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Failed, Db.Routes[id].Status);
        Assert.Equal(StatusReasons.ArtifactMismatch, Db.Routes[id].StatusReason);
        Assert.Empty(Db.Artifacts);
    }

    [Fact]
    public async Task Expired_stage_is_cancelled_outside_and_reported_inside_the_transaction()
    {
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        var x = await _h.EnterAndRequestNextStageAsync(id);
        Db.Advance(TimeSpan.FromHours(1));

        await _h.StepAdvancedAsync(id);

        Assert.Equal(RouteStatus.Expired, Db.Routes[id].Status);
        Assert.Equal(StageState.Expired, Db.Stages[x.Id].State);
        Assert.Equal(x.RequestId, Assert.Single(Handler.Cancels).RequestId);
        var transition = Handler.Transitions[^1];
        Assert.Equal((StageState.Requested, StageState.Expired), (transition.From, transition.To));
        Assert.Null(transition.Outcome);
    }

    [Fact]
    public async Task Exception_from_start_is_retried_with_the_same_request_id()
    {
        var calls = 0;
        Handler.OnStart = _ => ++calls == 1 ? throw new IOException("share unavailable") : new StageOutcome.Done();
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);
        var x = Db.CurrentStage(id);

        var failed = await _h.StepAsync(id);

        Assert.Equal(StepOutcome.TechnicalFailure, failed.Outcome);
        Assert.Equal(StageState.Pending, Db.Stages[x.Id].State);
        Assert.Equal(1, Db.Routes[id].Attempts);

        Db.Advance(TimeSpan.FromSeconds(10));
        await _h.StepAdvancedAsync(id);

        Assert.Equal(StageState.Done, Db.Stages[x.Id].State);
        Assert.All(Handler.Starts, s => Assert.Equal(x.RequestId, s.RequestId));
        Assert.Equal(2, Handler.Starts.Count);
    }

    [Fact]
    public async Task Exception_in_transition_rolls_back_the_step()
    {
        Handler.OnStart = _ => new StageOutcome.Done(Data: "data");
        Handler.OnTransition = _ => throw new InvalidOperationException("db write failed");
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);
        var x = Db.CurrentStage(id);

        var result = await _h.StepAsync(id);

        Assert.Equal(StepOutcome.TechnicalFailure, result.Outcome);
        Assert.Equal(x, Db.Stages[x.Id]);
        Assert.DoesNotContain(Db.Audit, a => a.Entry.EventType == AuditEventTypes.StageDone);
    }

    [Fact]
    public async Task Lost_lease_skips_transition_hook()
    {
        Handler.OnStart = _ => new StageOutcome.Done();
        var id = await _h.StartDocumentAsync("doc-1", "custom");
        await _h.StepAdvancedAsync(id);
        Db.BeforeOperation = op =>
        {
            if (op == Ops.UpdateAndRelease)
            {
                Db.BeforeOperation = null;
                Db.Routes[id] = Db.Routes[id] with { LockedBy = ReplicaB, LockedUntil = Db.Now + TimeSpan.FromMinutes(10) };
            }
        };

        Assert.Equal(StepOutcome.LeaseLost, (await _h.StepAsync(id)).Outcome);

        Assert.Single(Handler.Starts);
        Assert.Empty(Handler.Transitions);
        Assert.Equal(StageState.Pending, Db.CurrentStage(id).State);
    }

    [Fact]
    public async Task Stage_requested_audit_describes_handler_and_settings()
    {
        var id = await _h.StartDocumentAsync("doc-1", "two-stage");

        await _h.EnterAndRequestNextStageAsync(id);

        var requested = Db.Audit.Single(a => a.Entry.EventType == AuditEventTypes.StageRequested).Entry.Payload!;
        Assert.Contains("\"handler\":\"FakeSigningStageHandler\"", requested);
        Assert.Contains("\"signer\":\"alice\"", requested);
    }

    [Fact]
    public void Engine_does_not_start_when_a_stage_handler_is_not_registered()
    {
        var registry = new StageHandlerRegistry(new FakeSigningStageHandler(Db));

        var ex = Assert.Throws<InvalidOperationException>(() => new SigningEngine(
            new FakeUnitOfWorkFactory(Db), _h.Incoming, _h.Files, registry, new RouteRegistry([Custom]), _h.Options, ReplicaA));

        Assert.Contains("Route custom@v1, stage 'x': handler ScriptedStageHandler is not registered.", ex.Message);
    }

    [Fact]
    public void Engine_does_not_start_when_a_handler_rejects_stage_settings()
    {
        Handler.Validate = settings => settings is "x-settings" ? ["unsupported settings"] : [];

        var ex = Assert.Throws<InvalidOperationException>(() => _h.Engine);

        Assert.Contains("stage 'x': unsupported settings.", ex.Message);
    }
}
