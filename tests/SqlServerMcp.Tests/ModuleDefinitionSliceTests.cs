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

    [Fact]
    public void BuildModuleDefinitionSlice_SupportsMultipleKeywordsAndOccurrence()
    {
        const string definition = """
                                  line 1
                                  UPDATE A SET value=1
                                  line 3
                                  INSERT INTO B(id) SELECT 1
                                  line 5
                                  UPDATE A SET value=2
                                  """;

        var slice = SqlMetadataService.BuildModuleDefinitionSlice(
            definition,
            null,
            ["UPDATE A", "INSERT INTO"],
            null,
            null,
            0,
            0,
            0,
            10,
            2,
            true,
            500);

        Assert.Equal("keywords", slice.Reason);
        Assert.Equal([4], slice.MatchedLines);
        Assert.Equal("INSERT INTO B(id) SELECT 1", slice.Definition);
    }

    [Fact]
    public void BuildModuleDefinitionSlice_PreservesDiscontinuousSliceBoundaries()
    {
        var definition = string.Join('\n', Enumerable.Range(1, 12).Select(line => $"line {line}"));

        var slice = SqlMetadataService.BuildModuleDefinitionSlice(
            definition,
            null,
            ["line 2", "line 10"],
            null,
            null,
            0,
            0,
            0,
            10,
            null,
            true,
            500);

        Assert.Equal(2, slice.Slices.Length);
        Assert.Collection(
            slice.Slices,
            first =>
            {
                Assert.Equal(2, first.StartLine);
                Assert.Equal(2, first.EndLine);
                Assert.Equal("line 2", first.Definition);
            },
            second =>
            {
                Assert.Equal(10, second.StartLine);
                Assert.Equal(10, second.EndLine);
                Assert.Equal("line 10", second.Definition);
            });
        Assert.Contains("-- ... omitted lines 3-9 ...", slice.Definition);
        Assert.Contains("line 2", slice.Definition);
        Assert.Contains("line 10", slice.Definition);
    }
}
