namespace Invest.Sign.Router.Core.Routing;

/// <summary>Length limits for identifiers in route definitions. Must fit the columns in <c>db/service</c>.</summary>
public static class RouteLimits
{
    public const int MaxCodeLength = 100;
    public const int MaxStageKeyLength = 100;
    public const int MaxSignerLength = 200;
}
