using System.Security.Cryptography;
using System.Text;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Oracle;
using Invest.Sign.Router.IntegrationTests.Infrastructure;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>Stage 2 scenarios 1, 3, 4, 8, 9, 10 on real Oracle with the stub external side.</summary>
[Collection(OracleCollection.Name)]
public sealed class OracleEngineScenarioTests(OracleFixture db) : IAsyncLifetime
{
    private const string ReplicaA = "replica-a";
    private const string ReplicaB = "replica-b";

    private static readonly RouteDefinition TwoStage = Route.For("two-stage", 1)
        .Stage("a", s => s.Manual("alice").Timeout(TimeSpan.FromDays(1)))
        .Stage("b", s => s.Automatic("seal").Timeout(TimeSpan.FromHours(1)))
        .Build();

    private readonly InterceptingUnitOfWorkFactory _unitOfWork = new(new OracleUnitOfWorkFactory(db.Options));
    private readonly InMemoryFiles _files = new();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- 1. A multi-stage route reaches COMPLETED. ----

    [OracleFact]
    public async Task S01_multi_stage_route_completes()
    {
        var engine = CreateEngine(ReplicaA);
        var id = await StartAsync(engine, "doc-1");

        foreach (var (stageKey, file) in new[] { ("a", "out/a.pdf"), ("b", "out/b.pdf") })
        {
            await StepAdvancedAsync(engine, id);   // enter
            await StepAdvancedAsync(engine, id);   // request
            var requestId = await CurrentRequestIdAsync(id, stageKey);
            var content = Encoding.UTF8.GetBytes($"signed {stageKey}");
            _files.Files[file] = content;
            await SignAsync(requestId, file, Convert.ToHexString(SHA256.HashData(content)), content.Length);
            await StepAdvancedAsync(engine, id);   // done
        }

        await StepAdvancedAsync(engine, id);       // complete

        Assert.Equal("COMPLETED", await RouteStatusAsync(id));
        Assert.Equal(["DONE", "DONE"], await db.QueryServiceAsync<string>(
            "SELECT state FROM sgn_stage_instance WHERE route_instance_id = :id ORDER BY id", new { id }));
        Assert.Equal(["MANUAL:alice", "AUTO:seal"], await db.QueryExternalAsync<string>(
            "SELECT kind || ':' || signer FROM stub_sign_request ORDER BY created_at, request_id"));
        Assert.Equal(["signed a", "signed b"], (await db.QueryServiceAsync<byte[]>(
            "SELECT content FROM sgn_signed_artifact ORDER BY id")).Select(Encoding.UTF8.GetString));
        Assert.Equal(
            [
                AuditEventTypes.RouteCreated,
                AuditEventTypes.StageEntered, AuditEventTypes.StageRequested, AuditEventTypes.StageDone,
                AuditEventTypes.StageEntered, AuditEventTypes.StageRequested, AuditEventTypes.StageDone,
                AuditEventTypes.RouteFinished,
            ],
            await AuditTypesAsync(id));
    }

    // ---- 3. Rejection completes the route as REJECTED. ----

    [OracleFact]
    public async Task S03_rejection_rejects_route()
    {
        var engine = CreateEngine(ReplicaA);
        var id = await StartAsync(engine, "doc-1");
        await StepAdvancedAsync(engine, id);
        await StepAdvancedAsync(engine, id);
        var requestId = await CurrentRequestIdAsync(id, "a");

        await db.ExecuteExternalAsync(
            "UPDATE stub_sign_request SET status = 'REJECTED', status_reason = 'wrong amount' WHERE request_id = :requestId",
            new { requestId });
        await StepAdvancedAsync(engine, id);

        Assert.Equal("REJECTED", await RouteStatusAsync(id));
        Assert.Equal(("REJECTED", "wrong amount"), Assert.Single(await db.QueryServiceAsync<(string, string)>(
            "SELECT state, reason FROM sgn_stage_instance WHERE route_instance_id = :id", new { id })));
    }

    // ---- 4. Timeout expires the route and cancels the request. ----

    [OracleFact]
    public async Task S04_timeout_expires_route_and_cancels_request()
    {
        var engine = CreateEngine(ReplicaA);
        var id = await StartAsync(engine, "doc-1");
        await StepAdvancedAsync(engine, id);
        await StepAdvancedAsync(engine, id);
        var requestId = await CurrentRequestIdAsync(id, "a");

        // Move the deadline into the past instead of waiting a day.
        await db.ExecuteServiceAsync(
            "UPDATE sgn_stage_instance SET deadline_at = SYSTIMESTAMP - INTERVAL '1' SECOND WHERE request_id = :requestId",
            new { requestId });
        await StepAdvancedAsync(engine, id);

        Assert.Equal("EXPIRED", await RouteStatusAsync(id));
        Assert.Equal("EXPIRED", Assert.Single(await db.QueryServiceAsync<string>(
            "SELECT state FROM sgn_stage_instance WHERE route_instance_id = :id", new { id })));
        Assert.Equal(("REJECTED", "CANCELLED"), Assert.Single(await db.QueryExternalAsync<(string, string)>(
            "SELECT status, status_reason FROM stub_sign_request")));
    }

