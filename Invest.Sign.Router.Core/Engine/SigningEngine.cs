using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Core.Engine;

/// <summary>
/// Движок маршрутов. Один <see cref="TickAsync"/> обнаруживает новые документы и по очереди обрабатывает готовые
/// экземпляры маршрутов: захват аренды, ровно один переход, освобождение аренды в той же транзакции.
/// </summary>
/// <remarks>
/// Бизнес-исходы (отклонение, истечение срока, ошибка подписания, несовпадение артефакта, ошибка условия) являются обычными
/// переходами. Любое другое исключение шага — техническая ошибка: счётчик попыток увеличивается, а повтор
/// планируется с экспоненциальной задержкой в отдельной транзакции. Потеря аренды отбрасывает шаг.
/// </remarks>
public sealed class SigningEngine
{
    private const int MaxErrorLength = 2000;

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    private readonly IUnitOfWorkFactory _unitOfWork;
    private readonly IIncomingDocumentSource _incoming;
    private readonly ISignedFileSource _files;
    private readonly IStageHandlerResolver _handlers;
    private readonly RouteRegistry _routes;
    private readonly EngineOptions _options;

    /// <exception cref="ArgumentException">Настройки некорректны.</exception>
    /// <exception cref="InvalidOperationException">
    /// Обработчик какого-либо этапа не зарегистрирован или отклонил настройки этапа.
    /// </exception>
    public SigningEngine(
        IUnitOfWorkFactory unitOfWork,
        IIncomingDocumentSource incoming,
        ISignedFileSource files,
        IStageHandlerResolver handlers,
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

        ValidateHandlers(routes, handlers);

        _unitOfWork = unitOfWork;
        _incoming = incoming;
        _files = files;
        _handlers = handlers;
        _routes = routes;
        _options = options;
        ReplicaId = replicaId;
    }

    /// <summary>Владелец аренды и исполнитель в журнале аудита.</summary>
    public string ReplicaId { get; }

    /// <summary>
    /// Обнаруживает новые документы, затем последовательно обрабатывает готовые экземпляры.
    /// Отмена проверяется между экземплярами; выполняемый шаг получает тот же токен.
    /// </summary>
    public Task<TickResult> TickAsync(CancellationToken cancellationToken) =>
        TickAsync(cancellationToken, cancellationToken);

    /// <summary>
    /// Как <see cref="TickAsync(CancellationToken)"/>, но с раздельными токенами для корректной остановки:
    /// <paramref name="stopping"/> лишь запрещает брать новые документы и экземпляры, а выполняемый шаг
    /// прерывается только <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<TickResult> TickAsync(CancellationToken stopping, CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(stopping, cancellationToken);

        IReadOnlyList<long> candidates;
        await using (var uow = await _unitOfWork.BeginAsync(cancellationToken))
        {
            candidates = await uow.Store.FindCandidatesAsync(_options.BatchSize, cancellationToken);
        }

        var steps = new List<StepResult>(candidates.Count);
        foreach (var routeId in candidates)
        {
            if (stopping.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            steps.Add(await ProcessAsync(routeId, cancellationToken));
        }

        return new TickResult(discovery, steps);
    }

    /// <summary>
    /// Создаёт экземпляры маршрутов для новых входящих документов. Документ, одновременно вставленный другой
    /// репликой, считается уже существующим. Документ неизвестного типа получает экземпляр в статусе <c>FAILED</c>.
    /// </summary>
    public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        DiscoverAsync(cancellationToken, cancellationToken);

    private async Task<DiscoveryResult> DiscoverAsync(CancellationToken stopping, CancellationToken cancellationToken)
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
            if (stopping.IsCancellationRequested || cancellationToken.IsCancellationRequested)
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

