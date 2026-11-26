using System.Text.Json;
using Invest.Sign.Router.PosDb;

namespace Invest.Sign.Router;

/// <summary>HTTP API чтения статусов документов. Только чтение: управлять маршрутами через него нельзя.</summary>
public static class DocumentStatusEndpoints
{
    /// <summary>
    /// <c>GET /api/documents/{docId}/status-history</c>: текущий статус документа и история его изменений.
    /// 200 — документ найден, 404 — сервис ещё не обнаружил документ с таким идентификатором.
    /// </summary>
    public static IEndpointRouteBuilder MapDocumentStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/documents/{docId}/status-history", GetStatusHistoryAsync)
            .WithName("GetDocumentStatusHistory");
        return endpoints;
    }

    private static async Task<IResult> GetStatusHistoryAsync(
        string docId, DocumentStatusService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(docId))
        {
            return Results.BadRequest();
        }

        var history = await service.GetStatusHistoryAsync(docId, cancellationToken);
        return history is null
            ? Results.NotFound()
            : Results.Ok(DocumentStatusHistoryResponse.From(history));
    }
}

/// <summary>Ответ <c>GET /api/documents/{docId}/status-history</c>.</summary>
public sealed record DocumentStatusHistoryResponse(
    string DocId,
    string DocType,
    string Status,
    string? StatusReason,
    string? StageKey,
    string? StageState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DocumentStatusChangeResponse> History)
{
    public static DocumentStatusHistoryResponse From(DocumentStatusHistory h) => new(
        h.DocId, h.DocType, h.Status, h.StatusReason, h.StageKey, h.StageState, h.CreatedAt, h.UpdatedAt,
        h.History.Select(DocumentStatusChangeResponse.From).ToList());
}

/// <summary>Одно изменение статуса. <see cref="Payload"/> отдаётся JSON-объектом, а не строкой.</summary>
public sealed record DocumentStatusChangeResponse(
    DateTimeOffset At,
    string EventType,
    string Status,
    string? StatusReason,
    string? StageKey,
    string? StageState,
    JsonElement? Payload,
    string Actor)
{
    public static DocumentStatusChangeResponse From(DocumentStatusChange c) => new(
        c.At, c.EventType, c.Status, c.StatusReason, c.StageKey, c.StageState, ParsePayload(c.Payload), c.Actor);

    private static JsonElement? ParsePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }
}
