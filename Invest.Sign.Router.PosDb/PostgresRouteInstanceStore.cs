using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Invest.Sign.Router.PosDb;

/// <summary>
/// Состояние движка в Postgres: экземпляр маршрута и текущий этап — строка <c>sign_document</c>,
/// журнал — <c>sign_document_status_history</c>, подписи — <c>sign_signature</c>.
/// </summary>
/// <remarks>
/// <para>
/// Захват, ограждение аренды и переходы — <c>ExecuteUpdate</c> с условием в <c>Where</c>
/// (<c>locked_by = owner</c>, свободная аренда); результат определяется числом затронутых строк.
/// </para>
/// <para>
/// Явный SQL остался в двух местах. Создание экземпляра — <c>INSERT … ON CONFLICT DO NOTHING RETURNING</c>,
/// которого нет в EF; перехват нарушения уникальности не годится, потому что в Postgres ошибка оператора
/// прерывает всю транзакцию. Время базы — <c>SELECT clock_timestamp()</c>: в EF нет запроса без таблицы.
/// </para>
/// <para>
/// Время — всегда время базы (<see cref="SignDbContext.ClockTimestamp"/>, а не <c>now()</c>). Метки времени
/// пишутся в UTC: Npgsql не принимает <see cref="DateTimeOffset"/> с ненулевым смещением для <c>timestamptz</c>.
/// </para>
/// </remarks>
internal sealed class PostgresRouteInstanceStore(SignDbContext db) : IRouteInstanceStore
{
    // Константы, а не параметры: так они попадают в SQL литералами.
    private const string Active = "ACTIVE";
    private const string Pending = "PENDING";

