using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Tests.Routing;

public class RouteBuilderTests
{
    [Fact]
    public void Builds_route_with_stages_in_declared_order()
    {
        var route = Route.For("contract", version: 2)
            .Stage("review", s => s.Manual(signer: "legal").Timeout(TimeSpan.FromDays(3)))
            .Stage("seal", s => s.Automatic(signer: "org-seal").Timeout(TimeSpan.FromHours(1)))
            .Build();

        Assert.Equal("contract", route.Code);
        Assert.Equal(2, route.Version);
        Assert.Equal(new RouteKey("contract", 2), route.Key);
        Assert.Collection(
            route.Stages,
            s =>
            {
                Assert.Equal("review", s.Key);
                Assert.Equal(SigningKind.Manual, s.Kind);
                Assert.Equal("legal", s.Signer);
                Assert.Equal(TimeSpan.FromDays(3), s.Timeout);
                Assert.False(s.IsConditional);
            },
            s =>
            {
                Assert.Equal("seal", s.Key);
                Assert.Equal(SigningKind.Auto, s.Kind);
                Assert.Equal("org-seal", s.Signer);
                Assert.Equal(TimeSpan.FromHours(1), s.Timeout);
            });
    }

    [Fact]
    public void Order_of_stage_builder_calls_does_not_matter()
    {
        var route = Route.For("contract", 1)
            .Stage("director", s => s.Timeout(TimeSpan.FromDays(5)).When(_ => true).Manual("director"))
            .Build();

        var stage = Assert.Single(route.Stages);
        Assert.Equal(SigningKind.Manual, stage.Kind);
        Assert.True(stage.IsConditional);
    }

    [Fact]
    public void Rejects_route_without_stages()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1).Build());

        Assert.Contains(ex.Errors, e => e.Contains("at least one stage"));
    }

    [Fact]
    public void Rejects_duplicate_stage_keys()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage("review", s => s.Manual("a").Timeout(TimeSpan.FromHours(1)))
            .Stage("review", s => s.Manual("b").Timeout(TimeSpan.FromHours(1)))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("'review' is used more than once"));
    }

    [Fact]
    public void Rejects_stage_without_timeout()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage("review", s => s.Manual("legal"))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("timeout is not set"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_timeout(int seconds)
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage("review", s => s.Manual("legal").Timeout(TimeSpan.FromSeconds(seconds)))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("timeout must be positive"));
    }

    [Fact]
    public void Rejects_stage_without_handler()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage("review", s => s.Timeout(TimeSpan.FromHours(1)))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("handler is not set"));
    }

    [Fact]
    public void Rejects_settings_applied_twice()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage("review", s => s
                .Manual("legal").Automatic("seal")
                .Timeout(TimeSpan.FromHours(1)).Timeout(TimeSpan.FromHours(2))
                .When(_ => true).When(_ => false))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("handler is set more than once"));
        Assert.Contains(ex.Errors, e => e.Contains("timeout is set more than once"));
        Assert.Contains(ex.Errors, e => e.Contains("condition is set more than once"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" review")]
    public void Rejects_invalid_stage_key(string key)
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage(key, s => s.Manual("legal").Timeout(TimeSpan.FromHours(1)))
            .Build());

        Assert.Contains(ex.Errors, e => e.StartsWith("Stage key"));
    }

    [Fact]
    public void Rejects_empty_signer()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 1)
            .Stage("review", s => s.Manual("").Timeout(TimeSpan.FromHours(1)))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("signer must not be empty"));
    }

    [Fact]
    public void Rejects_identifiers_longer_than_columns()
    {
        var tooLong = new string('x', RouteLimits.MaxCodeLength + 1);

        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For(tooLong, 1)
            .Stage("review", s => s.Manual("legal").Timeout(TimeSpan.FromHours(1)))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains($"longer than {RouteLimits.MaxCodeLength}"));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("contract", 0)]
    public void Rejects_invalid_code_or_version(string code, int version)
    {
        Assert.Throws<RouteDefinitionException>(() => Route.For(code, version)
            .Stage("review", s => s.Manual("legal").Timeout(TimeSpan.FromHours(1)))
            .Build());
    }

    [Fact]
    public void Reports_all_errors_at_once()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => Route.For("contract", 0)
            .Stage("a", s => s.Manual("legal"))
            .Stage("b", s => s.Timeout(TimeSpan.FromHours(1)))
            .Build());

        Assert.Equal(3, ex.Errors.Count);
        Assert.Contains("Route 'contract' v0", ex.Message);
    }
}
