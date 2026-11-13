namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Executes a stage against the signing software. Automatic and manual signing look the same to the
/// service (open question 5) and differ only by <see cref="SignRequest.Kind"/>.
/// The gateway passed in belongs to the current unit of work.
/// </summary>
public interface IStageHandler
{
    /// <summary>Creates the signing request. Must be idempotent by request id.</summary>
    Task StartAsync(ISigningGateway gateway, SignRequest request, CancellationToken cancellationToken);

    /// <summary>Returns the current result of the request.</summary>
    Task<SignRequestResult> CheckAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken);

    /// <summary>Cancels the request after the stage deadline.</summary>
    Task CancelAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken);
}

/// <summary>Default handler: a direct mapping onto the signing gateway.</summary>
public sealed class SigningStageHandler : IStageHandler
{
    public Task StartAsync(ISigningGateway gateway, SignRequest request, CancellationToken cancellationToken) =>
        gateway.CreateRequestAsync(request, cancellationToken);

    public async Task<SignRequestResult> CheckAsync(
        ISigningGateway gateway, string requestId, CancellationToken cancellationToken)
    {
        // The contract guarantees the row exists right after create_sign_request.
        // Its absence is treated as a technical failure and retried.
        return await gateway.GetRequestAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException($"Sign request '{requestId}' is not found in the signing software.");
    }

    public Task CancelAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken) =>
        gateway.CancelRequestAsync(requestId, cancellationToken);
}
