using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Routes;

namespace Invest.Sign.Router;

/// <summary>
/// Маршруты по умолчанию — классы из <see cref="Routes"/>, по одному на тип документа. Свои маршруты передаются в
/// <see cref="RouterServiceCollectionExtensions.AddSigningRouter"/>.
/// </summary>
public static class DefaultRoutes
{
    public static IReadOnlyList<SigningRoute> All { get; } = [new DocumentRoute()];
}