    // ---- 8. A lost lease leaves no changes. ----

    [OracleFact]
    public async Task S08_lease_lost_when_entering_stage_leaves_no_changes()
    {
        var engine = CreateEngine(ReplicaA);
        var id = await StartAsync(engine, "doc-1");
        StealLeaseBeforeNextWrite(id);
        var auditBefore = (await AuditTypesAsync(id)).Count;

        var result = await engine.ProcessAsync(id, default);

        Assert.Equal(StepOutcome.LeaseLost, result.Outcome);
        Assert.Empty(await db.QueryServiceAsync<long>("SELECT id FROM sgn_stage_instance"));
        Assert.Equal(auditBefore, (await AuditTypesAsync(id)).Count);
        Assert.Equal((ReplicaB, 0, (long?)null), Assert.Single(await db.QueryServiceAsync<(string, int, long?)>(
            "SELECT locked_by, attempts, current_stage_id FROM sgn_route_instance WHERE id = :id", new { id })));
    }

    [OracleFact]
    public async Task S08_lease_lost_when_starting_stage_does_not_reach_signing_software()
    {
        var engine = CreateEngine(ReplicaA);
        var id = await StartAsync(engine, "doc-1");
        await StepAdvancedAsync(engine, id);
        StealLeaseBeforeNextWrite(id);

        var result = await engine.ProcessAsync(id, default);

        Assert.Equal(StepOutcome.LeaseLost, result.Outcome);
        Assert.Empty(await db.QueryExternalAsync<string>("SELECT request_id FROM stub_sign_request"));
        Assert.Equal("PENDING", Assert.Single(await db.QueryServiceAsync<string>("SELECT state FROM sgn_stage_instance")));
    }

    // ---- 9. Re-running a PENDING step does not create a second request. ----

    [OracleFact]
    public async Task S09_retried_start_does_not_create_second_request()
    {
        var engine = CreateEngine(ReplicaA);
        var id = await StartAsync(engine, "doc-1");
        await StepAdvancedAsync(engine, id);
        var requestId = await CurrentRequestIdAsync(id, "a");

        // Fail after create_sign_request inside the same transaction.
        _unitOfWork.Before = op =>
        {
            if (op != InterceptingUnitOfWorkFactory.UpdateStage)
            {
                return Task.CompletedTask;
            }

            _unitOfWork.Before = null;
            throw new IOException("Injected failure after create_sign_request.");
        };

        Assert.Equal(StepOutcome.TechnicalFailure, (await engine.ProcessAsync(id, default)).Outcome);
        Assert.Empty(await db.QueryExternalAsync<string>("SELECT request_id FROM stub_sign_request"));

        await StepAdvancedAsync(engine, id);

        Assert.Equal(requestId, Assert.Single(await db.QueryExternalAsync<string>("SELECT request_id FROM stub_sign_request")));
        Assert.Equal("REQUESTED", Assert.Single(await db.QueryServiceAsync<string>("SELECT state FROM sgn_stage_instance")));
    }

    [OracleFact]
    public async Task S09_create_sign_request_is_idempotent_across_committed_transactions()
    {
        await db.AddIncomingDocumentAsync("doc-1", "two-stage");
        var factory = new OracleUnitOfWorkFactory(db.Options);
        var request = new SignRequest("req-1", "doc-1", Core.Model.SigningKind.Auto, "seal");

        for (var i = 0; i < 2; i++)
        {
            await using var uow = await factory.BeginAsync(default);
            await uow.Gateway.CreateRequestAsync(request, default);
            await uow.CommitAsync(default);
        }

        Assert.Single(await db.QueryExternalAsync<string>("SELECT request_id FROM stub_sign_request"));
    }

    // ---- 10. A document discovered by two replicas is created once. ----

    [OracleFact]
    public async Task S10_documents_discovered_by_two_replicas_concurrently_are_created_once()
    {
        const int count = 20;
        for (var i = 0; i < count; i++)
        {
            await db.AddIncomingDocumentAsync($"doc-{i:D2}", "two-stage");
        }

        var replicas = Enumerable.Range(0, 3).Select(i => CreateEngine($"replica-{i}")).ToList();

        var results = await Task.WhenAll(replicas.Select(r => Task.Run(() => r.DiscoverAsync(default))));

        Assert.All(results, r => Assert.Empty(r.Errors));
        Assert.All(results, r => Assert.Null(r.SourceError));
        Assert.Equal(count, results.Sum(r => r.Created));
        Assert.Equal(count, (await db.QueryServiceAsync<long>("SELECT id FROM sgn_route_instance")).Count);
        Assert.Equal(count, (await db.QueryServiceAsync<long>(
            "SELECT id FROM sgn_audit_event WHERE event_type = 'ROUTE_CREATED'")).Count);
    }

