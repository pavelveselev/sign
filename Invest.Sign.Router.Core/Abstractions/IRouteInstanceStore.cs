using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Service state: route and stage instances, leases, artifacts and the audit log.
/// All calls run in the transaction of the owning <see cref="IUnitOfWork"/>.
/// </summary>
public interface IRouteInstanceStore
{
    /// <summary>Current database time (<c>SYSTIMESTAMP</c>). The engine never uses the replica clock.</summary>
    Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a route instance. Returns null when an instance with the same external document id
    /// already exists (unique violation): another replica inserted it first.
    /// </summary>
    Task<long?> TryCreateRouteAsync(NewRouteInstance route, CancellationToken cancellationToken);

    /// <summary>
    /// Ids of active instances due for work (<c>next_attempt_at</c> passed) whose lease is free or expired,
    /// ordered by <c>next_attempt_at</c>.
    /// </summary>
    Task<IReadOnlyList<long>> FindCandidatesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Optimistic lease capture: succeeds only if the instance is active and its lease is free or expired.
    /// </summary>
    Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken cancellationToken);

    Task<RouteInstance?> GetRouteAsync(long routeId, CancellationToken cancellationToken);

    Task<StageInstance?> GetStageAsync(long stageId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the mutable route fields (<c>status</c>, <c>status_reason</c>, <c>current_stage_id</c>,
    /// <c>attempts</c>, <c>next_attempt_at</c>) and releases the lease, fenced by <c>locked_by = owner</c>.
    /// </summary>
    /// <exception cref="LeaseLostException">No row matched: the lease belongs to someone else.</exception>
    Task UpdateAndReleaseAsync(RouteInstance route, string owner, CancellationToken cancellationToken);

    /// <summary>Creates a stage instance in <c>PENDING</c>. Returns its id.</summary>
    Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the mutable stage fields. Not fenced by itself: the engine always pairs it with
    /// <see cref="UpdateAndReleaseAsync"/> in the same transaction.
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
    DateTimeOffset NextAttemptAt);

public sealed record NewStageInstance(long RouteInstanceId, string StageKey, string RequestId);

public sealed record NewSignedArtifact(
    long StageInstanceId,
    byte[] Content,
    string Sha256,
    string SourceRef);

/// <summary>An audit log entry. <c>created_at</c> is set by the database.</summary>
public sealed record AuditEntry(
    long RouteInstanceId,
    long? StageInstanceId,
    string EventType,
    string? Payload,
    string Actor);

/// <summary>The lease was taken over by another replica. The step must be discarded without side effects.</summary>
public sealed class LeaseLostException : Exception
{
    public LeaseLostException(long routeId, string owner)
        : base($"Lease on route instance {routeId} is no longer held by '{owner}'.")
    {
        RouteId = routeId;
    }

    public long RouteId { get; }
}
