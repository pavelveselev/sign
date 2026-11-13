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
}
