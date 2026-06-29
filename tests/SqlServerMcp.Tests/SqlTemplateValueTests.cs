using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class SqlTemplateValueTests
{
    [Fact]
    public void ApplySqlTemplateValues_ReplacesNumericPlaceholders()
    {
        var application = SqlMetadataService.ApplySqlTemplateValues(
            "SELECT 1 AS id WHERE {0}",
            new Dictionary<string, object?>
            {
                ["0"] = "1=1"
            });

        Assert.Equal("SELECT 1 AS id WHERE 1=1", application.Sql);
        var replacement = Assert.Single(application.Replacements);
        Assert.Equal("{0}", replacement.Placeholder);
        Assert.True(replacement.Applied);
        Assert.Equal(1, replacement.Occurrences);
    }

    [Fact]
    public void ApplySqlTemplateValues_RejectsInvalidPlaceholder()
    {
        var ex = Assert.Throws<SqlMcpException>(() => SqlMetadataService.ApplySqlTemplateValues(
            "SELECT 1 AS id WHERE {where}",
            new Dictionary<string, object?>
            {
                ["where"] = "1=1"
            }));

        Assert.Equal(ErrorCodes.ConfigInvalid, ex.ErrorCode);
    }
}
