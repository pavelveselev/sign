using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Tests.Fakes;

/// <summary>Настройки этапа <see cref="FakeSigningStageHandler"/>.</summary>
public sealed record FakeSigningSettings(string Signer);

/// <summary>
/// Типичный обработчик, работающий с внешней системой через запросы: запрос создаётся в транзакции перехода
/// <c>PENDING → REQUESTED</c>, результат опрашивается в <see cref="CheckAsync"/>, по истечении срока запрос отменяется
/// в транзакции перехода в <c>EXPIRED</c>. Внешнюю сторону эмулирует <see cref="InMemoryDatabase"/>, поэтому на нём
/// проверяются аренда, откат записей обработчика, повторы и сроки движка.
/// </summary>
public sealed class FakeSigningStageHandler(InMemoryDatabase db) : IStageHandler
{
    /// <summary>Сам запрос создаётся в транзакции перехода <c>PENDING → REQUESTED</c>.</summary>
    public Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken) =>
        Task.FromResult<StageOutcome>(new StageOutcome.Waiting());

    public async Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken)
    {
        SignRequestResult? result;
        await using (var uow = new FakeUnitOfWork(db))
        {
            result = await uow.GetRequestAsync(stage.RequestId, cancellationToken);
        }

        if (result is null)
        {
            throw new InvalidOperationException($"Sign request '{stage.RequestId}' is not found.");
        }

        return result.Status switch
        {
            SignRequestStatus.Pending => new StageOutcome.Waiting(),
            SignRequestStatus.Signed => new StageOutcome.Done(
                File: string.IsNullOrWhiteSpace(result.FileRef)
                    ? null
                    : new StageFile(result.FileRef, result.FileHash, result.FileSize),
                CompletedAt: result.SignedAt),
            SignRequestStatus.Rejected => new StageOutcome.Rejected(result.StatusReason),
            SignRequestStatus.Error => new StageOutcome.Failed(result.StatusReason),
            _ => throw new InvalidOperationException($"Unknown sign request status {result.Status}."),
        };
    }

    public Task OnTransitionAsync(StageTransition transition, IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var stage = transition.Stage;
        var uow = (FakeUnitOfWork)unitOfWork;
        return (transition.From, transition.To) switch
        {
            (StageState.Pending, StageState.Requested) => uow.CreateRequestAsync(
                new SignRequest(stage.RequestId, stage.Document.ExternalDocId, stage.GetSettings<FakeSigningSettings>().Signer),
                cancellationToken),
            (_, StageState.Expired) => uow.CancelRequestAsync(stage.RequestId, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    public IReadOnlyList<string> ValidateSettings(object? settings) =>
        settings is FakeSigningSettings ? [] : [$"expected {nameof(FakeSigningSettings)}, got {settings?.GetType().Name ?? "null"}"];
}

public sealed record SignRequest(string RequestId, string ExternalDocId, string Signer);

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
