using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class ModuleDefinitionSliceTests
{
    [Fact]
    public void BuildModuleDefinitionSlice_ReturnsKeywordContextWithLineNumbers()
    {
        const string definition = """
                                  CREATE PROC dbo.Sample
                                  AS
                                  BEGIN
                                      SELECT Id
                                      FROM dbo.Customer
                                      WHERE Name = @name
                                  END
                                  """;

        var slice = SqlMetadataService.BuildModuleDefinitionSlice(
            definition,
            "Customer",
            null,
            null,
            1,
            500);

        Assert.True(slice.IsPartial);
        Assert.Equal("keyword", slice.Reason);
        Assert.Equal([5], slice.MatchedLines);
        Assert.Equal(4, slice.StartLine);
        Assert.Equal(6, slice.EndLine);
        Assert.Contains("FROM dbo.Customer", slice.Definition);
        Assert.All(slice.Lines, line => Assert.InRange(line.LineNumber, 4, 6));
    }

    [Fact]
    public void BuildModuleDefinitionSlice_ReturnsLineRange()
    {
        const string definition = "line 1\nline 2\nline 3\nline 4";

        var slice = SqlMetadataService.BuildModuleDefinitionSlice(
            definition,
            null,
            2,
            3,
            null,
            500);

        Assert.Equal("line 2" + Environment.NewLine + "line 3", slice.Definition);
        Assert.Equal(2, slice.StartLine);
        Assert.Equal(3, slice.EndLine);
        Assert.True(slice.IsPartial);
    }

    [Fact]
    public void BuildModuleDefinitionSlice_DoesNotTruncateFullDefinition()
    {
        var definition = string.Join('\n', Enumerable.Range(1, 10).Select(line => $"line {line}"));

        var slice = SqlMetadataService.BuildModuleDefinitionSlice(
            definition,
            null,
            null,
            null,
            null,
            3);

        Assert.False(slice.IsPartial);
        Assert.False(slice.Truncated);
        Assert.Equal(10, slice.SelectedLineCount);
        Assert.Contains("line 10", slice.Definition);
    }

    [Fact]
    public void BuildModuleDefinitionSlice_RejectsInvalidLineRange()
    {
        var ex = Assert.Throws<SqlMcpException>(() => SqlMetadataService.BuildModuleDefinitionSlice(
            "line 1\nline 2",
            null,
            3,
            null,
            null,
            500));

        Assert.Equal(ErrorCodes.ConfigInvalid, ex.ErrorCode);
    }
}
