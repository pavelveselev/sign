using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;

namespace Invest.Sign.Router.Tests.Routing;

public class RouteRegistryTests
{
    private static RouteVersion V(int number, string stageKey = "s") =>
        new(number, new SigningStage(stageKey, "x", TimeSpan.FromHours(1)));

    [Fact]
    public void New_documents_get_the_highest_version_regardless_of_declaration_order()
    {
        var registry = new RouteRegistry([new TestRoute("contract", V(2), V(3), V(1))]);

        Assert.Equal(3, registry.FindLatest("contract")?.Version);
    }

    [Fact]
    public void Started_documents_resolve_their_own_version()
    {
        var registry = new RouteRegistry([new TestRoute("contract", V(1, "old"), V(2, "new"))]);

        var route = registry.Find("contract", 1);

        Assert.NotNull(route);
        Assert.Equal("old", Assert.Single(route.Stages).Key);
    }

    [Fact]
    public void Unknown_type_or_version_returns_null()
    {
        var registry = new RouteRegistry([new TestRoute("contract", V(1))]);

        Assert.Null(registry.FindLatest("invoice"));
        Assert.Null(registry.Find("contract", 2));
        Assert.Null(registry.Find("invoice", 1));
    }

    [Fact]
    public void Codes_are_case_sensitive()
    {
        var registry = new RouteRegistry([new TestRoute("contract", V(1))]);

        Assert.Null(registry.FindLatest("Contract"));
    }

    [Fact]
    public void Versions_of_different_codes_are_independent()
    {
        var registry = new RouteRegistry([new TestRoute("contract", V(5)), new TestRoute("invoice", V(1))]);

        Assert.Equal(5, registry.FindLatest("contract")?.Version);
        Assert.Equal(1, registry.FindLatest("invoice")?.Version);
        Assert.Equal(2, registry.All.Count);
    }

    [Fact]
    public void Rejects_two_classes_for_one_document_type()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new RouteRegistry(
            [new TestRoute("contract", V(1)), new TestRoute("contract", V(2)), new TestRoute("invoice", V(1))]));

        Assert.Equal(
            "Route 'contract' is declared by more than one class: TestRoute, TestRoute.", Assert.Single(ex.Errors));
    }

    [Fact]
    public void Rejects_invalid_route()
    {
        var ex = Assert.Throws<RouteDefinitionException>(
            () => new RouteRegistry([new TestRoute("contract", V(1), V(1))]));

        Assert.Contains(ex.Errors, e => e.Contains("Version 1 is declared more than once"));
    }
}
