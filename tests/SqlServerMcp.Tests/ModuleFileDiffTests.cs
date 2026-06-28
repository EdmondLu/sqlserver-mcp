using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class ModuleFileDiffTests
{
    [Fact]
    public void BuildLineDiff_ReturnsEqualForSameText()
    {
        const string text = "CREATE PROC dbo.Sample\nAS\nSELECT 1";

        var diff = SqlMetadataService.BuildLineDiff(text, text, 2);

        Assert.True(diff.Equal);
        Assert.Null(diff.FirstDifferentLine);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void BuildLineDiff_ReturnsChangedBlockWithContext()
    {
        const string databaseDefinition = """
                                          CREATE PROC dbo.Sample
                                          AS
                                          SELECT A = 1
                                          SELECT B = 2
                                          SELECT C = 3
                                          """;
        const string fileText = """
                                CREATE PROC dbo.Sample
                                AS
                                SELECT A = 1
                                SELECT B = 20
                                SELECT C = 3
                                """;

        var diff = SqlMetadataService.BuildLineDiff(databaseDefinition, fileText, 1);

        Assert.False(diff.Equal);
        Assert.Equal(4, diff.FirstDifferentLine);
        Assert.Equal(1, diff.DatabaseChangedLineCount);
        Assert.Equal(1, diff.FileChangedLineCount);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Contains(hunk.DatabaseLines, line => line.LineNumber == 4 && line.Changed && line.Text.Contains("SELECT B = 2"));
        Assert.Contains(hunk.FileLines, line => line.LineNumber == 4 && line.Changed && line.Text.Contains("SELECT B = 20"));
        Assert.Contains(hunk.DatabaseLines, line => line.LineNumber == 3 && !line.Changed);
        Assert.Contains(hunk.FileLines, line => line.LineNumber == 5 && !line.Changed);
    }
}
