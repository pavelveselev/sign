using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.OracleDb;

/// <summary><see cref="IRouteInstanceStore"/> поверх таблиц <c>SGN_*</c> (db/service).</summary>
internal sealed class OracleRouteInstanceStore(OracleSession session) : IRouteInstanceStore
{
    private const int UniqueConstraintViolated = 1;

    private const string RouteColumns = """
        id                               AS Id,
        external_doc_id                  AS ExternalDocId,
        doc_type                         AS DocType,
        attributes                       AS Attributes,
        content_hash                     AS ContentHash,
        route_code                       AS RouteCode,
        route_version                    AS RouteVersion,
        status                           AS Status,
        status_reason                    AS StatusReason,
        current_stage_id                 AS CurrentStageId,
        attempts                         AS Attempts,
        SYS_EXTRACT_UTC(next_attempt_at) AS NextAttemptAt,
        locked_by                        AS LockedBy,
        SYS_EXTRACT_UTC(locked_until)    AS LockedUntil,
        SYS_EXTRACT_UTC(created_at)      AS CreatedAt,
        SYS_EXTRACT_UTC(updated_at)      AS UpdatedAt
        """;

    private const string StageColumns = """
        id                            AS Id,
        route_instance_id             AS RouteInstanceId,
        stage_key                     AS StageKey,
        state                         AS State,
        request_id                    AS RequestId,
        SYS_EXTRACT_UTC(requested_at) AS RequestedAt,
        SYS_EXTRACT_UTC(deadline_at)  AS DeadlineAt,
        SYS_EXTRACT_UTC(finished_at)  AS FinishedAt,
        reason                        AS Reason
        """;

    /// <summary>Должно совпадать с размером столбца <c>sgn_stage_instance.reason</c>.</summary>
    private const int MaxReasonLength = 2000;

