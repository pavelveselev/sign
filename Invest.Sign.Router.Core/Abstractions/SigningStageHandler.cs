using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Настройки этапа подписания во внешнем ПО: <c>p_kind</c> и <c>p_signer</c>.</summary>
public sealed record SigningStageSettings(SigningKind Kind, string Signer);

/// <summary>
/// Этап подписания во внешнем ПО (<c>db/contract</c>). Автоматическое и ручное подписание для сервиса выглядят одинаково
/// (открытый вопрос 5) и различаются только значением <see cref="SigningStageSettings.Kind"/>.
/// </summary>
/// <remarks>
/// Создание и отмена запроса — вызовы процедур той же базы, поэтому они выполняются в транзакции перехода
/// (<see cref="OnTransitionAsync"/>) после проверки аренды: потерянная аренда не доходит до ПО подписания.
/// </remarks>
public sealed class SigningStageHandler(IUnitOfWorkFactory unitOfWork) : IStageHandler
{
    /// <summary>Сам запрос создаётся в транзакции перехода <c>PENDING → REQUESTED</c>.</summary>
    public Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken) =>
        Task.FromResult<StageOutcome>(new StageOutcome.Waiting());

    public async Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken)
    {
        SignRequestResult? result;
        await using (var uow = await unitOfWork.BeginAsync(cancellationToken))
        {
            result = await uow.Gateway.GetRequestAsync(stage.RequestId, cancellationToken);
        }

        // Контракт гарантирует, что строка существует сразу после create_sign_request.
        // Её отсутствие считается технической ошибкой и повторяется.
        if (result is null)
        {
            throw new InvalidOperationException($"Sign request '{stage.RequestId}' is not found in the signing software.");
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
            SignRequestStatus.Error => new StageOutcome.Failed(result.StatusReason, StatusReasons.SigningError),
            _ => throw new InvalidOperationException($"Unknown sign request status {result.Status}."),
        };
    }

    public Task OnTransitionAsync(StageTransition transition, IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var stage = transition.Stage;
        return (transition.From, transition.To) switch
        {
            (StageState.Pending, StageState.Requested) => unitOfWork.Gateway.CreateRequestAsync(
                new SignRequest(stage.RequestId, stage.Document.ExternalDocId,
                    stage.GetSettings<SigningStageSettings>().Kind, stage.GetSettings<SigningStageSettings>().Signer),
                cancellationToken),
            (_, StageState.Expired) => unitOfWork.Gateway.CancelRequestAsync(stage.RequestId, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    public IReadOnlyList<string> ValidateSettings(object? settings) =>
        settings is SigningStageSettings ? [] : [$"expected {nameof(SigningStageSettings)}, got {settings?.GetType().Name ?? "null"}"];
}
