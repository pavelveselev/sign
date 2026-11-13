namespace Invest.Sign.Router.Core.Model;

/// <summary>A route instance: one per document. Mirrors <c>SGN_ROUTE_INSTANCE</c>.</summary>
public sealed record RouteInstance
{
    public required long Id { get; init; }

    public required string ExternalDocId { get; init; }

    public required string DocType { get; init; }

    /// <summary>Document attributes as a JSON object, may be null.</summary>
    public string? Attributes { get; init; }

    public string? ContentHash { get; init; }

    /// <summary>Route code fixed at start. Null when the route was not resolved (e.g. unknown doc type).</summary>
    public string? RouteCode { get; init; }

    /// <summary>Route version fixed at start. Null when the route was not resolved.</summary>
    public int? RouteVersion { get; init; }

    public required RouteStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public long? CurrentStageId { get; init; }

    /// <summary>Number of consecutive technical failures.</summary>
    public int Attempts { get; init; }

    public required DateTimeOffset NextAttemptAt { get; init; }

    public string? LockedBy { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DocumentContext ToDocumentContext() =>
        new(ExternalDocId, DocType, Attributes, ContentHash);
}
