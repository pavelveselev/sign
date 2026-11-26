using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router.Tests.Fakes;

/// <summary>Обработчик этапа, исходы которого задаёт тест. Записывает все вызовы.</summary>
public sealed class ScriptedStageHandler : IStageHandler
{
    public Func<StageContext, StageOutcome> OnStart { get; set; } = _ => new StageOutcome.Waiting();

    public Func<StageContext, StageOutcome> OnCheck { get; set; } = _ => new StageOutcome.Waiting();

    /// <summary>Вызывается в транзакции перехода; может выбросить исключение, чтобы проверить откат.</summary>
    public Action<StageTransition>? OnTransition { get; set; }

    public Func<object?, IReadOnlyList<string>> Validate { get; set; } = _ => [];

    public List<StageContext> Starts { get; } = [];

    public List<StageContext> Checks { get; } = [];

    public List<StageContext> Cancels { get; } = [];

    public List<StageTransition> Transitions { get; } = [];

    public Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken)
    {
        Starts.Add(stage);
        return Task.FromResult(OnStart(stage));
    }

    public Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken)
    {
        Checks.Add(stage);
        return Task.FromResult(OnCheck(stage));
    }

    public Task CancelAsync(StageContext stage, CancellationToken cancellationToken)
    {
        Cancels.Add(stage);
        return Task.CompletedTask;
    }

    public Task OnTransitionAsync(StageTransition transition, IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        OnTransition?.Invoke(transition);
        Transitions.Add(transition);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> ValidateSettings(object? settings) => Validate(settings);
}
