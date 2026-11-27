using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Routes;

namespace Invest.Sign.Router;

/// <summary>
/// Маршруты по умолчанию — классы из <see cref="Routes"/>. Свои маршруты передаются в
/// <see cref="RouterServiceCollectionExtensions.AddSigningRouter"/>.
/// </summary>
public static class DefaultRoutes
{
    public static IReadOnlyList<RouteDefinition> All { get; } = Build(new DocumentRouteV1());

    /// <summary>Строит определения маршрутов-классов; некорректный маршрут останавливает запуск.</summary>
    public static IReadOnlyList<RouteDefinition> Build(params SigningRoute[] routes) =>
        routes.Select(r => r.Build()).ToArray();
}
