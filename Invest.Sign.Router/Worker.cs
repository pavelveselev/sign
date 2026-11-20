using Invest.Sign.Router.Core.Engine;
using Microsoft.Extensions.Options;

namespace Invest.Sign.Router;

/// <summary>
/// Цикл тактов движка. По сигналу остановки новые документы и экземпляры не берутся, а текущий шаг
/// дорабатывает до конца; прерывается он, только если истёк <see cref="RouterOptions.ShutdownTimeout"/>.
/// </summary>
public sealed class Worker(
    SigningEngine engine,
    IOptions<RouterOptions> routerOptions,
    IOptions<EngineOptions> engineOptions,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly RouterOptions _options = routerOptions.Value;
    private readonly int _batchSize = engineOptions.Value.BatchSize;

    // Отменяется, только когда хост перестаёт ждать (истёк тайм-аут остановки): прерывает выполняемый шаг.
    private readonly CancellationTokenSource _abort = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Signing router started as replica {ReplicaId}, tick interval {TickInterval}",
            engine.ReplicaId, _options.TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var fullBatch = false;
            try
            {
                var tick = await engine.TickAsync(stoppingToken, _abort.Token);
                LogTick(tick);
                fullBatch = tick.Steps.Count >= _batchSize;
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Например, база недоступна при выборе кандидатов. Цикл продолжается: следующий такт повторит попытку.
                logger.LogError(ex, "Tick failed");
            }

            if (fullBatch)
            {
                // Есть ещё готовая работа: следующий такт сразу.
                continue;
            }

            try
            {
                await Task.Delay(_options.TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Signing router stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // cancellationToken отменяется по истечении HostOptions.ShutdownTimeout.
        await using var registration = cancellationToken.Register(_abort.Cancel);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    private void LogTick(TickResult tick)
    {
        var discovery = tick.Discovery;
        if (discovery.SourceError is { } sourceError)
        {
            logger.LogError(sourceError, "Reading incoming documents failed");
        }

        foreach (var (docId, error) in discovery.Errors)
        {
            logger.LogError(error, "Creating route instance for document {DocId} failed", docId);
        }

        if (discovery.Created > 0)
        {
            logger.LogInformation("Discovered {Created} new document(s)", discovery.Created);
        }

        foreach (var step in tick.Steps)
        {
            switch (step.Outcome)
            {
                case StepOutcome.Advanced:
                    logger.LogDebug("Route {RouteId} advanced", step.RouteId);
                    break;
                case StepOutcome.NotAcquired:
                    logger.LogDebug("Route {RouteId} is taken by another replica", step.RouteId);
                    break;
                case StepOutcome.LeaseLost:
                    logger.LogWarning(step.Error, "Lease on route {RouteId} was lost, step discarded", step.RouteId);
                    break;
                case StepOutcome.TechnicalFailure:
                    logger.LogWarning(step.Error, "Route {RouteId} step failed, retry scheduled", step.RouteId);
                    break;
                case StepOutcome.FailureNotRecorded:
                    logger.LogError(step.Error, "Route {RouteId} step failed and the failure was not recorded", step.RouteId);
                    break;
            }
        }

        if (tick.Steps.Count > 0)
        {
            logger.LogInformation(
                "Tick processed {Steps} route instance(s), {Advanced} advanced",
                tick.Steps.Count, tick.Steps.Count(s => s.Outcome == StepOutcome.Advanced));
        }
    }
}
