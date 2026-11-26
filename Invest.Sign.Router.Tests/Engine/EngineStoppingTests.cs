using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;
using static Invest.Sign.Router.Tests.Engine.EngineHarness;

namespace Invest.Sign.Router.Tests.Engine;

/// <summary>Корректная остановка: сигнал остановки не прерывает текущий шаг, но запрещает брать новую работу.</summary>
public class EngineStoppingTests
{
    private readonly EngineHarness _h = new();

    [Fact]
    public async Task Stop_signal_lets_running_step_finish_and_skips_remaining_candidates()
    {
        var first = await _h.StartDocumentAsync("doc-1", "two-stage");
        var second = await _h.StartDocumentAsync("doc-2", "two-stage");
        await _h.StepAdvancedAsync(first);    // вход в "a": этап PENDING
        await _h.StepAdvancedAsync(second);

        using var stopping = new CancellationTokenSource();
        var handler = new StoppingHandler(stopping, new SigningStageHandler(new FakeUnitOfWorkFactory(_h.Db)));
        var handlers = new StageHandlerRegistry().Add(typeof(SigningStageHandler), handler);
        var engine = new SigningEngine(
            new FakeUnitOfWorkFactory(_h.Db), _h.Incoming, _h.Files, handlers, new RouteRegistry([TwoStage]), _h.Options, ReplicaA);

        var tick = await engine.TickAsync(stopping.Token, CancellationToken.None);

        var step = Assert.Single(tick.Steps);
        Assert.Equal(StepOutcome.Advanced, step.Outcome);
        Assert.Equal(StageState.Requested, _h.Db.CurrentStage(step.RouteId).State);
        Assert.False(handler.SawCancellation);

        var other = step.RouteId == first ? second : first;
        Assert.Equal(StageState.Pending, _h.Db.CurrentStage(other).State);
        Assert.Null(_h.Db.Routes[other].LockedBy);
    }

    [Fact]
    public async Task Stop_signal_before_tick_creates_no_routes()
    {
        _h.Incoming.Add("doc-1", "two-stage");
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var tick = await _h.Engine.TickAsync(stopping.Token, CancellationToken.None);

        Assert.Equal(0, tick.Discovery.Created);
        Assert.Empty(tick.Steps);
        Assert.Empty(_h.Db.Routes);
    }

    /// <summary>Подаёт сигнал остановки в момент создания запроса на подписание, то есть посреди шага.</summary>
    private sealed class StoppingHandler(CancellationTokenSource stopping, IStageHandler inner) : IStageHandler
    {
        public bool SawCancellation { get; private set; }

        public Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken) =>
            inner.StartAsync(stage, cancellationToken);

        public Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken) =>
            inner.CheckAsync(stage, cancellationToken);

        public async Task OnTransitionAsync(
            StageTransition transition, IUnitOfWork unitOfWork, CancellationToken cancellationToken)
        {
            await stopping.CancelAsync();
            SawCancellation |= cancellationToken.IsCancellationRequested;
            await inner.OnTransitionAsync(transition, unitOfWork, cancellationToken);
        }
    }
}
