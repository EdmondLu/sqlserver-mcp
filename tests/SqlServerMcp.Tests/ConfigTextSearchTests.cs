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
}
