namespace Invest.Sign.Router.Core.Routing;

/// <summary>Ограничения длины идентификаторов в определениях маршрутов. Должны соответствовать столбцам в <c>db/service</c>.</summary>
public static class RouteLimits
{
    public const int MaxCodeLength = 100;
    public const int MaxStageKeyLength = 100;
    public const int MaxSignerLength = 200;
}
