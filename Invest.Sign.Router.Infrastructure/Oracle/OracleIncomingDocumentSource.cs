using Dapper;
using Invest.Sign.Router.Core.Abstractions;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.Infrastructure.Oracle;

/// <summary>
/// Reads new documents from the external incoming documents view: rows without a route instance (anti-join),
/// oldest first.
/// </summary>
public sealed class OracleIncomingDocumentSource : IIncomingDocumentSource
{
    private readonly OracleOptions _options;

    public OracleIncomingDocumentSource(OracleOptions options)
    {
        options.EnsureValid();
        _options = options;
    }

    public async Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT d.doc_id                      AS DocId,
                   d.doc_type                    AS DocType,
                   d.attributes                  AS Attributes,
                   d.content_hash                AS ContentHash,
                   SYS_EXTRACT_UTC(d.created_at) AS CreatedAt
              FROM {_options.ExternalSchema}.sign_incoming_document_v d
             WHERE NOT EXISTS (SELECT 1 FROM sgn_route_instance r WHERE r.external_doc_id = d.doc_id)
             ORDER BY d.created_at, d.doc_id
             FETCH FIRST :limit ROWS ONLY
            """,
            new { limit },
            commandTimeout: _options.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));

        return rows
            .Select(r => new IncomingDocument(r.DocId, r.DocType, r.Attributes, r.ContentHash, OracleTime.FromUtc(r.CreatedAt)))
            .ToList();
    }

    private sealed class Row
    {
        public string DocId { get; set; } = "";
        public string DocType { get; set; } = "";
        public string? Attributes { get; set; }
        public string? ContentHash { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
