using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Tests.Fakes;

/// <summary>
/// База данных в памяти: состояние сервиса, эмуляция внешней системы подписания и управляемые часы.
/// Единицы работы применяют изменения сразу и отменяют их при откате. Тесты выполняются последовательно, поэтому изоляция
/// между параллельными единицами работы не моделируется.
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

    /// <summary>Внешняя сторона: строки представления запросов на подписание по идентификатору запроса.</summary>
    public Dictionary<string, FakeSignRequest> SignRequests { get; } = [];

    /// <summary>Каждый вызов создания запроса, сохраняется даже при откате транзакции.</summary>
    public List<SignRequest> CreateRequestCalls { get; } = [];

    /// <summary>
    /// Если false, изменения внешней системы подписания переживают откат, как будто внешняя сторона зафиксировала их сама.
    /// Используется для проверки того, что движок опирается на идемпотентность, а не только на общую транзакцию.
    /// </summary>
    public bool SigningSideTransactional { get; set; } = true;

    /// <summary>Вызывается перед каждой операцией с её именем (см. <see cref="Ops"/>). Может выбросить исключение или изменить состояние.</summary>
    public Action<string>? BeforeOperation { get; set; }

    public void Advance(TimeSpan delta) => Now += delta;

    /// <summary>Заставляет следующие <paramref name="times"/> вызовов <paramref name="operation"/> выбросить исключение.</summary>
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

    // ---- Внешняя сторона: то, что делало бы ПО подписания. ----

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

    // ---- Запросы для проверок. ----

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
