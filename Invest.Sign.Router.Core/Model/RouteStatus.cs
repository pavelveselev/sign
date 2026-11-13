namespace Invest.Sign.Router.Core.Model;

/// <summary>Status of a route instance. Common for all document types.</summary>
public enum RouteStatus
{
    Active,
    Completed,
    Rejected,
    Expired,
    Cancelled,
    Failed,
}

public static class RouteStatusExtensions
{
    public static bool IsTerminal(this RouteStatus status) => status != RouteStatus.Active;

    /// <summary>Value stored in <c>SGN_ROUTE_INSTANCE.status</c>.</summary>
    public static string ToCode(this RouteStatus status) => status switch
    {
        RouteStatus.Active => "ACTIVE",
        RouteStatus.Completed => "COMPLETED",
        RouteStatus.Rejected => "REJECTED",
        RouteStatus.Expired => "EXPIRED",
        RouteStatus.Cancelled => "CANCELLED",
        RouteStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static RouteStatus ParseRouteStatus(string code) => code switch
    {
        "ACTIVE" => RouteStatus.Active,
        "COMPLETED" => RouteStatus.Completed,
        "REJECTED" => RouteStatus.Rejected,
        "EXPIRED" => RouteStatus.Expired,
        "CANCELLED" => RouteStatus.Cancelled,
        "FAILED" => RouteStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown route status."),
    };
}
