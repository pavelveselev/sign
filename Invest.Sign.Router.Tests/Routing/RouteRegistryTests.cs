using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Tests.Routing;

public class RouteRegistryTests
{
    private static RouteDefinition Make(string code, int version, string stageKey = "s") =>
        Route.For(code, version)
            .Stage(stageKey, s => s.Manual("x").Timeout(TimeSpan.FromHours(1)))
            .Build();

    [Fact]
    public void New_documents_get_the_highest_version_regardless_of_registration_order()
    {
        var registry = new RouteRegistry([Make("contract", 2), Make("contract", 3), Make("contract", 1)]);

        Assert.Equal(3, registry.FindLatest("contract")?.Version);
    }

    [Fact]
    public void Started_documents_resolve_their_own_version()
    {
        var registry = new RouteRegistry([Make("contract", 1, "old"), Make("contract", 2, "new")]);

        var route = registry.Find("contract", 1);

        Assert.NotNull(route);
        Assert.Equal("old", Assert.Single(route.Stages).Key);
    }

    [Fact]
    public void Unknown_type_or_version_returns_null()
    {
        var registry = new RouteRegistry([Make("contract", 1)]);

        Assert.Null(registry.FindLatest("invoice"));
        Assert.Null(registry.Find("contract", 2));
        Assert.Null(registry.Find("invoice", 1));
    }

    [Fact]
    public void Codes_are_case_sensitive()
    {
        var registry = new RouteRegistry([Make("contract", 1)]);

        Assert.Null(registry.FindLatest("Contract"));
    }

    [Fact]
    public void Versions_of_different_codes_are_independent()
    {
        var registry = new RouteRegistry([Make("contract", 5), Make("invoice", 1)]);

        Assert.Equal(5, registry.FindLatest("contract")?.Version);
        Assert.Equal(1, registry.FindLatest("invoice")?.Version);
        Assert.Equal(2, registry.All.Count);
    }

    [Fact]
    public void Rejects_duplicate_code_and_version()
    {
        var ex = Assert.Throws<RouteDefinitionException>(
            () => new RouteRegistry([Make("contract", 1, "a"), Make("contract", 1, "b"), Make("invoice", 1)]));

        Assert.Equal("Route contract@v1 is registered more than once.", Assert.Single(ex.Errors));
    }
}
