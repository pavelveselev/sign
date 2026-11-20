using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Model;
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
        var handler = new StoppingHandler(stopping);
        var engine = new SigningEngine(
            new FakeUnitOfWorkFactory(_h.Db), _h.Incoming, _h.Files, handler, _h.Registry, _h.Options, ReplicaA);

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
    private sealed class StoppingHandler(CancellationTokenSource stopping) : IStageHandler
    {
        private readonly SigningStageHandler _inner = new();

        public bool SawCancellation { get; private set; }

        public async Task StartAsync(ISigningGateway gateway, SignRequest request, CancellationToken cancellationToken)
        {
            await stopping.CancelAsync();
            SawCancellation |= cancellationToken.IsCancellationRequested;
            await _inner.StartAsync(gateway, request, cancellationToken);
        }

        public Task<SignRequestResult> CheckAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken) =>
            _inner.CheckAsync(gateway, requestId, cancellationToken);

        public Task CancelAsync(ISigningGateway gateway, string requestId, CancellationToken cancellationToken) =>
            _inner.CancelAsync(gateway, requestId, cancellationToken);
    }
}
