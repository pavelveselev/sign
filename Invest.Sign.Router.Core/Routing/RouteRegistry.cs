namespace Invest.Sign.Router.Core.Routing;

/// <summary>
/// Registry of route definitions keyed by (code, version).
/// New documents get the highest version of the route for their type; started documents
/// keep the version fixed on the instance, so old versions must stay registered while
/// documents on them are active.
/// </summary>
public sealed class RouteRegistry
{
    private readonly Dictionary<RouteKey, RouteDefinition> _byKey;
    private readonly Dictionary<string, RouteDefinition> _latestByCode;

    /// <exception cref="RouteDefinitionException">The same (code, version) is registered more than once.</exception>
    public RouteRegistry(IEnumerable<RouteDefinition> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var all = routes.ToArray();
        var duplicates = all
            .GroupBy(r => r.Key)
            .Where(g => g.Count() > 1)
            .Select(g => $"Route {g.Key} is registered more than once.")
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new RouteDefinitionException("Route registry", duplicates);
        }

        _byKey = all.ToDictionary(r => r.Key);
        _latestByCode = all
            .GroupBy(r => r.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Version)!, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<RouteDefinition> All => _byKey.Values;

    /// <summary>Route for a document type that is about to start. Null when the type has no route.</summary>
    public RouteDefinition? FindLatest(string docType)
    {
        ArgumentNullException.ThrowIfNull(docType);
        return _latestByCode.GetValueOrDefault(docType);
    }

    /// <summary>Exact route version fixed on an instance. Null when that version is not registered.</summary>
    public RouteDefinition? Find(string code, int version)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _byKey.GetValueOrDefault(new RouteKey(code, version));
    }
}
