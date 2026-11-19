namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Выполняет этап в ПО подписания. Автоматическое и ручное подписание для сервиса выглядят одинаково
/// (открытый вопрос 5) и различаются только значением <see cref="SignRequest.Kind"/>.
/// Переданный шлюз принадлежит текущей единице работы.
/// </summary>
public interface IStageHandler
{
    /// <summary>Создаёт запрос на подписание. Должен быть идемпотентен по идентификатору запроса.</summary>
    Task StartAsync(ISigningGateway gateway, SignRequest request, CancellationToken cancellationToken);

    /// <summary>Возвращает текущий результат запроса.</summary>
    Task<SignRequestResult> CheckAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken);

    /// <summary>Отменяет запрос после истечения срока этапа.</summary>
    Task CancelAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken);
}

/// <summary>Обработчик по умолчанию: прямое отображение на шлюз подписания.</summary>
public sealed class SigningStageHandler : IStageHandler
{
    public Task StartAsync(ISigningGateway gateway, SignRequest request, CancellationToken cancellationToken) =>
        gateway.CreateRequestAsync(request, cancellationToken);

    public async Task<SignRequestResult> CheckAsync(
        ISigningGateway gateway, string requestId, CancellationToken cancellationToken)
    {
        // Контракт гарантирует, что строка существует сразу после create_sign_request.
        // Её отсутствие считается технической ошибкой и повторяется.
        return await gateway.GetRequestAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException($"Sign request '{requestId}' is not found in the signing software.");
    }

    public Task CancelAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken) =>
        gateway.CancelRequestAsync(requestId, cancellationToken);
}
