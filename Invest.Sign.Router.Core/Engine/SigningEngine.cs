using System.Security.Cryptography;
using System.Text.Json;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Core.Engine;

/// <summary>
/// Route engine. One <see cref="TickAsync"/> discovers new documents and processes due route instances
/// one by one: capture the lease, perform exactly one transition, release the lease in the same transaction.
/// </summary>
/// <remarks>
/// Business outcomes (rejection, timeout, signing error, artifact mismatch, condition error) are ordinary
/// transitions. Any other exception from a step is a technical failure: attempts are incremented and a retry
/// is scheduled with exponential backoff in a separate transaction. A lost lease discards the step.
/// </remarks>
public sealed class SigningEngine
{
    private const int MaxErrorLength = 2000;

    private readonly IUnitOfWorkFactory _unitOfWork;
    private readonly IIncomingDocumentSource _incoming;
    private readonly ISignedFileSource _files;
    private readonly IStageHandler _handler;
    private readonly RouteRegistry _routes;
    private readonly EngineOptions _options;

    public SigningEngine(
        IUnitOfWorkFactory unitOfWork,
        IIncomingDocumentSource incoming,
        ISignedFileSource files,
        IStageHandler handler,
        RouteRegistry routes,
        EngineOptions options,
        string replicaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid engine options: {string.Join(" ", errors)}", nameof(options));
        }

