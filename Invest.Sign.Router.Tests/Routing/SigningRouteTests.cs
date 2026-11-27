using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;

namespace Invest.Sign.Router.Tests.Routing;

public class SigningRouteTests
{
    [Fact]
    public void Builds_the_same_definition_as_the_fluent_api()
    {
        var route = new ContractRoute().Build();

        Assert.Equal(new RouteKey("contract", 2), route.Key);
        Assert.Collection(
            route.Stages,
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
                Assert.True(s.IsConditional);
            });
    }

    [Fact]
    public void Stage_condition_is_evaluated_against_document_attributes()
    {
        var route = new ContractRoute().Build();

        var small = new DocumentContext("doc-1", "contract", """{"amount":100}""", null);
        var large = new DocumentContext("doc-2", "contract", """{"amount":50000}""", null);

        Assert.Null(route.NextStage("legal", small));
        Assert.Equal("director", route.NextStage("legal", large)?.Key);
    }

    [Fact]
    public void Reports_errors_like_the_fluent_api()
    {
        var ex = Assert.Throws<RouteDefinitionException>(() => new BrokenRoute().Build());

        Assert.Contains(ex.Errors, e => e.Contains("'legal' is used more than once"));
        Assert.Contains(ex.Errors, e => e.Contains("timeout must be positive"));
        Assert.Contains("Route 'contract' v1", ex.Message);
    }

    private sealed class ContractRoute : SigningRoute
    {
        public override string Code => "contract";

        public override int Version => 2;

        public override IReadOnlyList<RouteStage> Stages { get; } = [new LegalStage(), new DirectorStage()];
    }

    private sealed class BrokenRoute : SigningRoute
    {
        public override string Code => "contract";

        public override int Version => 1;

        public override IReadOnlyList<RouteStage> Stages { get; } = [new LegalStage(), new LegalStage(), new NoTimeStage()];
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
