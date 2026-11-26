using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Stages;
using Route = Invest.Sign.Router.Core.Routing.Route;

namespace Invest.Sign.Router;

/// <summary>
/// Маршруты по умолчанию. Реальные типы документов пока неизвестны (открытый вопрос 9): <c>document</c> —
/// заглушка для <c>doc_type</c> из вьюхи входящих документов. Свои маршруты передаются в
/// <see cref="RouterServiceCollectionExtensions.AddSigningRouter"/>.
/// </summary>
public static class DefaultRoutes
{
    /// <summary>Один этап подписания через каталоги; имя файла — атрибут <c>file_name</c>.</summary>
    public static RouteDefinition DocumentV1 { get; } =
        Route.For("document", version: 1)
            .Stage("sign", s => s.FileSigning().Timeout(TimeSpan.FromDays(7)))
            .Build();

    public static IReadOnlyList<RouteDefinition> All { get; } = [DocumentV1];
}
