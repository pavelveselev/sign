using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Core.Routes;

/// <summary>
/// Sample routes. Real document types and routes are not known yet (open question 9);
/// replace these when they are. Add new versions next to old ones, do not edit a version
/// that may have started documents.
/// </summary>
public static class SampleRoutes
{
    /// <summary>Automatic signing only: organization seal.</summary>
    public static RouteDefinition InvoiceV1 { get; } =
        Route.For("invoice", version: 1)
            .Stage("org-seal", s => s.Automatic(signer: "org-seal").Timeout(TimeSpan.FromHours(1)))
            .Build();

    /// <summary>Mixed route: manual review, automatic seal, director approval for large amounts.</summary>
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
