using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Tests.Model;

public class DocumentContextTests
{
    private static DocumentContext Doc(string? json, string? hash = null) => new("doc-1", "contract", json, hash);

    [Fact]
    public void Reads_typed_attributes()
    {
        var doc = Doc("""{"amount": 15000.50, "count": 3, "urgent": true, "dept": "legal"}""");

        Assert.Equal(15000.50m, doc.GetDecimal("amount"));
        Assert.Equal(3L, doc.GetInt64("count"));
        Assert.True(doc.GetBoolean("urgent"));
        Assert.Equal("legal", doc.GetString("dept"));
    }

    [Fact]
    public void Parses_numbers_and_booleans_from_strings_in_invariant_culture()
    {
        var doc = Doc("""{"amount": "15000.50", "count": "3", "urgent": "false"}""");

        Assert.Equal(15000.50m, doc.GetDecimal("amount"));
        Assert.Equal(3L, doc.GetInt64("count"));
        Assert.False(doc.GetBoolean("urgent"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void Empty_attributes_have_no_values(string? json)
    {
        var doc = Doc(json);

        Assert.False(doc.Has("amount"));
        Assert.Null(doc.GetDecimalOrNull("amount"));
    }

    [Fact]
    public void Strict_getter_throws_on_missing_or_null_attribute()
    {
        var doc = Doc("""{"amount": null}""");

        Assert.False(doc.Has("amount"));
        Assert.Throws<DocumentAttributeException>(() => doc.GetDecimal("amount"));
        Assert.Throws<DocumentAttributeException>(() => doc.GetString("other"));
    }

    [Fact]
    public void Nullable_getter_throws_on_malformed_value()
    {
        var doc = Doc("""{"amount": "abc", "flag": 1, "obj": {}}""");

        Assert.Throws<DocumentAttributeException>(() => doc.GetDecimalOrNull("amount"));
        Assert.Throws<DocumentAttributeException>(() => doc.GetBooleanOrNull("flag"));
        Assert.Throws<DocumentAttributeException>(() => doc.GetStringOrNull("obj"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("42")]
    public void Rejects_attributes_that_are_not_a_json_object(string json)
    {
        Assert.Throws<DocumentAttributeException>(() => Doc(json));
    }

    [Fact]
    public void Blank_content_hash_is_treated_as_absent()
    {
        Assert.Null(Doc(null, "  ").ContentHash);
        Assert.Equal("abc", Doc(null, "abc").ContentHash);
    }
}
