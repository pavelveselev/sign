namespace Invest.Sign.Router.Core.Model;

/// <summary>Этап экземпляра маршрута, создаётся при входе в этап. Соответствует <c>SGN_STAGE_INSTANCE</c>.</summary>
public sealed record StageInstance
{
    public required long Id { get; init; }

    public required long RouteInstanceId { get; init; }

    public required string StageKey { get; init; }

    public required StageState State { get; init; }

    /// <summary>Идентификатор запроса на подписание, генерируемый сервисом. Уникален.</summary>
    public required string RequestId { get; init; }

    public DateTimeOffset? RequestedAt { get; init; }

    public DateTimeOffset? DeadlineAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Причина отклонения или ошибки.</summary>
    public string? Reason { get; init; }
}