    public async Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"")
            .ToListAsync(cancellationToken))
        .Single();

    public async Task<long?> TryCreateRouteAsync(NewRouteInstance route, CancellationToken cancellationToken)
    {
        var ids = await db.Database.SqlQuery<long>($"""
            INSERT INTO sign_document (
                external_doc_id, doc_type, attributes, content_hash, source_created_at,
                route_code, route_version, status, status_reason, attempts, next_attempt_at)
            VALUES (
                {route.ExternalDocId}, {route.DocType}, {route.Attributes}, {route.ContentHash}, {Utc(route.SourceCreatedAt)},
                {route.RouteCode}, {route.RouteVersion}, {route.Status.ToCode()}, {route.StatusReason}, 0,
                {Utc(route.NextAttemptAt)})
            ON CONFLICT (external_doc_id) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : ids[0];
    }

    public async Task<IReadOnlyList<long>> FindCandidatesAsync(int limit, CancellationToken cancellationToken) =>
        await db.Documents
            .Where(d => d.Status == Active
                && d.NextAttemptAt <= SignDbContext.ClockTimestamp()
                && (d.LockedUntil == null || d.LockedUntil < SignDbContext.ClockTimestamp()))
            .OrderBy(d => d.NextAttemptAt).ThenBy(d => d.Id)
            .Select(d => d.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        var rows = await db.Documents
            .Where(d => d.Id == routeId
                && d.Status == Active
                && (d.LockedUntil == null || d.LockedUntil < SignDbContext.ClockTimestamp()))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LockedBy, owner)
                .SetProperty(d => d.LockedUntil, d => SignDbContext.ClockTimestamp() + lease),
                cancellationToken);
        return rows == 1;
    }

    public async Task<RouteInstance?> GetRouteAsync(long routeId, CancellationToken cancellationToken)
    {
        var row = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == routeId, cancellationToken);
        return row is null ? null : ToRoute(row);
    }

    public async Task<StageInstance?> GetStageAsync(long stageId, CancellationToken cancellationToken)
    {
        var row = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.CurrentStageId == stageId, cancellationToken);
        return row is null ? null : ToStage(row);
    }

    public async Task UpdateAndReleaseAsync(RouteInstance route, string owner, CancellationToken cancellationToken)
    {
        var status = route.Status.ToCode();
        var nextAttemptAt = Utc(route.NextAttemptAt);
        var rows = await db.Documents
            .Where(d => d.Id == route.Id && d.LockedBy == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.StatusReason, route.StatusReason)
                .SetProperty(d => d.CurrentStageId, route.CurrentStageId)
                .SetProperty(d => d.Attempts, route.Attempts)
                .SetProperty(d => d.NextAttemptAt, nextAttemptAt)
                .SetProperty(d => d.LockedBy, (string?)null)
                .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(d => d.UpdatedAt, d => SignDbContext.ClockTimestamp()),
                cancellationToken);

        if (rows == 0)
        {
            throw new LeaseLostException(route.Id, owner);
        }
    }

    public async Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken cancellationToken)
    {
        // Этап занимает место предыдущего в строке документа; предыдущий остаётся в истории.
        var document = db.Documents.Where(d => d.Id == stage.RouteInstanceId);
        var rows = await document.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.CurrentStageId, d => SignDbContext.NextVal(SignDbContext.StageIdSequence))
            .SetProperty(d => d.StageKey, stage.StageKey)
            .SetProperty(d => d.StageState, Pending)
            .SetProperty(d => d.RequestId, stage.RequestId)
            .SetProperty(d => d.StageRequestedAt, (DateTimeOffset?)null)
            .SetProperty(d => d.StageDeadlineAt, (DateTimeOffset?)null)
            .SetProperty(d => d.StageFinishedAt, (DateTimeOffset?)null)
            .SetProperty(d => d.StageReason, (string?)null)
            .SetProperty(d => d.StageData, (string?)null)
            .SetProperty(d => d.UpdatedAt, d => SignDbContext.ClockTimestamp()),
            cancellationToken);

        if (rows == 0)
        {
            throw new InvalidOperationException($"Route instance {stage.RouteInstanceId} is not found.");
        }

        // Строка заблокирована обновлением до конца транзакции: читается только что присвоенный id.
        return await document.Select(d => d.CurrentStageId!.Value).SingleAsync(cancellationToken);
    }

    public async Task UpdateStageAsync(StageInstance stage, CancellationToken cancellationToken)
    {
        var state = stage.State.ToCode();
        var requestedAt = Utc(stage.RequestedAt);
        var deadlineAt = Utc(stage.DeadlineAt);
        var finishedAt = Utc(stage.FinishedAt);
        var rows = await db.Documents
            .Where(d => d.CurrentStageId == stage.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.StageState, state)
                .SetProperty(d => d.StageRequestedAt, requestedAt)
                .SetProperty(d => d.StageDeadlineAt, deadlineAt)
                .SetProperty(d => d.StageFinishedAt, finishedAt)
                .SetProperty(d => d.StageReason, stage.Reason)
                .SetProperty(d => d.StageData, stage.Data)
                .SetProperty(d => d.UpdatedAt, d => SignDbContext.ClockTimestamp()),
                cancellationToken);

        if (rows == 0)
        {
            throw new InvalidOperationException($"Stage instance {stage.Id} is not the current stage of any document.");
        }
    }

    public async Task SaveArtifactAsync(NewSignedArtifact artifact, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking()
            .Where(d => d.CurrentStageId == artifact.StageInstanceId)
            .Select(d => new { d.Id, d.StageKey })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Stage instance {artifact.StageInstanceId} is not the current stage of any document.");

        db.Signatures.Add(new SignatureEntity
        {
            DocumentId = document.Id,
            StageId = artifact.StageInstanceId,
            StageKey = document.StageKey!,
            SourceRef = artifact.SourceRef,
            Content = artifact.Content,
            Sha256 = artifact.Sha256,
            SizeBytes = artifact.Content.LongLength,
        });
        await SaveAndDetachAsync(cancellationToken);
    }

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        // Снимок статусов после перехода: движок пишет событие последним в транзакции перехода.
        var document = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == entry.RouteInstanceId, cancellationToken)
            ?? throw new InvalidOperationException($"Route instance {entry.RouteInstanceId} is not found.");

        var current = entry.StageInstanceId is null || entry.StageInstanceId == document.CurrentStageId;
        db.StatusHistory.Add(new DocumentStatusHistoryEntity
        {
            DocumentId = document.Id,
            EventType = entry.EventType,
            Status = document.Status,
            StatusReason = document.StatusReason,
            StageId = entry.StageInstanceId ?? document.CurrentStageId,
            StageKey = current ? document.StageKey : null,
            StageState = current ? document.StageState : null,
            Payload = entry.Payload,
            Actor = entry.Actor,
        });
        await SaveAndDetachAsync(cancellationToken);
    }

    private async Task SaveAndDetachAsync(CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static RouteInstance ToRoute(DocumentEntity d) => new()
    {
        Id = d.Id,
        ExternalDocId = d.ExternalDocId,
        DocType = d.DocType,
        Attributes = d.Attributes,
        ContentHash = d.ContentHash,
        RouteCode = d.RouteCode,
        RouteVersion = d.RouteVersion,
        Status = RouteStatusExtensions.ParseRouteStatus(d.Status),
        StatusReason = d.StatusReason,
        CurrentStageId = d.CurrentStageId,
        Attempts = d.Attempts,
        NextAttemptAt = d.NextAttemptAt,
        LockedBy = d.LockedBy,
        LockedUntil = d.LockedUntil,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };

    private static StageInstance? ToStage(DocumentEntity d) =>
        d is { CurrentStageId: { } id, StageKey: { } key, StageState: { } state, RequestId: { } requestId }
            ? new StageInstance
            {
                Id = id,
                RouteInstanceId = d.Id,
                StageKey = key,
                State = StageStateExtensions.ParseStageState(state),
                RequestId = requestId,
                RequestedAt = d.StageRequestedAt,
                DeadlineAt = d.StageDeadlineAt,
                FinishedAt = d.StageFinishedAt,
                Reason = d.StageReason,
                Data = d.StageData,
            }
            : null;
}
