using SqlServerMcp.Configuration;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class ConfigTextSearchTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Sample WHERE Id = 1", "sql")]
    [InlineData("function save() { return true; }", "javascript")]
    [InlineData("{ \"name\": \"sample\" }", "json")]
    [InlineData("<root><item /></root>", "xml")]
    [InlineData("plain text", "text")]
    public void DetectContentKind_ClassifiesCommonConfigText(string text, string expectedKind)
    {
        Assert.Equal(expectedKind, SqlMetadataService.DetectContentKind(text));
    }

    [Fact]
    public void TextSearchTargetOptions_NormalizeLabelColumns()
    {
        var options = new TextSearchOptions
        {
            Targets =
            [
                new TextSearchTargetOptions
                {
                    Table = "Config",
                    TextColumn = "script",
                    LabelColumns = ["page_name", "page_name", " control_name ", ""],
                    ContentKind = " sql "
                }
            ]
        };

        options.Normalize();

        var target = Assert.Single(options.Targets);
        Assert.Equal(["page_name", "control_name"], target.LabelColumns);
        Assert.Equal("sql", target.ContentKind);
    }

    [Fact]
    public void FindTextSearchMatch_PrefersTextThenMetadataColumns()
    {
        var match = SqlMetadataService.FindTextSearchMatch(
            ["reserve"],
            [
                new SqlMetadataService.TextSearchMatchInput("sql_text", "text", "SELECT 1"),
                new SqlMetadataService.TextSearchMatchInput("sql_name", "name", "reserve 保存句柄")
            ]);

        Assert.NotNull(match);
        Assert.Equal("sql_name", match.Column);
        Assert.Equal("name", match.Kind);
        Assert.Equal("reserve", match.Term);
        Assert.Equal(1, match.Start);
    }

    [Fact]
    public void BuildTextSearchSnippet_UsesOneBasedMatchPosition()
    {
        var snippet = SqlMetadataService.BuildTextSearchSnippet(
            "before before reserve after after",
            matchStart: 15,
            matchLength: 7,
            maxLength: 18);

        Assert.Contains("reserve", snippet);
    }
}