    public async Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken)
    {
        var now = await session.QuerySingleOrDefaultAsync<DateTime>(
            "SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) FROM dual", null, cancellationToken);
        return OracleTime.FromUtc(now);
    }

    public async Task<long?> TryCreateRouteAsync(NewRouteInstance route, CancellationToken cancellationToken)
    {
        var id = P.OutLong("id");
        try
        {
            await session.ExecuteAsync(
                """
                INSERT INTO sgn_route_instance
                    (external_doc_id, doc_type, attributes, content_hash, route_code, route_version,
                     status, status_reason, next_attempt_at)
                VALUES
                    (:external_doc_id, :doc_type, :attributes, :content_hash, :route_code, :route_version,
                     :status, :status_reason, FROM_TZ(:next_attempt_at, '+00:00'))
                RETURNING id INTO :id
                """,
                cancellationToken,
                P.Str("external_doc_id", route.ExternalDocId),
                P.Str("doc_type", route.DocType),
                P.Clob("attributes", route.Attributes),
                P.Str("content_hash", route.ContentHash),
                P.Str("route_code", route.RouteCode),
                P.Int("route_version", route.RouteVersion),
                P.Str("status", route.Status.ToCode()),
                P.Str("status_reason", route.StatusReason),
                P.Utc("next_attempt_at", route.NextAttemptAt),
                id);
        }
        catch (OracleException ex) when (ex.Number == UniqueConstraintViolated)
        {
            // Откатывается только оператор; транзакция остаётся пригодной к использованию.
            return null;
        }

        return ReadLong(id);
    }

    public async Task<IReadOnlyList<long>> FindCandidatesAsync(int limit, CancellationToken cancellationToken)
    {
        var ids = await session.QueryAsync<long>(
            """
            SELECT id
              FROM sgn_route_instance
             WHERE status = 'ACTIVE'
               AND next_attempt_at <= SYSTIMESTAMP
               AND (locked_until IS NULL OR locked_until < SYSTIMESTAMP)
             ORDER BY next_attempt_at, id
             FETCH FIRST :limit ROWS ONLY
            """,
            new { limit },
            cancellationToken);
        return ids.ToList();
    }

    public async Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        var rows = await session.ExecuteAsync(
            """
            UPDATE sgn_route_instance
               SET locked_by = :owner,
                   locked_until = SYSTIMESTAMP + NUMTODSINTERVAL(:lease_seconds, 'SECOND')
             WHERE id = :id
               AND status = 'ACTIVE'
               AND (locked_until IS NULL OR locked_until < SYSTIMESTAMP)
            """,
            cancellationToken,
            P.Str("owner", owner),
            P.Long("lease_seconds", (long)Math.Ceiling(lease.TotalSeconds)),
            P.Long("id", routeId));
        return rows == 1;
    }

    public async Task<RouteInstance?> GetRouteAsync(long routeId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<RouteRow>(
            $"SELECT {RouteColumns} FROM sgn_route_instance WHERE id = :id",
            new { id = routeId },
            cancellationToken);
        return row?.ToModel();
    }

    public async Task<StageInstance?> GetStageAsync(long stageId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<StageRow>(
            $"SELECT {StageColumns} FROM sgn_stage_instance WHERE id = :id",
            new { id = stageId },
            cancellationToken);
        return row?.ToModel();
    }

    public async Task UpdateAndReleaseAsync(RouteInstance route, string owner, CancellationToken cancellationToken)
    {
        var rows = await session.ExecuteAsync(
            """
            UPDATE sgn_route_instance
               SET status = :status,
                   status_reason = :status_reason,
                   current_stage_id = :current_stage_id,
                   attempts = :attempts,
                   next_attempt_at = FROM_TZ(:next_attempt_at, '+00:00'),
                   locked_by = NULL,
                   locked_until = NULL,
                   updated_at = SYSTIMESTAMP
             WHERE id = :id
               AND locked_by = :owner
            """,
            cancellationToken,
            P.Str("status", route.Status.ToCode()),
            P.Str("status_reason", route.StatusReason),
            P.Long("current_stage_id", route.CurrentStageId),
            P.Int("attempts", route.Attempts),
            P.Utc("next_attempt_at", route.NextAttemptAt),
            P.Long("id", route.Id),
            P.Str("owner", owner));

        if (rows != 1)
        {
            throw new LeaseLostException(route.Id, owner);
        }
    }

    public async Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken cancellationToken)
    {
        var id = P.OutLong("id");
        await session.ExecuteAsync(
            """
            INSERT INTO sgn_stage_instance (route_instance_id, stage_key, state, request_id)
            VALUES (:route_instance_id, :stage_key, 'PENDING', :request_id)
            RETURNING id INTO :id
            """,
            cancellationToken,
            P.Long("route_instance_id", stage.RouteInstanceId),
            P.Str("stage_key", stage.StageKey),
            P.Str("request_id", stage.RequestId),
            id);
        return ReadLong(id);
    }

    public async Task UpdateStageAsync(StageInstance stage, CancellationToken cancellationToken)
    {
        var rows = await session.ExecuteAsync(
            """
            UPDATE sgn_stage_instance
               SET state = :state,
                   requested_at = FROM_TZ(:requested_at, '+00:00'),
                   deadline_at = FROM_TZ(:deadline_at, '+00:00'),
                   finished_at = FROM_TZ(:finished_at, '+00:00'),
                   reason = :reason
             WHERE id = :id
            """,
            cancellationToken,
            P.Str("state", stage.State.ToCode()),
            P.Utc("requested_at", stage.RequestedAt),
            P.Utc("deadline_at", stage.DeadlineAt),
            P.Utc("finished_at", stage.FinishedAt),
            P.Str("reason", Truncate(stage.Reason, MaxReasonLength)),
            P.Long("id", stage.Id));

        if (rows != 1)
        {
            throw new InvalidOperationException($"Stage instance {stage.Id} is not found.");
        }
    }

    public Task SaveArtifactAsync(NewSignedArtifact artifact, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO sgn_signed_artifact (stage_instance_id, content, sha256, size_bytes, source_ref)
            VALUES (:stage_instance_id, :content, :sha256, :size_bytes, :source_ref)
            """,
            cancellationToken,
            P.Long("stage_instance_id", artifact.StageInstanceId),
            P.Blob("content", artifact.Content),
            P.Str("sha256", artifact.Sha256),
            P.Long("size_bytes", artifact.Content.LongLength),
            P.Str("source_ref", artifact.SourceRef));

    public Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO sgn_audit_event (route_instance_id, stage_instance_id, event_type, payload, actor)
            VALUES (:route_instance_id, :stage_instance_id, :event_type, :payload, :actor)
            """,
            cancellationToken,
            P.Long("route_instance_id", entry.RouteInstanceId),
            P.Long("stage_instance_id", entry.StageInstanceId),
            P.Str("event_type", entry.EventType),
            P.Clob("payload", entry.Payload),
            P.Str("actor", entry.Actor));

    private static long ReadLong(OracleParameter parameter) =>
        Convert.ToInt64(parameter.Value is global::Oracle.ManagedDataAccess.Types.OracleDecimal d ? d.Value : parameter.Value);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private sealed class RouteRow
    {
        public long Id { get; set; }
        public string ExternalDocId { get; set; } = "";
        public string DocType { get; set; } = "";
        public string? Attributes { get; set; }
        public string? ContentHash { get; set; }
        public string? RouteCode { get; set; }
        public int? RouteVersion { get; set; }
        public string Status { get; set; } = "";
        public string? StatusReason { get; set; }
        public long? CurrentStageId { get; set; }
        public int Attempts { get; set; }
        public DateTime NextAttemptAt { get; set; }
        public string? LockedBy { get; set; }
        public DateTime? LockedUntil { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public RouteInstance ToModel() => new()
        {
            Id = Id,
            ExternalDocId = ExternalDocId,
            DocType = DocType,
            Attributes = Attributes,
            ContentHash = ContentHash,
            RouteCode = RouteCode,
            RouteVersion = RouteVersion,
            Status = RouteStatusExtensions.ParseRouteStatus(Status),
            StatusReason = StatusReason,
            CurrentStageId = CurrentStageId,
            Attempts = Attempts,
            NextAttemptAt = OracleTime.FromUtc(NextAttemptAt),
            LockedBy = LockedBy,
            LockedUntil = OracleTime.FromUtc(LockedUntil),
            CreatedAt = OracleTime.FromUtc(CreatedAt),
            UpdatedAt = OracleTime.FromUtc(UpdatedAt),
        };
    }

    private sealed class StageRow
    {
        public long Id { get; set; }
        public long RouteInstanceId { get; set; }
        public string StageKey { get; set; } = "";
        public string State { get; set; } = "";
        public string RequestId { get; set; } = "";
        public DateTime? RequestedAt { get; set; }
        public DateTime? DeadlineAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? Reason { get; set; }

        public StageInstance ToModel() => new()
        {
            Id = Id,
            RouteInstanceId = RouteInstanceId,
            StageKey = StageKey,
            State = StageStateExtensions.ParseStageState(State),
            RequestId = RequestId,
            RequestedAt = OracleTime.FromUtc(RequestedAt),
            DeadlineAt = OracleTime.FromUtc(DeadlineAt),
            FinishedAt = OracleTime.FromUtc(FinishedAt),
            Reason = Reason,
        };
    }
}
