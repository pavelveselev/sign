using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Core.Routes;

/// <summary>
/// Примеры маршрутов. Реальные типы документов и маршруты пока неизвестны (открытый вопрос 9);
/// замените эти, когда они появятся. Новые версии добавляйте рядом со старыми, не редактируйте версию,
/// по которой могли быть запущены документы.
/// </summary>
public static class SampleRoutes
{
    /// <summary>Только автоматическое подписание: печать организации.</summary>
    public static RouteDefinition InvoiceV1 { get; } =
        Route.For("invoice", version: 1)
            .Stage("org-seal", s => s.Automatic(signer: "org-seal").Timeout(TimeSpan.FromHours(1)))
            .Build();

    /// <summary>Смешанный маршрут: ручная проверка, автоматическая печать, согласование директора для крупных сумм.</summary>
    public static RouteDefinition ContractV1 { get; } =
        Route.For("contract", version: 1)
            .Stage("legal-review", s => s.Manual(signer: "legal").Timeout(TimeSpan.FromDays(3)))
            .Stage("org-seal", s => s.Automatic(signer: "org-seal").Timeout(TimeSpan.FromHours(1)))
            .Stage("director", s => s.Manual(signer: "director")
                .When(doc => doc.GetDecimal("amount") > 10_000)
                .Timeout(TimeSpan.FromDays(5)))
            .Build();

    public static IReadOnlyList<RouteDefinition> All { get; } = [InvoiceV1, ContractV1];
}
