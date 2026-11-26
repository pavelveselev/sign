using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.IntegrationTests.Infrastructure;
using Invest.Sign.Router.PosDb;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>Хранилище состояния на реальном Postgres: уникальность, аренда, ограждение записи, водяной знак.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresStoreTests(PostgresFixture db) : IAsyncLifetime
{
    private PostgresUnitOfWorkFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return;
        }

        _factory = new PostgresUnitOfWorkFactory(db.Options);
        await db.ResetAsync();
    }

    public async Task DisposeAsync()
    {
        if (DockerAvailability.SkipReason is null)
        {
            await _factory.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Duplicate_document_returns_null_and_keeps_the_transaction_usable()
    {
        await using var uow = await _factory.BeginAsync(default);
        var now = await uow.Store.GetNowAsync(default);

        var first = await uow.Store.TryCreateRouteAsync(NewRoute("doc-1", now), default);
        var second = await uow.Store.TryCreateRouteAsync(NewRoute("doc-1", now), default);
        var third = await uow.Store.TryCreateRouteAsync(NewRoute("doc-2", now), default);
        await uow.CommitAsync(default);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.NotNull(third);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM sign_document"));
    }

    [PostgresFact]
    public async Task Only_one_of_two_connections_acquires_the_lease()
    {
        var id = await CreateAsync("doc-1");

        await using var a = await _factory.BeginAsync(default);
        Assert.True(await a.Store.TryAcquireAsync(id, "replica-a", TimeSpan.FromMinutes(5), default));
        await a.CommitAsync(default);

        await using var b = await _factory.BeginAsync(default);
        Assert.False(await b.Store.TryAcquireAsync(id, "replica-b", TimeSpan.FromMinutes(5), default));
    }

    [PostgresFact]
    public async Task Write_without_the_lease_throws_lease_lost()
    {
        var id = await CreateAsync("doc-1");
        await using (var a = await _factory.BeginAsync(default))
        {
            await a.Store.TryAcquireAsync(id, "replica-a", TimeSpan.FromMinutes(5), default);
            await a.CommitAsync(default);
        }

        await using var b = await _factory.BeginAsync(default);
        var route = (await b.Store.GetRouteAsync(id, default))!;

        await Assert.ThrowsAsync<LeaseLostException>(
            () => b.Store.UpdateAndReleaseAsync(route with { Status = RouteStatus.Completed }, "replica-b", default));
    }

    [PostgresFact]
    public async Task Stage_lives_in_the_document_row_and_history_snapshots_statuses()
    {
        var id = await CreateAsync("doc-1");
        await using (var uow = await _factory.BeginAsync(default))
        {
            await uow.Store.TryAcquireAsync(id, "replica-a", TimeSpan.FromMinutes(5), default);
            var stageId = await uow.Store.CreateStageAsync(new NewStageInstance(id, "sign", "req-1"), default);
            var route = (await uow.Store.GetRouteAsync(id, default))!;
            await uow.Store.UpdateAndReleaseAsync(route with { CurrentStageId = stageId }, "replica-a", default);
            await uow.Store.AppendAuditAsync(new AuditEntry(id, stageId, "STAGE_ENTERED", "{\"stageKey\":\"sign\"}", "replica-a"), default);

            var stage = (await uow.Store.GetStageAsync(stageId, default))!;
            Assert.Equal(("sign", StageState.Pending, "req-1"), (stage.StageKey, stage.State, stage.RequestId));
            await uow.Store.SaveArtifactAsync(new NewSignedArtifact(stageId, [1, 2, 3], "abc", "doc.pdf.sig"), default);
            await uow.CommitAsync(default);
        }

        var history = await new DocumentStatusReader(_factory).GetAsync("doc-1", default);

        Assert.NotNull(history);
        Assert.Equal(("ACTIVE", "sign", "PENDING"), (history.Status, history.StageKey, history.StageState));
        var change = Assert.Single(history.History);
        Assert.Equal(("STAGE_ENTERED", "ACTIVE", "sign", "PENDING", "replica-a"),
            (change.EventType, change.Status, change.StageKey, change.StageState, change.Actor));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT size_bytes FROM sign_signature"));
        Assert.Null(await new DocumentStatusReader(_factory).GetAsync("missing", default));
    }

    [PostgresFact]
    public async Task Known_documents_report_watermark_and_existing_ids()
    {
        var known = new PostgresKnownDocuments(_factory);
        Assert.Null(await known.GetWatermarkAsync(default));

        var t = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        await CreateAsync("doc-1", t);
        await CreateAsync("doc-2", t.AddMinutes(5));

        Assert.Equal(t.AddMinutes(5), await known.GetWatermarkAsync(default));
        Assert.Equal(["doc-2"], await known.FindKnownAsync(["doc-2", "doc-3"], default));
    }

    private async Task<long> CreateAsync(string docId, DateTimeOffset? sourceCreatedAt = null)
    {
        await using var uow = await _factory.BeginAsync(default);
        var now = await uow.Store.GetNowAsync(default);
        var id = await uow.Store.TryCreateRouteAsync(NewRoute(docId, now, sourceCreatedAt), default);
        await uow.CommitAsync(default);
        return id!.Value;
    }

    private static NewRouteInstance NewRoute(string docId, DateTimeOffset now, DateTimeOffset? sourceCreatedAt = null) =>
        new(docId, "document", "{\"file_name\":\"doc.pdf\"}", null, "document", 1,
            RouteStatus.Active, null, now, sourceCreatedAt ?? now);
}
