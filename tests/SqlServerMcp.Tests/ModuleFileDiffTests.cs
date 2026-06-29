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
        Assert.False(diff.Truncated);
        Assert.Equal(0, diff.OmittedHunkCount);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Contains(hunk.DatabaseLines, line => line.LineNumber == 4 && line.Changed && line.Text.Contains("SELECT B = 2"));
        Assert.Contains(hunk.FileLines, line => line.LineNumber == 4 && line.Changed && line.Text.Contains("SELECT B = 20"));
        Assert.Contains(hunk.DatabaseLines, line => line.LineNumber == 3 && !line.Changed);
        Assert.Contains(hunk.FileLines, line => line.LineNumber == 5 && !line.Changed);
    }

    [Fact]
    public void BuildLineDiff_SplitsHeaderAndTrailingGoNoiseIntoSmallHunks()
    {
        const string databaseDefinition = """
                                          CREATE   PROCEDURE dbo.Sample
                                          AS
                                          BEGIN
                                              SELECT A = 1
                                              SELECT B = 2
                                          END
                                          """;
        const string fileText = """
                                SET ANSI_NULLS ON
                                GO
                                SET QUOTED_IDENTIFIER ON
                                GO
                                CREATE OR ALTER PROCEDURE dbo.Sample
                                AS
                                BEGIN
                                    SELECT A = 1
                                    SELECT B = 2
                                END
                                GO
                                """;

        var diff = SqlMetadataService.BuildLineDiff(databaseDefinition, fileText, 1);

        Assert.False(diff.Equal);
        Assert.False(diff.Truncated);
        Assert.True(diff.Hunks.Length <= 3);
        Assert.True(diff.Hunks.Sum(hunk => hunk.DatabaseLines.Length + hunk.FileLines.Length) < 20);
        Assert.Contains(diff.Hunks, hunk => hunk.FileLines.Any(line => line.Text.Contains("CREATE OR ALTER PROCEDURE", StringComparison.Ordinal)));
        Assert.Contains(diff.Hunks, hunk => hunk.FileLines.Any(line => line.Text == "GO" && line.Changed));
    }

    [Fact]
    public void NormalizeSqlModuleTextForComparison_IgnoresCommonScriptWrapperNoise()
    {
        const string databaseDefinition = """
                                          CREATE   PROCEDURE dbo.Sample
                                          AS
                                          SELECT 1
                                          """;
        const string fileText = """
                                SET ANSI_NULLS ON
                                GO
                                SET QUOTED_IDENTIFIER ON
                                GO
                                CREATE OR ALTER PROCEDURE dbo.Sample
                                AS
                                SELECT 1
                                GO
                                """;

        var databaseNormalized = SqlMetadataService.NormalizeSqlModuleTextForComparison(databaseDefinition);
        var fileNormalized = SqlMetadataService.NormalizeSqlModuleTextForComparison(fileText);

        Assert.Equal(databaseNormalized, fileNormalized);
    }

    [Fact]
    public void FindModuleSqlFileDiscovery_SelectsSchemaQualifiedFile()
    {
        var root = CreateTempRoot();
        try
        {
            var procedures = Directory.CreateDirectory(Path.Combine(root, "procedures"));
            File.WriteAllText(Path.Combine(procedures.FullName, "dbo.SampleProc.sql"), "CREATE PROC dbo.SampleProc AS SELECT 1");
            File.WriteAllText(Path.Combine(procedures.FullName, "SampleProc_old.sql"), "CREATE PROC dbo.SampleProc AS SELECT 2");

            var discovery = SqlMetadataService.FindModuleSqlFileDiscovery(root, "dbo", "SampleProc", null, null);

            Assert.False(discovery.Ambiguous);
            Assert.NotNull(discovery.SelectedCandidate);
            Assert.EndsWith("dbo.SampleProc.sql", discovery.SelectedCandidate.Path);
            Assert.Contains("schema-qualified file name", discovery.SelectedCandidate.Reasons);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindModuleSqlFileDiscovery_ReturnsAmbiguousForTiedTopCandidates()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.WriteAllText(Path.Combine(root, "a", "SampleProc.sql"), "SELECT 1");
            File.WriteAllText(Path.Combine(root, "b", "SampleProc.sql"), "SELECT 2");

            var discovery = SqlMetadataService.FindModuleSqlFileDiscovery(root, "dbo", "SampleProc", null, null);

            Assert.True(discovery.Ambiguous);
            Assert.Null(discovery.SelectedCandidate);
            Assert.Equal(2, discovery.CandidateCount);
            Assert.Equal(discovery.Candidates[0].Score, discovery.Candidates[1].Score);
            Assert.All(discovery.Candidates, candidate => Assert.Contains("exact object file name", candidate.Reasons));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindModuleSqlFileDiscovery_AppliesPathPatterns()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "procedures"));
            Directory.CreateDirectory(Path.Combine(root, "archive"));
            File.WriteAllText(Path.Combine(root, "procedures", "SampleProc.sql"), "SELECT 1");
            File.WriteAllText(Path.Combine(root, "archive", "SampleProc.sql"), "SELECT 2");

            var discovery = SqlMetadataService.FindModuleSqlFileDiscovery(
                root,
                "dbo",
                "SampleProc",
                ["procedures/*.sql"],
                null);

            var candidate = Assert.Single(discovery.Candidates);
            Assert.Equal("procedures/SampleProc.sql", candidate.RelativePath);
            Assert.Same(candidate, discovery.SelectedCandidate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SqlServerMcpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
