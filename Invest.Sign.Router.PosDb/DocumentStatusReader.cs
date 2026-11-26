using Microsoft.EntityFrameworkCore;

namespace Invest.Sign.Router.PosDb;

/// <summary>Текущий статус документа и история его изменений.</summary>
/// <param name="DocId">Идентификатор документа во внешней системе.</param>
/// <param name="Status">Текущий статус маршрута: <c>ACTIVE</c>, <c>COMPLETED</c>, <c>FAILED</c> и т. д.</param>
/// <param name="StageKey">Текущий (последний) этап; null, если документ ещё не вошёл ни в один этап.</param>
/// <param name="StageState">Состояние текущего этапа.</param>
/// <param name="History">Изменения статусов по возрастанию времени.</param>
public sealed record DocumentStatusHistory(
    string DocId,
    string DocType,
    string Status,
    string? StatusReason,
    string? StageKey,
    string? StageState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DocumentStatusChange> History);

/// <summary>Одно изменение статуса: событие и статусы документа и этапа сразу после него.</summary>
/// <param name="EventType">Событие: <c>ROUTE_CREATED</c>, <c>STAGE_ENTERED</c>, <c>STAGE_DONE</c>, <c>ROUTE_FINISHED</c> и т. д.</param>
/// <param name="Payload">Подробности события, JSON.</param>
/// <param name="Actor">Реплика сервиса, выполнившая переход.</param>
public sealed record DocumentStatusChange(
    long Id,
    DateTimeOffset At,
    string EventType,
    string Status,
    string? StatusReason,
    string? StageKey,
    string? StageState,
    string? Payload,
    string Actor);

/// <summary>Читает текущий статус и историю статусов документа из базы состояния.</summary>
public sealed class DocumentStatusReader(IDocumentDbContextFactory contexts)
{
    /// <summary>Статус и история документа; null, если документа с таким идентификатором нет.</summary>
    public async Task<DocumentStatusHistory?> GetAsync(string docId, CancellationToken cancellationToken)
    {
        await using var db = contexts.CreateDbContext();
        var document = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ExternalDocId == docId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var history = await db.StatusHistory.AsNoTracking()
            .Where(h => h.DocumentId == document.Id)
            .OrderBy(h => h.Id)
            .Select(h => new DocumentStatusChange(
                h.Id, h.CreatedAt, h.EventType, h.Status, h.StatusReason, h.StageKey, h.StageState, h.Payload, h.Actor))
            .ToListAsync(cancellationToken);

        return new DocumentStatusHistory(
            document.ExternalDocId,
            document.DocType,
            document.Status,
            document.StatusReason,
            document.StageKey,
            document.StageState,
            document.CreatedAt,
            document.UpdatedAt,
            history);
    }
}