    /// <summary>Захватывает экземпляр, выполняет один шаг и освобождает его.</summary>
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
        // Фаза 1: чтение состояния в короткой транзакции.
        DateTimeOffset readAt;
        RouteInstance route;
        StageInstance? stage = null;
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
        }

        // Фаза 2: вызов обработчика вне транзакции (если нужен) и один переход в своей транзакции.
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
                await StartStageAsync(route, Activate(route, definition, stage), ct);
                break;
            case StageState.Requested:
                await CheckStageAsync(route, Activate(route, definition, stage), readAt, ct);
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

        await InTransactionAsync(async (uow, now) =>
        {
            var store = uow.Store;
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

    private async Task StartStageAsync(RouteInstance route, ActiveStage active, CancellationToken ct)
    {
        var outcome = await active.Handler.StartAsync(active.Context, ct);
        if (outcome is not StageOutcome.Waiting waiting)
        {
            await ApplyFinalOutcomeAsync(route, active, outcome, ct);
            return;
        }

        var stage = active.Instance;
        await InTransactionAsync(async (uow, now) =>
        {
            // Сначала проверка аренды: потерянная аренда не должна дойти до записей обработчика.
            await uow.Store.UpdateAndReleaseAsync(
                route with { Attempts = 0, NextAttemptAt = now + _options.PollInterval },
                ReplicaId, ct);

            await active.Handler.OnTransitionAsync(
                new StageTransition(active.Context, StageState.Pending, StageState.Requested, outcome, null), uow, ct);

            var deadline = now + active.Definition.Timeout;
            await uow.Store.UpdateStageAsync(
                stage with
                {
                    State = StageState.Requested,
                    RequestedAt = now,
                    DeadlineAt = deadline,
                    Data = waiting.Data ?? stage.Data,
                },
                ct);
            await AuditAsync(uow.Store, route.Id, stage.Id, AuditEventTypes.StageRequested, new
            {
                stageKey = stage.StageKey,
                requestId = stage.RequestId,
                handler = active.Definition.HandlerType.Name,
                settings = DescribeSettings(active.Definition.Settings),
                deadlineAt = deadline,
            }, ct);
        }, ct);
    }

    private async Task CheckStageAsync(
        RouteInstance route, ActiveStage active, DateTimeOffset checkedAt, CancellationToken ct)
    {
        var outcome = await active.Handler.CheckAsync(active.Context, ct);

        // Результат проверяется до срока: результат, пришедший с опозданием, всё равно засчитывается.
        if (outcome is not StageOutcome.Waiting waiting)
        {
            await ApplyFinalOutcomeAsync(route, active, outcome, ct);
            return;
        }

        var stage = active.Instance;
        var deadline = stage.DeadlineAt
            ?? throw new InvalidOperationException($"Requested stage instance {stage.Id} has no deadline.");

        if (checkedAt < deadline)
        {
            await InTransactionAsync(async (uow, now) =>
            {
                var next = now + _options.PollInterval;
                await uow.Store.UpdateAndReleaseAsync(
                    route with { Attempts = 0, NextAttemptAt = next < deadline ? next : deadline },
                    ReplicaId, ct);
                if (waiting.Data is not null && waiting.Data != stage.Data)
                {
                    await uow.Store.UpdateStageAsync(stage with { Data = waiting.Data }, ct);
                }
            }, ct);
            return;
        }

        await active.Handler.CancelAsync(active.Context, ct);
        await FinishAsync(route, RouteStatus.Expired, null,
            stage, StageState.Expired, null,
            new { deadlineAt = deadline },
            beforeStageWrite: uow => active.Handler.OnTransitionAsync(
                new StageTransition(active.Context, StageState.Requested, StageState.Expired, null, null), uow, ct),
            ct: ct);
    }

    /// <summary>Применяет исход обработчика, завершающий этап: выполнение, отказ или бизнес-ошибку.</summary>
    private Task ApplyFinalOutcomeAsync(RouteInstance route, ActiveStage active, StageOutcome outcome, CancellationToken ct)
    {
        switch (outcome)
        {
            case StageOutcome.Done done:
                return CompleteStageAsync(route, active, done, ct);

            case StageOutcome.Rejected rejected:
                return FinishStageByHandlerAsync(route, active, outcome, RouteStatus.Rejected, null,
                    StageState.Rejected, rejected.Reason, null, ct);

            case StageOutcome.Failed failed:
                return FinishStageByHandlerAsync(route, active, outcome, RouteStatus.Failed, failed.RouteReason,
                    StageState.Failed, failed.Reason, null, ct);

            default:
                throw new InvalidOperationException($"Unknown stage outcome {outcome.GetType().Name}.");
        }
    }

    private async Task CompleteStageAsync(
        RouteInstance route, ActiveStage active, StageOutcome.Done done, CancellationToken ct)
    {
        var stage = active.Instance;
        NewSignedArtifact? artifact = null;
        if (done.File is { } file && !string.IsNullOrWhiteSpace(file.FileRef))
        {
            // Чтение вне транзакции: общий ресурс может быть медленным.
            var content = await _files.ReadAsync(file.FileRef, ct);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));

            var hashMismatch = !string.IsNullOrWhiteSpace(file.Hash)
                && !string.Equals(file.Hash.Trim(), sha256, StringComparison.OrdinalIgnoreCase);
            var sizeMismatch = file.Size is { } size && size != content.LongLength;
            if (hashMismatch || sizeMismatch)
            {
                var details = new
                {
                    fileRef = file.FileRef,
                    expectedHash = file.Hash,
                    actualHash = sha256,
                    expectedSize = file.Size,
                    actualSize = content.LongLength,
                };
                await FinishStageByHandlerAsync(route, active, done, RouteStatus.Failed, StatusReasons.ArtifactMismatch,
                    StageState.Failed, StatusReasons.ArtifactMismatch, details, ct);
                return;
            }

            artifact = new NewSignedArtifact(stage.Id, content, sha256, file.FileRef);
        }

        await InTransactionAsync(async (uow, now) =>
        {
            await uow.Store.UpdateAndReleaseAsync(route with { Attempts = 0, NextAttemptAt = now }, ReplicaId, ct);
            if (artifact is not null)
            {
                await uow.Store.SaveArtifactAsync(artifact, ct);
            }

            await active.Handler.OnTransitionAsync(
                new StageTransition(active.Context, stage.State, StageState.Done, done, null), uow, ct);

            await uow.Store.UpdateStageAsync(
                stage with
                {
                    State = StageState.Done,
                    RequestedAt = stage.RequestedAt ?? now,
                    FinishedAt = now,
                    Data = done.Data ?? stage.Data,
                },
                ct);
            await AuditAsync(uow.Store, route.Id, stage.Id, AuditEventTypes.StageDone, new
            {
                stageKey = stage.StageKey,
                requestId = stage.RequestId,
                signedAt = done.CompletedAt,
                fileRef = done.File?.FileRef,
                sha256 = artifact?.Sha256,
                sizeBytes = artifact?.Content.LongLength,
            }, ct);
        }, ct);
    }

    /// <summary>Завершает маршрут из-за исхода обработчика, сообщая обработчику о переходе в той же транзакции.</summary>
    private Task FinishStageByHandlerAsync(
        RouteInstance route,
        ActiveStage active,
        StageOutcome outcome,
        RouteStatus status,
        string? reason,
        StageState stageState,
        string? stageReason,
        object? details,
        CancellationToken ct) =>
        FinishAsync(route, status, reason, active.Instance, stageState, stageReason, details,
            beforeStageWrite: uow => active.Handler.OnTransitionAsync(
                new StageTransition(active.Context, active.Instance.State, stageState, outcome, stageReason), uow, ct),
            ct: ct);

    /// <summary>
    /// Переводит маршрут в терминальный статус и, если этап задан и ещё не терминален, этап — в терминальное состояние.
    /// </summary>
    private Task FinishAsync(
        RouteInstance route,
        RouteStatus status,
        string? reason,
        StageInstance? stage = null,
        StageState stageState = StageState.Failed,
        string? stageReason = null,
        object? details = null,
        Func<IUnitOfWork, Task>? beforeStageWrite = null,
        CancellationToken ct = default) =>
        InTransactionAsync(async (uow, now) =>
        {
            await uow.Store.UpdateAndReleaseAsync(
                route with { Status = status, StatusReason = reason, Attempts = 0 },
                ReplicaId, ct);

            if (beforeStageWrite is not null)
            {
                await beforeStageWrite(uow);
            }

            if (stage is not null && !stage.State.IsTerminal())
            {
                await FinishStageAsync(uow.Store, route.Id, stage, stageState, stageReason, now, ct);
            }

            await AuditRouteFinishedAsync(uow.Store, route.Id, status, reason, details, ct);
        }, ct);

    private async Task<StepResult> RecordTechnicalFailureAsync(long routeId, Exception error, CancellationToken ct)
    {
        try
        {
            await InTransactionAsync(async (uow, now) =>
            {
                var store = uow.Store;
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

    private async Task InTransactionAsync(Func<IUnitOfWork, DateTimeOffset, Task> work, CancellationToken ct)
    {
        await using var uow = await _unitOfWork.BeginAsync(ct);
        var now = await uow.Store.GetNowAsync(ct);
        await work(uow, now);
        await uow.CommitAsync(ct);
    }

    /// <summary>Связывает экземпляр этапа с его определением и обработчиком.</summary>
    private ActiveStage Activate(RouteInstance route, RouteDefinition definition, StageInstance stage)
    {
        var stageDefinition = definition.FindStage(stage.StageKey)
            ?? throw new InvalidOperationException($"Stage '{stage.StageKey}' is not defined in route {definition.Key}.");
        var handler = _handlers.Find(stageDefinition.HandlerType)
            ?? throw new InvalidOperationException(
                $"Stage handler {stageDefinition.HandlerType.Name} is not registered.");
        var context = new StageContext(
            route.Id, stage.Id, stage.StageKey, stage.RequestId,
            route.ToDocumentContext(), stageDefinition.Settings, stage.Data);
        return new ActiveStage(stageDefinition, stage, handler, context);
    }

    private static void ValidateHandlers(RouteRegistry routes, IStageHandlerResolver handlers)
    {
        var errors = new List<string>();
        foreach (var route in routes.All.OrderBy(r => r.Code, StringComparer.Ordinal).ThenBy(r => r.Version))
        {
            foreach (var stage in route.Stages)
            {
                var subject = $"Route {route.Key}, stage '{stage.Key}'";
                if (handlers.Find(stage.HandlerType) is not { } handler)
                {
                    errors.Add($"{subject}: handler {stage.HandlerType.Name} is not registered.");
                    continue;
                }

                errors.AddRange(handler.ValidateSettings(stage.Settings).Select(e => $"{subject}: {e}."));
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid stage handlers: {string.Join(" ", errors)}");
        }
    }

    /// <summary>Настройки этапа для аудита. Несериализуемые настройки записываются строкой, а не роняют шаг.</summary>
    private static object? DescribeSettings(object? settings)
    {
        if (settings is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(settings, settings.GetType(), SettingsJson);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return settings.ToString();
        }
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

    /// <summary>Этап в работе: определение, экземпляр, обработчик и контекст для обработчика.</summary>
    private sealed record ActiveStage(
        StageDefinition Definition, StageInstance Instance, IStageHandler Handler, StageContext Context);
}
