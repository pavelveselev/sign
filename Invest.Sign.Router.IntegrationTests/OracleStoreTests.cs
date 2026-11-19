using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Infrastructure.Oracle;
using Invest.Sign.Router.IntegrationTests.Infrastructure;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>Реализации хранилища, шлюза и источника входящих документов для Oracle, протестированные напрямую.</summary>
[Collection(OracleCollection.Name)]
public sealed class OracleStoreTests(OracleFixture db) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlockedWait = TimeSpan.FromSeconds(2);

    private readonly OracleUnitOfWorkFactory _factory = new(db.Options);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [OracleFact]
    public async Task Now_comes_from_the_database_in_utc()
    {
        await using var uow = await _factory.BeginAsync(default);

        var now = await uow.Store.GetNowAsync(default);

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.InRange(now, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [OracleFact]
    public async Task Route_round_trips_and_duplicate_insert_keeps_transaction_usable()
    {
        long id;
        DateTimeOffset now;
        await using (var uow = await _factory.BeginAsync(default))
        {
            now = await uow.Store.GetNowAsync(default);
            id = (await uow.Store.TryCreateRouteAsync(NewRoute("doc-1", now, """{"amount": 15000.5}"""), default))!.Value;

            Assert.Null(await uow.Store.TryCreateRouteAsync(NewRoute("doc-1", now), default));
            Assert.NotNull(await uow.Store.TryCreateRouteAsync(NewRoute("doc-2", now), default));
            await uow.CommitAsync(default);
        }

        await using (var uow = await _factory.BeginAsync(default))
        {
            var route = await uow.Store.GetRouteAsync(id, default);

            Assert.NotNull(route);
            Assert.Equal("doc-1", route.ExternalDocId);
            Assert.Equal("contract", route.DocType);
            Assert.Equal("""{"amount": 15000.5}""", route.Attributes);
            Assert.Equal(("contract", 1), (route.RouteCode, route.RouteVersion));
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(now, route.NextAttemptAt);
            Assert.Null(route.CurrentStageId);
            Assert.Null(route.LockedBy);
            Assert.Equal(0, route.Attempts);
        }
    }

    [OracleFact]
    public async Task Candidates_are_due_unleased_active_and_ordered()
    {
        long early, late, leased, future, failed;
        await using (var uow = await _factory.BeginAsync(default))
        {
            var now = await uow.Store.GetNowAsync(default);
            late = await CreateAsync(uow, "late", now.AddMinutes(-1));
            early = await CreateAsync(uow, "early", now.AddMinutes(-2));
            leased = await CreateAsync(uow, "leased", now.AddMinutes(-3));
            future = await CreateAsync(uow, "future", now.AddMinutes(10));
            failed = (await uow.Store.TryCreateRouteAsync(
                NewRoute("failed", now.AddMinutes(-5)) with { Status = RouteStatus.Failed, StatusReason = "X" }, default))!.Value;
            Assert.True(await uow.Store.TryAcquireAsync(leased, "replica-a", Lease, default));
            await uow.CommitAsync(default);
        }

        await using (var uow = await _factory.BeginAsync(default))
        {
            Assert.Equal([early, late], await uow.Store.FindCandidatesAsync(10, default));
            Assert.Equal([early], await uow.Store.FindCandidatesAsync(1, default));
        }

        Assert.DoesNotContain(future, new[] { early, late });
        Assert.DoesNotContain(failed, new[] { early, late });
    }

    [OracleFact]
    public async Task Concurrent_capture_of_one_record_by_two_connections_has_one_winner()
    {
        var id = await CreateCommittedAsync("doc-1");

        await using var first = await _factory.BeginAsync(default);
        await using var second = await _factory.BeginAsync(default);

        Assert.True(await first.Store.TryAcquireAsync(id, "replica-a", Lease, default));

        // Второй UPDATE ждёт блокировки строки, удерживаемой первой транзакцией.
        var secondCapture = second.Store.TryAcquireAsync(id, "replica-b", Lease, default);
        await Task.Delay(BlockedWait);
        Assert.False(secondCapture.IsCompleted);

        await first.CommitAsync(default);

        // После первой фиксации Oracle заново вычисляет условие WHERE: аренда уже занята.
        Assert.False(await secondCapture);
        await second.CommitAsync(default);

        await using var check = await _factory.BeginAsync(default);
        Assert.Equal("replica-a", (await check.Store.GetRouteAsync(id, default))!.LockedBy);
    }

    [OracleFact]
    public async Task Parallel_capture_by_many_replicas_has_exactly_one_winner()
    {
        var id = await CreateCommittedAsync("doc-1");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var uow = await _factory.BeginAsync(default);
            var acquired = await uow.Store.TryAcquireAsync(id, $"replica-{i}", Lease, default);
            await uow.CommitAsync(default);
            return acquired;
        }));

        Assert.Single(results, r => r);
    }

    [OracleFact]
    public async Task Expired_lease_can_be_taken_over_and_old_owner_is_fenced()
    {
        var id = await CreateCommittedAsync("doc-1");
        await using (var uow = await _factory.BeginAsync(default))
        {
            Assert.True(await uow.Store.TryAcquireAsync(id, "replica-a", Lease, default));
            await uow.CommitAsync(default);
        }

        await db.ExecuteServiceAsync(
            "UPDATE sgn_route_instance SET locked_until = SYSTIMESTAMP - INTERVAL '1' SECOND WHERE id = :id", new { id });

        RouteInstance staleView;
        await using (var uow = await _factory.BeginAsync(default))
        {
            staleView = (await uow.Store.GetRouteAsync(id, default))!;
            Assert.True(await uow.Store.TryAcquireAsync(id, "replica-b", Lease, default));
            await uow.CommitAsync(default);
        }

        await using (var uow = await _factory.BeginAsync(default))
        {
            await Assert.ThrowsAsync<LeaseLostException>(() =>
                uow.Store.UpdateAndReleaseAsync(staleView with { Attempts = 5 }, "replica-a", default));
        }

        await using (var uow = await _factory.BeginAsync(default))
        {
            var route = (await uow.Store.GetRouteAsync(id, default))!;
            Assert.Equal("replica-b", route.LockedBy);
            Assert.Equal(0, route.Attempts);
        }
    }

    [OracleFact]
    public async Task Concurrent_insert_of_the_same_document_returns_null_for_the_loser()
    {
        await using var first = await _factory.BeginAsync(default);
        await using var second = await _factory.BeginAsync(default);
        var now = await first.Store.GetNowAsync(default);

        Assert.NotNull(await first.Store.TryCreateRouteAsync(NewRoute("doc-1", now), default));
        var secondInsert = second.Store.TryCreateRouteAsync(NewRoute("doc-1", now), default);
        await Task.Delay(BlockedWait);
        Assert.False(secondInsert.IsCompleted);

        await first.CommitAsync(default);

        Assert.Null(await secondInsert);
    }

    [OracleFact]
    public async Task Stage_artifact_and_audit_are_written_and_rolled_back_together()
    {
        var id = await CreateCommittedAsync("doc-1");
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();

        await using (var uow = await _factory.BeginAsync(default))
        {
            var now = await uow.Store.GetNowAsync(default);
            var stageId = await uow.Store.CreateStageAsync(new NewStageInstance(id, "legal", "req-1"), default);
            await uow.Store.UpdateStageAsync(new StageInstance
            {
                Id = stageId, RouteInstanceId = id, StageKey = "legal", RequestId = "req-1",
                State = StageState.Done, RequestedAt = now, DeadlineAt = now.AddDays(1), FinishedAt = now,
                Reason = new string('x', 3000),
            }, default);
            await uow.Store.SaveArtifactAsync(new NewSignedArtifact(stageId, content, "abc", "out/a.pdf"), default);
            await uow.Store.AppendAuditAsync(new AuditEntry(id, stageId, "STAGE_DONE", """{"a":1}""", "replica-a"), default);

            var stage = (await uow.Store.GetStageAsync(stageId, default))!;
            Assert.Equal(StageState.Done, stage.State);
            Assert.Equal(now.AddDays(1), stage.DeadlineAt);
            Assert.Equal(2000, stage.Reason!.Length);

            // Освобождено без фиксации: всё вышеперечисленное должно откатиться.
        }

        Assert.Empty(await db.QueryServiceAsync<long>("SELECT id FROM sgn_stage_instance"));
        Assert.Empty(await db.QueryServiceAsync<long>("SELECT id FROM sgn_signed_artifact"));
        Assert.Empty(await db.QueryServiceAsync<long>("SELECT id FROM sgn_audit_event"));
    }

    [OracleFact]
    public async Task Artifact_blob_is_stored_intact()
    {
        var id = await CreateCommittedAsync("doc-1");
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();

        await using (var uow = await _factory.BeginAsync(default))
        {
            var stageId = await uow.Store.CreateStageAsync(new NewStageInstance(id, "legal", "req-1"), default);
            await uow.Store.SaveArtifactAsync(new NewSignedArtifact(stageId, content, "abc", "out/a.pdf"), default);
            await uow.CommitAsync(default);
        }

        var stored = Assert.Single(await db.QueryServiceAsync<(byte[] Content, long SizeBytes)>(
            "SELECT content, size_bytes FROM sgn_signed_artifact"));
        Assert.Equal(content, stored.Content);
        Assert.Equal(content.Length, stored.SizeBytes);
    }

    [OracleFact]
    public async Task Gateway_follows_the_contract()
    {
        await db.AddIncomingDocumentAsync("doc-1", "contract");
        var request = new SignRequest("req-1", "doc-1", SigningKind.Manual, "legal");

        await using (var uow = await _factory.BeginAsync(default))
        {
            Assert.Null(await uow.Gateway.GetRequestAsync("req-1", default));
            await uow.Gateway.CreateRequestAsync(request, default);
            await uow.Gateway.CreateRequestAsync(request, default);
            Assert.Equal(SignRequestStatus.Pending, (await uow.Gateway.GetRequestAsync("req-1", default))!.Status);
            await uow.CommitAsync(default);
        }

        await db.ExecuteExternalAsync("""
            UPDATE stub_sign_request
               SET status = 'SIGNED', signed_at = SYSTIMESTAMP, file_ref = 'out/a.pdf', file_hash = 'ABC', file_size = 42
             WHERE request_id = 'req-1'
            """);

        await using (var uow = await _factory.BeginAsync(default))
        {
            var result = (await uow.Gateway.GetRequestAsync("req-1", default))!;
            Assert.Equal(SignRequestStatus.Signed, result.Status);
            Assert.Equal(("out/a.pdf", "ABC", 42L), (result.FileRef, result.FileHash, result.FileSize));
            Assert.NotNull(result.SignedAt);

            // Отмена запроса в финальном статусе ничего не делает.
            await uow.Gateway.CancelRequestAsync("req-1", default);
            await uow.Gateway.CancelRequestAsync("unknown", default);
            Assert.Equal(SignRequestStatus.Signed, (await uow.Gateway.GetRequestAsync("req-1", default))!.Status);
        }

        Assert.Single(await db.QueryExternalAsync<string>("SELECT request_id FROM stub_sign_request"));
    }

    [OracleFact]
    public async Task Gateway_cancel_rejects_pending_request()
    {
        await db.AddIncomingDocumentAsync("doc-1", "contract");
        await using var uow = await _factory.BeginAsync(default);
        await uow.Gateway.CreateRequestAsync(new SignRequest("req-1", "doc-1", SigningKind.Auto, "seal"), default);

        await uow.Gateway.CancelRequestAsync("req-1", default);

        var result = (await uow.Gateway.GetRequestAsync("req-1", default))!;
        Assert.Equal((SignRequestStatus.Rejected, "CANCELLED"), (result.Status, result.StatusReason));
    }

    [OracleFact]
    public async Task Incoming_source_returns_new_documents_oldest_first()
    {
        await db.ExecuteExternalAsync("""
            INSERT ALL
                INTO stub_incoming_document (doc_id, doc_type, created_at) VALUES ('doc-b', 'contract', SYSTIMESTAMP - INTERVAL '1' MINUTE)
                INTO stub_incoming_document (doc_id, doc_type, created_at) VALUES ('doc-a', 'contract', SYSTIMESTAMP - INTERVAL '2' MINUTE)
                INTO stub_incoming_document (doc_id, doc_type, attributes, content_hash, created_at)
                    VALUES ('doc-c', 'invoice', '{"x":1}', 'h', SYSTIMESTAMP)
            SELECT * FROM dual
            """);
        await CreateCommittedAsync("doc-b");
        var source = new OracleIncomingDocumentSource(db.Options);

        var documents = await source.GetNewAsync(10, default);

        Assert.Equal(["doc-a", "doc-c"], documents.Select(d => d.DocId));
        Assert.Equal(("invoice", """{"x":1}""", "h"), (documents[1].DocType, documents[1].Attributes, documents[1].ContentHash));
        Assert.Single(await source.GetNewAsync(1, default));
    }

    private static NewRouteInstance NewRoute(string docId, DateTimeOffset nextAttemptAt, string? attributes = null) =>
        new(docId, "contract", attributes, null, "contract", 1, RouteStatus.Active, null, nextAttemptAt);

    private static async Task<long> CreateAsync(IUnitOfWork uow, string docId, DateTimeOffset nextAttemptAt) =>
        (await uow.Store.TryCreateRouteAsync(NewRoute(docId, nextAttemptAt), default))!.Value;

    private async Task<long> CreateCommittedAsync(string docId)
    {
        await using var uow = await _factory.BeginAsync(default);
        var id = await CreateAsync(uow, docId, await uow.Store.GetNowAsync(default));
        await uow.CommitAsync(default);
        return id;
    }
}
