namespace Invest.Sign.Router.Core.Model;

/// <summary>Экземпляр маршрута: один на документ. Соответствует <c>SGN_ROUTE_INSTANCE</c>.</summary>
public sealed record RouteInstance
{
    public required long Id { get; init; }

    public required string ExternalDocId { get; init; }

    public required string DocType { get; init; }

    /// <summary>Атрибуты документа в виде JSON-объекта, могут быть null.</summary>
    public string? Attributes { get; init; }

    public string? ContentHash { get; init; }

    /// <summary>Код маршрута, фиксируемый при старте. Null, если маршрут не определён (например, неизвестный тип документа).</summary>
    public string? RouteCode { get; init; }

    /// <summary>Версия маршрута, фиксируемая при старте. Null, если маршрут не определён.</summary>
    public int? RouteVersion { get; init; }

    public required RouteStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public long? CurrentStageId { get; init; }

    /// <summary>Число подряд идущих технических ошибок.</summary>
    public int Attempts { get; init; }

    public required DateTimeOffset NextAttemptAt { get; init; }

    public string? LockedBy { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DocumentContext ToDocumentContext() =>
        new(ExternalDocId, DocType, Attributes, ContentHash);
}