        _unitOfWork = unitOfWork;
        _incoming = incoming;
        _files = files;
        _handler = handler;
        _routes = routes;
        _options = options;
        ReplicaId = replicaId;
    }

    /// <summary>Lease owner and audit actor.</summary>
    public string ReplicaId { get; }

    /// <summary>
    /// Discovers new documents, then processes due instances sequentially.
    /// Cancellation is checked between instances; a step in progress receives the same token.
    /// </summary>
    public async Task<TickResult> TickAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);

        IReadOnlyList<long> candidates;
        await using (var uow = await _unitOfWork.BeginAsync(cancellationToken))
        {
            candidates = await uow.Store.FindCandidatesAsync(_options.BatchSize, cancellationToken);
        }

        var steps = new List<StepResult>(candidates.Count);
        foreach (var routeId in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            steps.Add(await ProcessAsync(routeId, cancellationToken));
        }

        return new TickResult(discovery, steps);
    }

    /// <summary>
    /// Creates route instances for new incoming documents. A document inserted concurrently by another
    /// replica is counted as already existing. A document of an unknown type gets a <c>FAILED</c> instance.
    /// </summary>
    public async Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IncomingDocument> documents;
        try
        {
            documents = await _incoming.GetNewAsync(_options.DiscoveryBatchSize, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscoveryResult(0, 0, [], ex);
        }

        var created = 0;
        var existing = 0;
        var errors = new List<(string, Exception)>();
        foreach (var document in documents)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (await CreateRouteAsync(document, cancellationToken))
                {
                    created++;
                }
                else
                {
                    existing++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add((document.DocId, ex));
            }
        }

        return new DiscoveryResult(created, existing, errors);
    }

    /// <summary>Captures the instance, performs one step and releases it.</summary>
    public async Task<StepResult> ProcessAsync(long routeId, CancellationToken cancellationToken)
    {
        await using (var uow = await _unitOfWork.BeginAsync(cancellationToken))
        {
            if (!await uow.Store.TryAcquireAsync(routeId, ReplicaId, _options.LeaseDuration, cancellationToken))
            {
                return new StepResult(routeId, StepOutcome.NotAcquired);
            }

            await uow.CommitAsync(cancellationToken);
        }

        try
        {
            await StepAsync(routeId, cancellationToken);
            return new StepResult(routeId, StepOutcome.Advanced);
        }
        catch (LeaseLostException ex)
        {
            return new StepResult(routeId, StepOutcome.LeaseLost, ex);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return await RecordTechnicalFailureAsync(routeId, ex, cancellationToken);
        }
    }

    private async Task<bool> CreateRouteAsync(IncomingDocument document, CancellationToken ct)
    {
        await using var uow = await _unitOfWork.BeginAsync(ct);
        var store = uow.Store;
        var now = await store.GetNowAsync(ct);
        var route = _routes.FindLatest(document.DocType);

        var instance = new NewRouteInstance(
            document.DocId,
            document.DocType,
            document.Attributes,
            document.ContentHash,
            route?.Code,
            route?.Version,
            route is null ? RouteStatus.Failed : RouteStatus.Active,
            route is null ? StatusReasons.UnknownDocType : null,
            now);

        var id = await store.TryCreateRouteAsync(instance, ct);
        if (id is null)
        {
            return false;
        }

        await AuditAsync(store, id.Value, null, AuditEventTypes.RouteCreated, new
        {
            docId = document.DocId,
            docType = document.DocType,
            routeCode = route?.Code,
            routeVersion = route?.Version,
        }, ct);

        if (route is null)
        {
            await AuditRouteFinishedAsync(store, id.Value, RouteStatus.Failed, StatusReasons.UnknownDocType, null, ct);
        }

        await uow.CommitAsync(ct);
        return true;
    }

    private async Task StepAsync(long routeId, CancellationToken ct)
    {
        // Phase 1: read state and, for a requested stage, the result of the request.
        DateTimeOffset readAt;
        RouteInstance route;
        StageInstance? stage = null;
        SignRequestResult? check = null;
        RouteDefinition? definition;
        await using (var uow = await _unitOfWork.BeginAsync(ct))
        {
            readAt = await uow.Store.GetNowAsync(ct);
            route = await uow.Store.GetRouteAsync(routeId, ct) ?? throw new LeaseLostException(routeId, ReplicaId);
            if (route.LockedBy != ReplicaId || route.Status != RouteStatus.Active)
            {
                throw new LeaseLostException(routeId, ReplicaId);
            }

            if (route.CurrentStageId is { } stageId)
            {
                stage = await uow.Store.GetStageAsync(stageId, ct)
                    ?? throw new InvalidOperationException($"Stage instance {stageId} of route {routeId} is not found.");
            }

            definition = route is { RouteCode: { } code, RouteVersion: { } version } ? _routes.Find(code, version) : null;
            if (definition is not null && stage?.State == StageState.Requested)
            {
                check = await _handler.CheckAsync(uow.Gateway, stage.RequestId, ct);
            }
        }

        // Phase 2: one transition, each in its own transaction.
        if (definition is null)
        {
            await FinishAsync(route, RouteStatus.Failed, StatusReasons.RouteNotFound,
                stage, StageState.Failed, StatusReasons.RouteNotFound,
                new { routeCode = route.RouteCode, routeVersion = route.RouteVersion }, ct: ct);
            return;
        }

        switch (stage?.State)
        {
            case null or StageState.Done:
                await EnterNextStageAsync(route, definition, stage, ct);
                break;
            case StageState.Pending:
                await StartStageAsync(route, definition, stage, ct);
                break;
            case StageState.Requested:
                await HandleCheckAsync(route, stage, check!, readAt, ct);
                break;
            default:
                throw new InvalidOperationException(
                    $"Stage instance {stage.Id} is {stage.State.ToCode()} while route {routeId} is active.");
        }
    }

    private async Task EnterNextStageAsync(
        RouteInstance route, RouteDefinition definition, StageInstance? current, CancellationToken ct)
    {
        StageSelection selection;
        try
        {
            selection = definition.SelectNextStage(current?.StageKey, route.ToDocumentContext());
        }
        catch (Exception ex) when (ex is StageConditionException or DocumentAttributeException)
        {
            await FinishAsync(route, RouteStatus.Failed, StatusReasons.ConditionError,
                details: new { stageKey = (ex as StageConditionException)?.StageKey, error = Truncate(ex.Message) },
                ct: ct);
            return;
        }

        await InTransactionAsync(async (store, _, now) =>
        {
            if (selection.Next is null)
            {
                await store.UpdateAndReleaseAsync(
                    route with { Status = RouteStatus.Completed, StatusReason = null, Attempts = 0, NextAttemptAt = now },
                    ReplicaId, ct);
                await AuditSkippedAsync(store, route.Id, selection, ct);
                await AuditRouteFinishedAsync(store, route.Id, RouteStatus.Completed, null, null, ct);
                return;
            }

            var requestId = Guid.NewGuid().ToString("N");
            var stageId = await store.CreateStageAsync(new NewStageInstance(route.Id, selection.Next.Key, requestId), ct);
            await store.UpdateAndReleaseAsync(
                route with { CurrentStageId = stageId, Attempts = 0, NextAttemptAt = now },
                ReplicaId, ct);
            await AuditSkippedAsync(store, route.Id, selection, ct);
            await AuditAsync(store, route.Id, stageId, AuditEventTypes.StageEntered,
                new { stageKey = selection.Next.Key, requestId }, ct);
        }, ct);
    }

    private async Task StartStageAsync(
        RouteInstance route, RouteDefinition definition, StageInstance stage, CancellationToken ct)
    {
        var stageDefinition = definition.FindStage(stage.StageKey)
            ?? throw new InvalidOperationException($"Stage '{stage.StageKey}' is not defined in route {definition.Key}.");

        await InTransactionAsync(async (store, gateway, now) =>
        {
            // Fence first: a lost lease must not reach the signing software.
            await store.UpdateAndReleaseAsync(
                route with { Attempts = 0, NextAttemptAt = now + _options.PollInterval },
                ReplicaId, ct);

            var request = new SignRequest(stage.RequestId, route.ExternalDocId, stageDefinition.Kind, stageDefinition.Signer);
            await _handler.StartAsync(gateway, request, ct);

            var deadline = now + stageDefinition.Timeout;
            await store.UpdateStageAsync(
                stage with { State = StageState.Requested, RequestedAt = now, DeadlineAt = deadline },
                ct);
            await AuditAsync(store, route.Id, stage.Id, AuditEventTypes.StageRequested, new
            {
                stageKey = stage.StageKey,
                requestId = stage.RequestId,
                kind = stageDefinition.Kind.ToString().ToUpperInvariant(),
                signer = stageDefinition.Signer,
                deadlineAt = deadline,
            }, ct);
        }, ct);
    }

    private async Task HandleCheckAsync(
        RouteInstance route, StageInstance stage, SignRequestResult check, DateTimeOffset checkedAt, CancellationToken ct)
    {
        // The result is evaluated before the deadline: a signature that arrived late still counts.
        switch (check.Status)
        {
            case SignRequestStatus.Signed:
                await CompleteStageAsync(route, stage, check, ct);
                return;

            case SignRequestStatus.Rejected:
                await FinishAsync(route, RouteStatus.Rejected, null,
                    stage, StageState.Rejected, check.StatusReason, ct: ct);
                return;

            case SignRequestStatus.Error:
                await FinishAsync(route, RouteStatus.Failed, StatusReasons.SigningError,
                    stage, StageState.Failed, check.StatusReason, ct: ct);
                return;

            case SignRequestStatus.Pending:
                break;

            default:
                throw new InvalidOperationException($"Unknown sign request status {check.Status}.");
        }

        var deadline = stage.DeadlineAt
            ?? throw new InvalidOperationException($"Requested stage instance {stage.Id} has no deadline.");

        if (checkedAt < deadline)
        {
            await InTransactionAsync(async (store, _, now) =>
            {
                var next = now + _options.PollInterval;
                await store.UpdateAndReleaseAsync(
                    route with { Attempts = 0, NextAttemptAt = next < deadline ? next : deadline },
                    ReplicaId, ct);
            }, ct);
            return;
        }

        await FinishAsync(route, RouteStatus.Expired, null,
            stage, StageState.Expired, null,
            new { deadlineAt = deadline },
            beforeStageWrite: gateway => _handler.CancelAsync(gateway, stage.RequestId, ct),
            ct: ct);
    }

    private async Task CompleteStageAsync(
        RouteInstance route, StageInstance stage, SignRequestResult check, CancellationToken ct)
    {
        NewSignedArtifact? artifact = null;
        if (!string.IsNullOrWhiteSpace(check.FileRef))
        {
            // Read outside of any transaction: the share may be slow.
            var content = await _files.ReadAsync(check.FileRef, ct);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));

            var hashMismatch = !string.IsNullOrWhiteSpace(check.FileHash)
                && !string.Equals(check.FileHash.Trim(), sha256, StringComparison.OrdinalIgnoreCase);
            var sizeMismatch = check.FileSize is { } size && size != content.LongLength;
            if (hashMismatch || sizeMismatch)
            {
                var details = new
                {
                    fileRef = check.FileRef,
                    expectedHash = check.FileHash,
                    actualHash = sha256,
                    expectedSize = check.FileSize,
                    actualSize = content.LongLength,
                };
                await FinishAsync(route, RouteStatus.Failed, StatusReasons.ArtifactMismatch,
                    stage, StageState.Failed, StatusReasons.ArtifactMismatch, details, ct: ct);
                return;
            }

            artifact = new NewSignedArtifact(stage.Id, content, sha256, check.FileRef);
        }

        await InTransactionAsync(async (store, _, now) =>
        {
            await store.UpdateAndReleaseAsync(route with { Attempts = 0, NextAttemptAt = now }, ReplicaId, ct);
            if (artifact is not null)
            {
                await store.SaveArtifactAsync(artifact, ct);
            }

            await store.UpdateStageAsync(stage with { State = StageState.Done, FinishedAt = now }, ct);
            await AuditAsync(store, route.Id, stage.Id, AuditEventTypes.StageDone, new
            {
                stageKey = stage.StageKey,
                requestId = stage.RequestId,
                signedAt = check.SignedAt,
                fileRef = check.FileRef,
                sha256 = artifact?.Sha256,
                sizeBytes = artifact?.Content.LongLength,
            }, ct);
        }, ct);
    }

    /// <summary>
    /// Moves the route to a terminal status and, if given and not terminal yet, the stage to a terminal state.
    /// </summary>
    private Task FinishAsync(
        RouteInstance route,
        RouteStatus status,
        string? reason,
        StageInstance? stage = null,
        StageState stageState = StageState.Failed,
        string? stageReason = null,
        object? details = null,
        Func<ISigningGateway, Task>? beforeStageWrite = null,
        CancellationToken ct = default) =>
        InTransactionAsync(async (store, gateway, now) =>
        {
            await store.UpdateAndReleaseAsync(
                route with { Status = status, StatusReason = reason, Attempts = 0 },
                ReplicaId, ct);

            if (beforeStageWrite is not null)
            {
                await beforeStageWrite(gateway);
            }

            if (stage is not null && !stage.State.IsTerminal())
            {
                await FinishStageAsync(store, route.Id, stage, stageState, stageReason, now, ct);
            }

            await AuditRouteFinishedAsync(store, route.Id, status, reason, details, ct);
        }, ct);

    private async Task<StepResult> RecordTechnicalFailureAsync(long routeId, Exception error, CancellationToken ct)
    {
        try
        {
            await InTransactionAsync(async (store, _, now) =>
            {
                var route = await store.GetRouteAsync(routeId, ct);
                if (route is null || route.LockedBy != ReplicaId || route.Status != RouteStatus.Active)
                {
                    throw new LeaseLostException(routeId, ReplicaId);
                }

                var attempts = route.Attempts + 1;
                var exhausted = attempts >= _options.MaxAttempts;
                var nextAttemptAt = exhausted ? route.NextAttemptAt : now + _options.GetRetryDelay(attempts);

                await store.UpdateAndReleaseAsync(
                    exhausted
                        ? route with { Status = RouteStatus.Failed, StatusReason = StatusReasons.AttemptsExhausted, Attempts = attempts }
                        : route with { Attempts = attempts, NextAttemptAt = nextAttemptAt },
                    ReplicaId, ct);

                await AuditAsync(store, routeId, route.CurrentStageId, AuditEventTypes.TechnicalFailure, new
                {
                    attempts,
                    nextAttemptAt = exhausted ? (DateTimeOffset?)null : nextAttemptAt,
                    errorType = error.GetType().FullName,
                    error = Truncate(error.Message),
                }, ct);

                if (exhausted)
                {
                    if (route.CurrentStageId is { } stageId
                        && await store.GetStageAsync(stageId, ct) is { } stage
                        && !stage.State.IsTerminal())
                    {
                        await FinishStageAsync(store, routeId, stage, StageState.Failed, StatusReasons.AttemptsExhausted, now, ct);
                    }

                    await AuditRouteFinishedAsync(store, routeId, RouteStatus.Failed, StatusReasons.AttemptsExhausted, null, ct);
                }
            }, ct);

            return new StepResult(routeId, StepOutcome.TechnicalFailure, error);
        }
        catch (LeaseLostException)
        {
            return new StepResult(routeId, StepOutcome.LeaseLost, error);
        }
        catch (Exception recordError) when (!ct.IsCancellationRequested)
        {
            return new StepResult(routeId, StepOutcome.FailureNotRecorded, new AggregateException(error, recordError));
        }
    }

    private async Task InTransactionAsync(
        Func<IRouteInstanceStore, ISigningGateway, DateTimeOffset, Task> work, CancellationToken ct)
    {
        await using var uow = await _unitOfWork.BeginAsync(ct);
        var now = await uow.Store.GetNowAsync(ct);
        await work(uow.Store, uow.Gateway, now);
        await uow.CommitAsync(ct);
    }

    private async Task FinishStageAsync(
        IRouteInstanceStore store,
        long routeId,
        StageInstance stage,
        StageState state,
        string? reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await store.UpdateStageAsync(stage with { State = state, FinishedAt = now, Reason = reason }, ct);

        var eventType = state switch
        {
            StageState.Rejected => AuditEventTypes.StageRejected,
            StageState.Expired => AuditEventTypes.StageExpired,
            StageState.Failed => AuditEventTypes.StageFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a terminal failure state."),
        };
        await AuditAsync(store, routeId, stage.Id, eventType,
            new { stageKey = stage.StageKey, requestId = stage.RequestId, reason }, ct);
    }

    private Task AuditRouteFinishedAsync(
        IRouteInstanceStore store, long routeId, RouteStatus status, string? reason, object? details, CancellationToken ct) =>
        AuditAsync(store, routeId, null, AuditEventTypes.RouteFinished,
            new { status = status.ToCode(), reason, details }, ct);

    private async Task AuditSkippedAsync(IRouteInstanceStore store, long routeId, StageSelection selection, CancellationToken ct)
    {
        foreach (var skipped in selection.Skipped)
        {
            await AuditAsync(store, routeId, null, AuditEventTypes.StageSkipped, new { stageKey = skipped.Key }, ct);
        }
    }

    private Task AuditAsync(
        IRouteInstanceStore store, long routeId, long? stageId, string eventType, object payload, CancellationToken ct) =>
        store.AppendAuditAsync(
            new AuditEntry(routeId, stageId, eventType, JsonSerializer.Serialize(payload), ReplicaId),
            ct);

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
