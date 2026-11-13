using System.Globalization;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routes;
using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Tests.Routes;

public class SampleRoutesTests
{
    private static DocumentContext Contract(decimal amount) =>
        new("doc-1", "contract", string.Create(CultureInfo.InvariantCulture, $$"""{"amount": {{amount}}}"""), null);

    [Fact]
    public void Sample_routes_form_a_valid_registry()
    {
        var registry = new RouteRegistry(SampleRoutes.All);

        Assert.NotNull(registry.FindLatest("invoice"));
        Assert.NotNull(registry.FindLatest("contract"));
    }

    [Fact]
    public void Invoice_route_is_automatic_only()
    {
        Assert.All(SampleRoutes.InvoiceV1.Stages, s => Assert.Equal(SigningKind.Auto, s.Kind));
    }

    [Fact]
    public void Contract_route_mixes_manual_and_automatic_stages()
    {
        var kinds = SampleRoutes.ContractV1.Stages.Select(s => s.Kind).Distinct();

        Assert.Equal([SigningKind.Manual, SigningKind.Auto], kinds);
    }

    [Fact]
    public void Contract_requires_director_above_threshold()
    {
        Assert.Equal("director", SampleRoutes.ContractV1.NextStage("org-seal", Contract(10_000.01m))?.Key);
    }

    [Fact]
    public void Contract_skips_director_at_or_below_threshold()
    {
        Assert.Null(SampleRoutes.ContractV1.NextStage("org-seal", Contract(10_000m)));
    }
}
