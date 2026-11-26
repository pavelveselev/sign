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
/// Захват, ограждение аренды и создание экземпляра — явный SQL: условия <c>locked_by = :me</c> и
/// <c>ON CONFLICT DO NOTHING</c> должны быть видны. <c>ON CONFLICT</c> вместо перехвата нарушения уникальности
/// нужен потому, что в Postgres ошибка оператора прерывает всю транзакцию.
/// </para>
/// <para>
/// Время — всегда время базы (<c>clock_timestamp()</c>). Метки времени пишутся в UTC: Npgsql не принимает
/// <see cref="DateTimeOffset"/> с ненулевым смещением для <c>timestamptz</c>.
/// </para>
/// </remarks>
internal sealed class PostgresRouteInstanceStore(SignDbContext db) : IRouteInstanceStore
{
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
        await db.Database.SqlQuery<long>($"""
            SELECT id AS "Value"
              FROM sign_document
             WHERE status = 'ACTIVE'
               AND next_attempt_at <= clock_timestamp()
               AND (locked_until IS NULL OR locked_until < clock_timestamp())
             ORDER BY next_attempt_at, id
             LIMIT {limit}
            """).ToListAsync(cancellationToken);

    public async Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE sign_document
               SET locked_by = {owner}, locked_until = clock_timestamp() + {lease}
             WHERE id = {routeId}
               AND status = 'ACTIVE'
               AND (locked_until IS NULL OR locked_until < clock_timestamp())
            """, cancellationToken);
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
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE sign_document
               SET status = {route.Status.ToCode()},
                   status_reason = {route.StatusReason},
                   current_stage_id = {route.CurrentStageId},
                   attempts = {route.Attempts},
                   next_attempt_at = {Utc(route.NextAttemptAt)},
                   locked_by = NULL,
                   locked_until = NULL,
                   updated_at = clock_timestamp()
             WHERE id = {route.Id}
               AND locked_by = {owner}
            """, cancellationToken);

        if (rows == 0)
        {
            throw new LeaseLostException(route.Id, owner);
        }
    }

    public async Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken cancellationToken)
    {
        // Этап занимает место предыдущего в строке документа; предыдущий остаётся в истории.
        var ids = await db.Database.SqlQuery<long>($"""
            UPDATE sign_document
               SET current_stage_id = nextval('sign_stage_id_seq'),
                   stage_key = {stage.StageKey},
                   stage_state = 'PENDING',
                   request_id = {stage.RequestId},
                   stage_requested_at = NULL,
                   stage_deadline_at = NULL,
                   stage_finished_at = NULL,
                   stage_reason = NULL,
                   stage_data = NULL,
                   updated_at = clock_timestamp()
             WHERE id = {stage.RouteInstanceId}
            RETURNING current_stage_id AS "Value"
            """).ToListAsync(cancellationToken);

        return ids.Count == 1
            ? ids[0]
            : throw new InvalidOperationException($"Route instance {stage.RouteInstanceId} is not found.");
    }

    public async Task UpdateStageAsync(StageInstance stage, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            UPDATE sign_document
               SET stage_state = {stage.State.ToCode()},
                   stage_requested_at = {Utc(stage.RequestedAt)},
                   stage_deadline_at = {Utc(stage.DeadlineAt)},
                   stage_finished_at = {Utc(stage.FinishedAt)},
                   stage_reason = {stage.Reason},
                   stage_data = {stage.Data},
                   updated_at = clock_timestamp()
             WHERE current_stage_id = {stage.Id}
            """, cancellationToken);

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
