using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;

namespace Invest.Sign.Router.Tests.Routing;

public class SigningRouteTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void Builds_every_version_with_stages_in_declared_order()
    {
        var routes = new ContractRoute().Build();

        Assert.Collection(
            routes,
            v1 =>
            {
                Assert.Equal(new RouteKey("contract", 1), v1.Key);
                Assert.Equal("legal", Assert.Single(v1.Stages).Key);
            },
            v2 =>
            {
                Assert.Equal(new RouteKey("contract", 2), v2.Key);
                Assert.Collection(
                    v2.Stages,
                    s =>
                    {
                        Assert.Equal("legal", s.Key);
                        Assert.Equal(typeof(FakeSigningStageHandler), s.HandlerType);
                        Assert.Equal(new FakeSigningSettings("legal"), s.Settings);
                        Assert.Equal(TimeSpan.FromDays(3), s.Timeout);
                        Assert.False(s.IsConditional);
                    },
                    s =>
                    {
                        Assert.Equal("director", s.Key);
                        Assert.Equal(new FakeSigningSettings("director"), s.Settings);
                        Assert.Equal(TimeSpan.FromDays(5), s.Timeout);
                        Assert.True(s.IsConditional);
                    });
            });
    }

    [Fact]
    public void Stage_condition_is_evaluated_against_document_attributes()
    {
        var route = new ContractRoute().Build().Single(r => r.Version == 2);

        var small = new DocumentContext("doc-1", "contract", """{"amount":100}""", null);
        var large = new DocumentContext("doc-2", "contract", """{"amount":50000}""", null);

        Assert.Null(route.NextStage("legal", small));
        Assert.Equal("director", route.NextStage("legal", large)?.Key);
    }

    [Fact]
    public void Rejects_route_without_versions()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute("contract").Build());

        Assert.Contains(ex.Errors, e => e.Contains("at least one version"));
    }

    [Fact]
    public void Rejects_version_without_stages()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute("contract", new RouteVersion(1)).Build());

        Assert.Contains(ex.Errors, e => e.Contains("v1: version must have at least one stage"));
    }

    [Fact]
    public void Rejects_duplicate_versions()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute(
            "contract",
            new RouteVersion(1, new SigningStage("a", "x", Hour)),
            new RouteVersion(1, new SigningStage("b", "x", Hour))).Build());

        Assert.Contains(ex.Errors, e => e.Contains("Version 1 is declared more than once"));
    }

    [Fact]
    public void Rejects_duplicate_stage_keys()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute("contract", new RouteVersion(
            1, new SigningStage("review", "a", Hour), new SigningStage("review", "b", Hour))).Build());

        Assert.Contains(ex.Errors, e => e.Contains("'review' is used more than once"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_timeout(int seconds)
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute("contract", new RouteVersion(
            1, new SigningStage("review", "legal", TimeSpan.FromSeconds(seconds)))).Build());

        Assert.Contains(ex.Errors, e => e.Contains("timeout must be positive"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" review")]
    public void Rejects_invalid_stage_key(string key)
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute("contract", new RouteVersion(
            1, new SigningStage(key, "legal", Hour))).Build());

        Assert.Contains(ex.Errors, e => e.StartsWith("v1: stage key"));
    }

    [Fact]
    public void Rejects_identifiers_longer_than_columns()
    {
        var tooLong = new string('x', RouteLimits.MaxCodeLength + 1);

        var ex = Assert.Throws<RouteDefinitionException>(() => new TestRoute(tooLong, new RouteVersion(
            1, new SigningStage("review", "legal", Hour))).Build());

        Assert.Contains(ex.Errors, e => e.Contains($"longer than {RouteLimits.MaxCodeLength}"));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("contract", 0)]
    public void Rejects_invalid_code_or_version(string code, int version)
    {
        Assert.Throws<RouteDefinitionException>(() => new TestRoute(code, new RouteVersion(
            version, new SigningStage("review", "legal", Hour))).Build());
    }

    [Fact]
    public void Reports_all_errors_at_once_with_the_route_class()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new BrokenRoute().Build());

        Assert.Equal(3, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.Contains("v0: version must be 1 or greater"));
        Assert.Contains(ex.Errors, e => e.Contains("v0: stage key 'legal' is used more than once"));
        Assert.Contains(ex.Errors, e => e.Contains("v0: stage 'seal': timeout must be positive"));
        Assert.Contains("Route 'contract' (BrokenRoute)", ex.Message);
    }

    private sealed class ContractRoute : SigningRoute
    {
        public override string Code => "contract";

        // Порядок объявления не важен: определения возвращаются по возрастанию версии.
        public override IReadOnlyList<RouteVersion> Versions { get; } =
        [
            new(2, new LegalStage(), new DirectorStage()),
            new(1, new LegalStage()),
        ];
    }

    private sealed class BrokenRoute : SigningRoute
    {
        public override string Code => "contract";

        public override IReadOnlyList<RouteVersion> Versions { get; } =
        [
            new(0, new LegalStage(), new LegalStage(), new NoTimeStage()),
        ];
    }

    private sealed class LegalStage : RouteStage<FakeSigningStageHandler>
    {
        public override string Key => "legal";

        public override TimeSpan Timeout => TimeSpan.FromDays(3);

        public override object? Settings => new FakeSigningSettings("legal");
    }

    private sealed class DirectorStage : RouteStage<FakeSigningStageHandler>
    {
        public override string Key => "director";

        public override TimeSpan Timeout => TimeSpan.FromDays(5);

        public override object? Settings => new FakeSigningSettings("director");

        public override Func<DocumentContext, bool>? Condition => doc => doc.GetDecimal("amount") > 10_000;
    }

    private sealed class NoTimeStage : RouteStage<FakeSigningStageHandler>
    {
        public override string Key => "seal";

        public override TimeSpan Timeout => TimeSpan.Zero;
    }
}
