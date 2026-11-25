using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.OracleDb;

/// <summary>
/// <see cref="ISigningGateway"/> поверх внешнего контракта (db/contract): процедуры вызываются внутри
/// транзакции единицы работы и никогда не фиксируют её сами.
/// </summary>
internal sealed class OracleSigningGateway(OracleSession session, string externalSchema) : ISigningGateway
{
    public Task CreateRequestAsync(SignRequest request, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"""
            BEGIN
                {externalSchema}.create_sign_request(
                    p_request_id => :request_id,
                    p_doc_id     => :doc_id,
                    p_kind       => :kind,
                    p_signer     => :signer);
            END;
            """,
            cancellationToken,
            P.Str("request_id", request.RequestId),
            P.Str("doc_id", request.ExternalDocId),
            P.Str("kind", request.Kind switch
            {
                SigningKind.Auto => "AUTO",
                SigningKind.Manual => "MANUAL",
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown signing kind."),
            }),
            P.Str("signer", request.Signer));

    public async Task<SignRequestResult?> GetRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<RequestRow>(
            $"""
            SELECT request_id                 AS RequestId,
                   status                     AS Status,
                   status_reason              AS StatusReason,
                   SYS_EXTRACT_UTC(signed_at) AS SignedAt,
                   file_ref                   AS FileRef,
                   file_hash                  AS FileHash,
                   file_size                  AS FileSize
              FROM {externalSchema}.sign_request_v
             WHERE request_id = :request_id
            """,
            new { request_id = requestId },
            cancellationToken);

        return row is null
            ? null
            : new SignRequestResult(
                row.RequestId,
                row.Status switch
                {
                    "PENDING" => SignRequestStatus.Pending,
                    "SIGNED" => SignRequestStatus.Signed,
                    "REJECTED" => SignRequestStatus.Rejected,
                    "ERROR" => SignRequestStatus.Error,
                    _ => throw new InvalidOperationException(
                        $"Sign request '{row.RequestId}' has status '{row.Status}' not defined by the contract."),
                },
                row.StatusReason,
                OracleTime.FromUtc(row.SignedAt),
                row.FileRef,
                row.FileHash,
                row.FileSize);
    }

    public Task CancelRequestAsync(string requestId, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"BEGIN {externalSchema}.cancel_sign_request(p_request_id => :request_id); END;",
            cancellationToken,
            P.Str("request_id", requestId));

    private sealed class RequestRow
    {
        public string RequestId { get; set; } = "";
        public string Status { get; set; } = "";
        public string? StatusReason { get; set; }
        public DateTime? SignedAt { get; set; }
        public string? FileRef { get; set; }
        public string? FileHash { get; set; }
        public long? FileSize { get; set; }
    }
}
