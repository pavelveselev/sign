namespace Invest.Sign.Router.Core.Routing;

/// <summary>
/// Ограничения длины идентификаторов в определениях маршрутов. Должны соответствовать столбцам
/// <c>route_code</c> и <c>stage_key</c> таблицы <c>sign_document</c>.
/// </summary>
public static class RouteLimits
{
    public const int MaxCodeLength = 100;
    public const int MaxStageKeyLength = 100;
}
