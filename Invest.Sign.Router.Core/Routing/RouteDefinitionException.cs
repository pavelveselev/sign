namespace Invest.Sign.Router.Core.Routing;

/// <summary>A route definition or a set of route definitions is invalid.</summary>
public sealed class RouteDefinitionException : Exception
{
    public RouteDefinitionException(string subject, IReadOnlyList<string> errors)
        : base($"{subject} is invalid:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