    [OracleFact]
    public async Task Tick_on_two_replicas_processes_each_instance_once()
    {
        for (var i = 0; i < 10; i++)
        {
            await db.AddIncomingDocumentAsync($"doc-{i}", "two-stage");
        }

        var a = CreateEngine(ReplicaA);
        var b = CreateEngine(ReplicaB);
        await a.DiscoverAsync(default);

        var ticks = await Task.WhenAll(Task.Run(() => a.TickAsync(default)), Task.Run(() => b.TickAsync(default)));

        // A replica may take an instance right after the other released it (the capture does not check
        // next_attempt_at), which only advances it one more step; it must never fail or double-enter a stage.
        var steps = ticks.SelectMany(t => t.Steps).ToList();
        Assert.All(steps, s => Assert.Contains(s.Outcome, new[] { StepOutcome.Advanced, StepOutcome.NotAcquired }));
        Assert.Equal(10, steps.Where(s => s.Outcome == StepOutcome.Advanced).Select(s => s.RouteId).Distinct().Count());
        Assert.Equal(10, (await db.QueryServiceAsync<long>("SELECT id FROM sgn_stage_instance")).Count);
    }

    private SigningEngine CreateEngine(string replicaId) =>
        new(_unitOfWork, new OracleIncomingDocumentSource(db.Options), _files, new SigningStageHandler(),
            new RouteRegistry([TwoStage]),
            new EngineOptions { PollInterval = TimeSpan.FromSeconds(1), MaxAttempts = 3 },
            replicaId);

    private async Task<long> StartAsync(SigningEngine engine, string docId)
    {
        await db.AddIncomingDocumentAsync(docId, "two-stage");
        Assert.Equal(1, (await engine.DiscoverAsync(default)).Created);
        return Assert.Single(await db.QueryServiceAsync<long>(
            "SELECT id FROM sgn_route_instance WHERE external_doc_id = :docId", new { docId }));
    }

    private static async Task StepAdvancedAsync(SigningEngine engine, long id)
    {
        var result = await engine.ProcessAsync(id, default);
        Assert.True(result.Outcome == StepOutcome.Advanced, $"Expected Advanced, got {result.Outcome}: {result.Error}");
    }

    private async Task<string> CurrentRequestIdAsync(long id, string stageKey) =>
        Assert.Single(await db.QueryServiceAsync<string>(
            """
            SELECT s.request_id
              FROM sgn_stage_instance s
              JOIN sgn_route_instance r ON r.current_stage_id = s.id
             WHERE r.id = :id AND s.stage_key = :stageKey
            """,
            new { id, stageKey }));

    private Task SignAsync(string requestId, string fileRef, string fileHash, long fileSize) =>
        db.ExecuteExternalAsync(
            """
            UPDATE stub_sign_request
               SET status = 'SIGNED', signed_at = SYSTIMESTAMP, file_ref = :fileRef, file_hash = :fileHash, file_size = :fileSize
             WHERE request_id = :requestId
            """,
            new { requestId, fileRef, fileHash, fileSize });

    private async Task<string> RouteStatusAsync(long id) =>
        Assert.Single(await db.QueryServiceAsync<string>("SELECT status FROM sgn_route_instance WHERE id = :id", new { id }));

    private async Task<IReadOnlyList<string>> AuditTypesAsync(long id) =>
        await db.QueryServiceAsync<string>(
            "SELECT event_type FROM sgn_audit_event WHERE route_instance_id = :id ORDER BY id", new { id });

    /// <summary>Before the step's fenced write, another replica takes over the lease and commits.</summary>
    private void StealLeaseBeforeNextWrite(long id) =>
        _unitOfWork.Before = async op =>
        {
            if (op != InterceptingUnitOfWorkFactory.UpdateAndRelease)
            {
                return;
            }

            _unitOfWork.Before = null;
            await using var connection = new OracleConnection(db.ServiceConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText = """
                UPDATE sgn_route_instance
                   SET locked_by = :owner, locked_until = SYSTIMESTAMP + INTERVAL '10' MINUTE
                 WHERE id = :id
                """;
            command.Parameters.Add("owner", ReplicaB);
            command.Parameters.Add("id", id);
            await command.ExecuteNonQueryAsync();
        };

    private sealed class InMemoryFiles : ISignedFileSource
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken) =>
            Files.TryGetValue(fileRef, out var content)
                ? Task.FromResult(content)
                : throw new FileNotFoundException(fileRef);
    }
}
