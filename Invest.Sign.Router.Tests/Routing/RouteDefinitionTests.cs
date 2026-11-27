using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;

namespace Invest.Sign.Router.Tests.Routing;

public class RouteDefinitionTests
{
    private static readonly RouteDefinition Route3 = new TestRoute("contract", new RouteVersion(
        1,
        new SigningStage("a", "x", TimeSpan.FromHours(1)),
        new SigningStage("b", "x", TimeSpan.FromHours(1), when: d => d.GetBoolean("needs-b")),
        new SigningStage("c", "x", TimeSpan.FromHours(1)))).Build().Single();

    private static DocumentContext Doc(string json) => new("doc-1", "contract", json, null);

    [Fact]
    public void Next_stage_from_start_is_first_applicable()
    {
        Assert.Equal("a", Route3.NextStage(null, Doc("{}"))?.Key);
    }

    [Fact]
    public void Conditional_stage_is_entered_when_condition_holds()
    {
        Assert.Equal("b", Route3.NextStage("a", Doc("""{"needs-b": true}"""))?.Key);
    }

    [Fact]
    public void Conditional_stage_is_skipped_when_condition_fails()
    {
        Assert.Equal("c", Route3.NextStage("a", Doc("""{"needs-b": false}"""))?.Key);
    }

    [Fact]
    public void No_next_stage_after_last()
    {
        Assert.Null(Route3.NextStage("c", Doc("{}")));
    }

    [Fact]
    public void Condition_errors_are_wrapped_with_stage_key()
    {
        var ex = Assert.Throws<StageConditionException>(() => Route3.NextStage("a", Doc("{}")));

        Assert.Equal("b", ex.StageKey);
        Assert.IsType<DocumentAttributeException>(ex.InnerException);
    }

    [Fact]
    public void Selection_reports_skipped_stages()
    {
        var selection = Route3.SelectNextStage("a", Doc("""{"needs-b": false}"""));

        Assert.Equal("c", selection.Next?.Key);
        Assert.Equal("b", Assert.Single(selection.Skipped).Key);
    }

    [Fact]
    public void Unknown_current_stage_throws()
    {
        Assert.Throws<ArgumentException>(() => Route3.NextStage("zzz", Doc("{}")));
    }

    [Fact]
    public void Finds_stage_by_key()
    {
        Assert.Equal("b", Route3.FindStage("b")?.Key);
        Assert.Null(Route3.FindStage("zzz"));
    }
}
