using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Состояние сервиса: экземпляры маршрутов и этапов, аренды, артефакты и журнал аудита.
/// Все вызовы выполняются в транзакции владеющего <see cref="IUnitOfWork"/>.
/// </summary>
public interface IRouteInstanceStore
{
    /// <summary>Текущее время базы данных (<c>SYSTIMESTAMP</c>). Движок никогда не использует часы реплики.</summary>
    Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Вставляет экземпляр маршрута. Возвращает null, если экземпляр с тем же внешним идентификатором документа
    /// уже существует (нарушение уникальности): его первой вставила другая реплика.
    /// </summary>
    Task<long?> TryCreateRouteAsync(NewRouteInstance route, CancellationToken cancellationToken);

    /// <summary>
    /// Идентификаторы активных экземпляров, готовых к работе (<c>next_attempt_at</c> наступил), со свободной или истёкшей арендой,
    /// упорядоченные по <c>next_attempt_at</c>.
    /// </summary>
    Task<IReadOnlyList<long>> FindCandidatesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Оптимистичный захват аренды: успешен, только если экземпляр активен, а его аренда свободна или истекла.
    /// </summary>
    Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken cancellationToken);

    Task<RouteInstance?> GetRouteAsync(long routeId, CancellationToken cancellationToken);

    Task<StageInstance?> GetStageAsync(long stageId, CancellationToken cancellationToken);

    /// <summary>
    /// Записывает изменяемые поля маршрута (<c>status</c>, <c>status_reason</c>, <c>current_stage_id</c>,
    /// <c>attempts</c>, <c>next_attempt_at</c>) и освобождает аренду; запись ограничена условием <c>locked_by = owner</c>.
    /// </summary>
    /// <exception cref="LeaseLostException">Ни одна строка не подошла: аренда принадлежит кому-то другому.</exception>
    Task UpdateAndReleaseAsync(RouteInstance route, string owner, CancellationToken cancellationToken);

    /// <summary>Создаёт экземпляр этапа в состоянии <c>PENDING</c>. Возвращает его идентификатор.</summary>
    Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken cancellationToken);

    /// <summary>
    /// Записывает изменяемые поля этапа. Сам по себе не защищён проверкой аренды: движок всегда вызывает его вместе с
    /// <see cref="UpdateAndReleaseAsync"/> в той же транзакции.
    /// </summary>
    Task UpdateStageAsync(StageInstance stage, CancellationToken cancellationToken);

    Task SaveArtifactAsync(NewSignedArtifact artifact, CancellationToken cancellationToken);

    Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed record NewRouteInstance(
    string ExternalDocId,
    string DocType,
    string? Attributes,
    string? ContentHash,
    string? RouteCode,
    int? RouteVersion,
    RouteStatus Status,
    string? StatusReason,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? SourceCreatedAt = null);

public sealed record NewStageInstance(long RouteInstanceId, string StageKey, string RequestId);

public sealed record NewSignedArtifact(
    long StageInstanceId,
    byte[] Content,
    string Sha256,
    string SourceRef);

/// <summary>Запись журнала аудита. <c>created_at</c> устанавливается базой данных.</summary>
public sealed record AuditEntry(
    long RouteInstanceId,
    long? StageInstanceId,
    string EventType,
    string? Payload,
    string Actor);

/// <summary>Аренду перехватила другая реплика. Шаг должен быть отброшен без побочных эффектов.</summary>
public sealed class LeaseLostException : Exception
{
    public LeaseLostException(long routeId, string owner)
        : base($"Lease on route instance {routeId} is no longer held by '{owner}'.")
    {
        RouteId = routeId;
    }

    public long RouteId { get; }
}
