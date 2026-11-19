namespace Invest.Sign.Router.Core.Routing;

/// <summary>Определение маршрута или набор определений маршрутов некорректен.</summary>
public sealed class RouteDefinitionException : Exception
{
    public RouteDefinitionException(string subject, IReadOnlyList<string> errors)
        : base($"{subject} is invalid:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
