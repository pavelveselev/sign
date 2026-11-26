using Invest.Sign.Router.Core.Discovery;
using Invest.Sign.Router.IntegrationTests.Infrastructure;
using Invest.Sign.Router.OracleDb;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>Постраничное чтение вьюхи входящих документов по <c>(created_at, doc_id)</c> на реальном Oracle.</summary>
[Collection(OracleCollection.Name)]
public sealed class OracleIncomingDocumentFeedTests(OracleFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => DockerAvailability.SkipReason is null ? db.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [OracleFact]
    public async Task Pages_by_created_at_and_doc_id_with_sub_second_precision()
    {
        // Одинаковое время до микросекунд: позиция в ленте различает строки по doc_id.
        foreach (var id in new[] { "b", "a", "c" })
        {
            await db.ExecuteExternalAsync(
                """
                INSERT INTO stub_incoming_document (doc_id, doc_type, created_at)
                VALUES (:id, 'document', TIMESTAMP '2026-11-01 10:00:00.123456 +00:00')
                """,
                new { id });
        }

        await db.ExecuteExternalAsync(
            """
            INSERT INTO stub_incoming_document (doc_id, doc_type, created_at)
            VALUES ('early', 'document', TIMESTAMP '2026-11-01 10:00:00.123455 +00:00')
            """);

        var feed = new OracleIncomingDocumentFeed(db.Options);

        var first = await feed.ReadAsync(null, null, 3, default);
        Assert.Equal(["early", "a", "b"], first.Select(d => d.DocId));
        Assert.Equal(123456, first[1].CreatedAt.Ticks % TimeSpan.TicksPerSecond / 10);

        var second = await feed.ReadAsync(null, new FeedPosition(first[^1].CreatedAt, first[^1].DocId), 3, default);
        Assert.Equal(["c"], second.Select(d => d.DocId));

        var since = await feed.ReadAsync(first[1].CreatedAt, null, 10, default);
        Assert.Equal(["a", "b", "c"], since.Select(d => d.DocId));
    }
}
