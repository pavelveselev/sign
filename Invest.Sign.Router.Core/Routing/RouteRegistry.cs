namespace Invest.Sign.Router.Core.Routing;

/// <summary>
/// Реестр определений маршрутов с ключом (код, версия).
/// Новые документы получают наибольшую версию маршрута для своего типа; запущенные документы
/// сохраняют версию, зафиксированную в экземпляре, поэтому старые версии должны оставаться зарегистрированными, пока
/// по ним есть активные документы.
/// </summary>
public sealed class RouteRegistry
{
    private readonly Dictionary<RouteKey, RouteDefinition> _byKey;
    private readonly Dictionary<string, RouteDefinition> _latestByCode;

    /// <exception cref="RouteDefinitionException">Одна и та же пара (код, версия) зарегистрирована более одного раза.</exception>
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

    /// <summary>Маршрут для типа документа, который собирается стартовать. Null, если у типа нет маршрута.</summary>
    public RouteDefinition? FindLatest(string docType)
    {
        ArgumentNullException.ThrowIfNull(docType);
        return _latestByCode.GetValueOrDefault(docType);
    }

    /// <summary>Точная версия маршрута, зафиксированная в экземпляре. Null, если эта версия не зарегистрирована.</summary>
    public RouteDefinition? Find(string code, int version)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _byKey.GetValueOrDefault(new RouteKey(code, version));
    }
}
