using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Signing software contract (<c>db/contract</c>): <c>create_sign_request</c>, the sign requests view
/// and <c>cancel_sign_request</c>. Calls run in the transaction of the owning <see cref="IUnitOfWork"/>.
/// </summary>
public interface ISigningGateway
{
    /// <summary>Creates a request. Idempotent by request id.</summary>
    Task CreateRequestAsync(SignRequest request, CancellationToken cancellationToken);

    /// <summary>Current state of a request, null if the request does not exist.</summary>
    Task<SignRequestResult?> GetRequestAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>Cancels a request. Idempotent; no-op for requests in a final status.</summary>
    Task CancelRequestAsync(string requestId, CancellationToken cancellationToken);
}

public sealed record SignRequest(string RequestId, string ExternalDocId, SigningKind Kind, string Signer);

public enum SignRequestStatus
{
    Pending,
    Signed,
    Rejected,
    Error,
}

public sealed record SignRequestResult(
    string RequestId,
    SignRequestStatus Status,
    string? StatusReason = null,
    DateTimeOffset? SignedAt = null,
    string? FileRef = null,
    string? FileHash = null,
    long? FileSize = null);
