namespace Invest.Sign.Router.Core.Routing;

/// <summary>Условие входа в этап выбросило исключение. Маршрут завершается с <c>CONDITION_ERROR</c> без повторов.</summary>
public sealed class StageConditionException : Exception
{
    public StageConditionException(string stageKey, Exception innerException)
        : base($"Condition of stage '{stageKey}' failed: {innerException.Message}", innerException)
    {
        StageKey = stageKey;
    }

    public string StageKey { get; }
}
