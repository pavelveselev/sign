using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Discovery;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.OracleDb;

/// <summary>
/// Читает вьюху входящих документов <c>sign_incoming_document_v</c> постранично по <c>(created_at, doc_id)</c>.
/// Таблицы сервиса не используются: состояние документов хранится в другой базе.
/// </summary>
/// <remarks>
/// Метки времени передаются параметрами <c>TIMESTAMP</c> (а не <c>DATE</c>, как сделал бы Dapper для <see cref="DateTime"/>):
/// доли секунды нужны для точного сравнения позиции в ленте.
/// </remarks>
public sealed class OracleIncomingDocumentFeed : IIncomingDocumentFeed
{
    private readonly OracleOptions _options;

    public OracleIncomingDocumentFeed(OracleOptions options)
    {
        options.EnsureValid();
        _options = options;
    }

    public async Task<IReadOnlyList<IncomingDocument>> ReadAsync(
        DateTimeOffset? since, FeedPosition? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var conditions = new List<string>();
        var parameters = new List<OracleParameter> { P.Int("limit", limit) };
        if (since is not null)
        {
            conditions.Add("SYS_EXTRACT_UTC(d.created_at) >= :since");
            parameters.Add(P.Utc("since", since));
        }

        if (after is not null)
        {
            conditions.Add("""
                (SYS_EXTRACT_UTC(d.created_at) > :after_at
                    OR (SYS_EXTRACT_UTC(d.created_at) = :after_at AND d.doc_id > :after_id))
                """);
            parameters.Add(P.Utc("after_at", after.CreatedAt));
            parameters.Add(P.Str("after_id", after.DocId));
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);

        await using var connection = new OracleConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.CommandText = $"""
            SELECT d.doc_id,
                   d.doc_type,
                   d.attributes,
                   d.content_hash,
                   SYS_EXTRACT_UTC(d.created_at) AS created_at
              FROM {_options.ExternalSchema}.sign_incoming_document_v d
             {where}
             ORDER BY SYS_EXTRACT_UTC(d.created_at), d.doc_id
             FETCH FIRST :limit ROWS ONLY
            """;
        command.Parameters.AddRange(parameters.ToArray());

        var result = new List<IncomingDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new IncomingDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                OracleTime.FromUtc(reader.GetDateTime(4))));
        }

        return result;
    }
}
