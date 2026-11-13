namespace Invest.Sign.Router.Core.Routing;

/// <summary>A stage entry condition threw. The route fails with <c>CONDITION_ERROR</c>, without retries.</summary>
public sealed class StageConditionException : Exception
{
    public StageConditionException(string stageKey, Exception innerException)
        : base($"Condition of stage '{stageKey}' failed: {innerException.Message}", innerException)
    {
        StageKey = stageKey;
    }

    public string StageKey { get; }
}
