using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Контракт ПО подписания (<c>db/contract</c>): <c>create_sign_request</c>, представление запросов на подписание
/// и <c>cancel_sign_request</c>. Вызовы выполняются в транзакции владеющего <see cref="IUnitOfWork"/>.
/// </summary>
public interface ISigningGateway
{
    /// <summary>Создаёт запрос. Идемпотентен по идентификатору запроса.</summary>
    Task CreateRequestAsync(SignRequest request, CancellationToken cancellationToken);

    /// <summary>Текущее состояние запроса; null, если запрос не существует.</summary>
    Task<SignRequestResult?> GetRequestAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>Отменяет запрос. Идемпотентен; для запросов в финальном статусе ничего не делает.</summary>
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
