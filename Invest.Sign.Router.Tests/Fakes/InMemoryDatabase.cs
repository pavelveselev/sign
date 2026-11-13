using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the Oracle database: service tables, the external signing side and a controlled clock.
/// Units of work apply changes immediately and undo them on rollback. Tests run sequentially, so isolation
/// between concurrent units of work is not modelled.
/// </summary>
public sealed class InMemoryDatabase
{
    private readonly Dictionary<string, (int Remaining, Func<Exception> Error)> _failures = [];
    private long _sequence;

    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public Dictionary<long, RouteInstance> Routes { get; } = [];

    public Dictionary<long, StageInstance> Stages { get; } = [];

    public List<StoredArtifact> Artifacts { get; } = [];

    public List<StoredAudit> Audit { get; } = [];

    /// <summary>External side: rows of the sign requests view, by request id.</summary>
    public Dictionary<string, FakeSignRequest> SignRequests { get; } = [];

    /// <summary>Every call of <c>create_sign_request</c>, kept even when the transaction rolls back.</summary>
    public List<SignRequest> CreateRequestCalls { get; } = [];

    /// <summary>
    /// When false, gateway changes survive a rollback, as if the external side committed on its own.
    /// Used to check that the engine relies on idempotency, not only on the shared transaction.
    /// </summary>
    public bool GatewayTransactional { get; set; } = true;

    /// <summary>Called before every operation with its name (see <see cref="Ops"/>). May throw or mutate state.</summary>
    public Action<string>? BeforeOperation { get; set; }

    public void Advance(TimeSpan delta) => Now += delta;

    /// <summary>Makes the next <paramref name="times"/> calls of <paramref name="operation"/> throw.</summary>
    public void FailOn(string operation, int times = 1, Func<Exception>? error = null) =>
        _failures[operation] = (times, error ?? (() => new IOException($"Injected failure in {operation}.")));

    internal void Before(string operation)
    {
        if (_failures.TryGetValue(operation, out var failure) && failure.Remaining > 0)
        {
            _failures[operation] = failure with { Remaining = failure.Remaining - 1 };
            throw failure.Error();
        }

        BeforeOperation?.Invoke(operation);
    }

    internal long NextId() => ++_sequence;

    // ---- External side: what the signing software would do. ----

    public void Sign(string requestId, string? fileRef = null, string? fileHash = null, long? fileSize = null) =>
        SetResult(requestId, r => r with
        {
            Status = SignRequestStatus.Signed,
            SignedAt = Now,
            FileRef = fileRef,
            FileHash = fileHash,
            FileSize = fileSize,
        });

    public void Reject(string requestId, string reason) =>
        SetResult(requestId, r => r with { Status = SignRequestStatus.Rejected, StatusReason = reason });

    public void Error(string requestId, string reason) =>
        SetResult(requestId, r => r with { Status = SignRequestStatus.Error, StatusReason = reason });

    private void SetResult(string requestId, Func<SignRequestResult, SignRequestResult> change)
    {
        var request = SignRequests[requestId];
        if (request.Result.Status != SignRequestStatus.Pending)
        {
            throw new InvalidOperationException("Contract: a final status never changes.");
        }

        SignRequests[requestId] = request with { Result = change(request.Result) };
    }

    // ---- Queries for assertions. ----

    public RouteInstance RouteByDoc(string docId) => Routes.Values.Single(r => r.ExternalDocId == docId);

    public IReadOnlyList<StageInstance> StagesOf(long routeId) =>
        Stages.Values.Where(s => s.RouteInstanceId == routeId).OrderBy(s => s.Id).ToList();

    public StageInstance CurrentStage(long routeId) =>
        Stages[Routes[routeId].CurrentStageId ?? throw new InvalidOperationException("Route has no current stage.")];

    public string[] AuditTypes(long routeId) =>
        Audit.Where(a => a.Entry.RouteInstanceId == routeId).Select(a => a.Entry.EventType).ToArray();
}

public static class Ops
{
    public const string GetNow = nameof(GetNow);
    public const string TryCreateRoute = nameof(TryCreateRoute);
    public const string FindCandidates = nameof(FindCandidates);
    public const string TryAcquire = nameof(TryAcquire);
    public const string GetRoute = nameof(GetRoute);
    public const string GetStage = nameof(GetStage);
    public const string UpdateAndRelease = nameof(UpdateAndRelease);
    public const string CreateStage = nameof(CreateStage);
    public const string UpdateStage = nameof(UpdateStage);
    public const string SaveArtifact = nameof(SaveArtifact);
    public const string AppendAudit = nameof(AppendAudit);
    public const string CreateRequest = nameof(CreateRequest);
    public const string GetRequest = nameof(GetRequest);
    public const string CancelRequest = nameof(CancelRequest);
    public const string Commit = nameof(Commit);
}

public sealed record StoredArtifact(long Id, NewSignedArtifact Artifact, DateTimeOffset StoredAt);

public sealed record StoredAudit(long Id, AuditEntry Entry, DateTimeOffset CreatedAt);

public sealed record FakeSignRequest(SignRequest Request, SignRequestResult Result, bool Cancelled = false);
