namespace Invest.Sign.Router.Core.Routing;

/// <summary>
/// Реестр определений маршрутов с ключом (код, версия), собранный из классов <see cref="SigningRoute"/>.
/// Новые документы получают наибольшую версию маршрута для своего типа; запущенные документы
/// сохраняют версию, зафиксированную в экземпляре, поэтому старые версии должны оставаться зарегистрированными, пока
/// по ним есть активные документы.
/// </summary>
public sealed class RouteRegistry
{
    private readonly Dictionary<RouteKey, RouteDefinition> _byKey;
    private readonly Dictionary<string, RouteDefinition> _latestByCode;

    /// <param name="routes">Маршруты, по одному классу на тип документа.</param>
    /// <exception cref="RouteDefinitionException">
    /// Маршрут некорректен или один тип документа описан более чем одним классом.
    /// </exception>
    public RouteRegistry(IEnumerable<SigningRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var classes = routes.ToArray();
        var duplicates = classes
            .GroupBy(r => r.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"Route '{g.Key}' is declared by more than one class: "
                + string.Join(", ", g.Select(r => r.GetType().Name)) + ".")
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new RouteDefinitionException("Route registry", duplicates);
        }

        var all = classes.SelectMany(r => r.Build()).ToArray();
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
