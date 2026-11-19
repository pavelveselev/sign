using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Tests.Fakes;

public sealed class FakeUnitOfWorkFactory(InMemoryDatabase db) : IUnitOfWorkFactory
{
    public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IUnitOfWork>(new FakeUnitOfWork(db));
}

/// <summary>Транзакция над <see cref="InMemoryDatabase"/>: изменения применяются сразу и отменяются при откате.</summary>
public sealed class FakeUnitOfWork(InMemoryDatabase db) : IUnitOfWork, IRouteInstanceStore, ISigningGateway
{
    private readonly Stack<Action> _undo = new();
    private bool _completed;

    public IRouteInstanceStore Store => this;

    public ISigningGateway Gateway => this;

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        db.Before(Ops.Commit);
        _undo.Clear();
        _completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            while (_undo.TryPop(out var undo))
            {
                undo();
            }

            _completed = true;
        }

        return ValueTask.CompletedTask;
    }

    // ---- IRouteInstanceStore ----

    public Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken)
    {
        Begin(Ops.GetNow);
        return Task.FromResult(db.Now);
    }

    public Task<long?> TryCreateRouteAsync(NewRouteInstance route, CancellationToken cancellationToken)
    {
        Begin(Ops.TryCreateRoute);
        if (db.Routes.Values.Any(r => r.ExternalDocId == route.ExternalDocId))
        {
            return Task.FromResult<long?>(null);
        }

        var id = db.NextId();
        db.Routes[id] = new RouteInstance
        {
            Id = id,
            ExternalDocId = route.ExternalDocId,
            DocType = route.DocType,
            Attributes = route.Attributes,
            ContentHash = route.ContentHash,
            RouteCode = route.RouteCode,
            RouteVersion = route.RouteVersion,
            Status = route.Status,
            StatusReason = route.StatusReason,
            NextAttemptAt = route.NextAttemptAt,
            CreatedAt = db.Now,
            UpdatedAt = db.Now,
        };
        _undo.Push(() => db.Routes.Remove(id));
        return Task.FromResult<long?>(id);
    }

    public Task<IReadOnlyList<long>> FindCandidatesAsync(int limit, CancellationToken cancellationToken)
    {
        Begin(Ops.FindCandidates);
        IReadOnlyList<long> ids = db.Routes.Values
            .Where(r => r.Status == RouteStatus.Active && r.NextAttemptAt <= db.Now && LeaseFree(r))
            .OrderBy(r => r.NextAttemptAt)
            .ThenBy(r => r.Id)
            .Take(limit)
            .Select(r => r.Id)
            .ToList();
        return Task.FromResult(ids);
    }

    public Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        Begin(Ops.TryAcquire);
        if (!db.Routes.TryGetValue(routeId, out var route) || route.Status != RouteStatus.Active || !LeaseFree(route))
        {
            return Task.FromResult(false);
        }

        Replace(route, route with { LockedBy = owner, LockedUntil = db.Now + lease });
        return Task.FromResult(true);
    }

    public Task<RouteInstance?> GetRouteAsync(long routeId, CancellationToken cancellationToken)
    {
        Begin(Ops.GetRoute);
        return Task.FromResult(db.Routes.GetValueOrDefault(routeId));
    }

    public Task<StageInstance?> GetStageAsync(long stageId, CancellationToken cancellationToken)
    {
        Begin(Ops.GetStage);
        return Task.FromResult(db.Stages.GetValueOrDefault(stageId));
    }

    public Task UpdateAndReleaseAsync(RouteInstance route, string owner, CancellationToken cancellationToken)
    {
        Begin(Ops.UpdateAndRelease);
        if (!db.Routes.TryGetValue(route.Id, out var current) || current.LockedBy != owner)
        {
            throw new LeaseLostException(route.Id, owner);
        }

        Replace(current, current with
        {
            Status = route.Status,
            StatusReason = route.StatusReason,
            CurrentStageId = route.CurrentStageId,
            Attempts = route.Attempts,
            NextAttemptAt = route.NextAttemptAt,
            LockedBy = null,
            LockedUntil = null,
            UpdatedAt = db.Now,
        });
        return Task.CompletedTask;
    }

    public Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken cancellationToken)
    {
        Begin(Ops.CreateStage);
        if (db.Stages.Values.Any(s => s.RequestId == stage.RequestId))
        {
            throw new InvalidOperationException("Unique constraint violated: request_id.");
        }

        var id = db.NextId();
        db.Stages[id] = new StageInstance
        {
            Id = id,
            RouteInstanceId = stage.RouteInstanceId,
            StageKey = stage.StageKey,
            State = StageState.Pending,
            RequestId = stage.RequestId,
        };
        _undo.Push(() => db.Stages.Remove(id));
        return Task.FromResult(id);
    }

    public Task UpdateStageAsync(StageInstance stage, CancellationToken cancellationToken)
    {
        Begin(Ops.UpdateStage);
        var previous = db.Stages[stage.Id];
        db.Stages[stage.Id] = previous with
        {
            State = stage.State,
            RequestedAt = stage.RequestedAt,
            DeadlineAt = stage.DeadlineAt,
            FinishedAt = stage.FinishedAt,
            Reason = stage.Reason,
        };
        _undo.Push(() => db.Stages[stage.Id] = previous);
        return Task.CompletedTask;
    }

    public Task SaveArtifactAsync(NewSignedArtifact artifact, CancellationToken cancellationToken)
    {
        Begin(Ops.SaveArtifact);
        var stored = new StoredArtifact(db.NextId(), artifact, db.Now);
        db.Artifacts.Add(stored);
        _undo.Push(() => db.Artifacts.Remove(stored));
        return Task.CompletedTask;
    }

    public Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Begin(Ops.AppendAudit);
        var stored = new StoredAudit(db.NextId(), entry, db.Now);
        db.Audit.Add(stored);
        _undo.Push(() => db.Audit.Remove(stored));
        return Task.CompletedTask;
    }

    // ---- ISigningGateway (заглушка внешнего контракта) ----

    public Task CreateRequestAsync(SignRequest request, CancellationToken cancellationToken)
    {
        Begin(Ops.CreateRequest);
        db.CreateRequestCalls.Add(request);

        // Идемпотентно по идентификатору запроса.
        if (!db.SignRequests.ContainsKey(request.RequestId))
        {
            db.SignRequests[request.RequestId] = new FakeSignRequest(
                request, new SignRequestResult(request.RequestId, SignRequestStatus.Pending));
            if (db.GatewayTransactional)
            {
                _undo.Push(() => db.SignRequests.Remove(request.RequestId));
            }
        }

        return Task.CompletedTask;
    }

    public Task<SignRequestResult?> GetRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        Begin(Ops.GetRequest);
        return Task.FromResult(db.SignRequests.GetValueOrDefault(requestId)?.Result);
    }

    public Task CancelRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        Begin(Ops.CancelRequest);
        if (db.SignRequests.TryGetValue(requestId, out var request)
            && request.Result.Status == SignRequestStatus.Pending
            && !request.Cancelled)
        {
            db.SignRequests[requestId] = request with { Cancelled = true };
            if (db.GatewayTransactional)
            {
                _undo.Push(() => db.SignRequests[requestId] = request);
            }
        }

        return Task.CompletedTask;
    }

    private bool LeaseFree(RouteInstance route) => route.LockedUntil is null || route.LockedUntil < db.Now;

    private void Replace(RouteInstance previous, RouteInstance next)
    {
        db.Routes[previous.Id] = next;
        _undo.Push(() => db.Routes[previous.Id] = previous);
    }

    private void Begin(string operation)
    {
        EnsureActive();
        db.Before(operation);
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(_completed, this);
}
