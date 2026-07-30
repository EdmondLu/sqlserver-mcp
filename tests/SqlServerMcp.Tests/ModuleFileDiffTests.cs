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
        Assert.Equal("compact", diff.Mode);
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
        Assert.Equal("compact", diff.Mode);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Contains(hunk.DatabaseLines, line => line.LineNumber == 4 && line.Changed && line.Text.Contains("SELECT B = 2"));
        Assert.Contains(hunk.FileLines, line => line.LineNumber == 4 && line.Changed && line.Text.Contains("SELECT B = 20"));
        Assert.Contains(hunk.DatabaseLines, line => line.LineNumber == 3 && !line.Changed);
        Assert.Contains(hunk.FileLines, line => line.LineNumber == 5 && !line.Changed);
    }

    [Fact]
    public void BuildLineDiff_SummaryModeReturnsRangesWithoutLineText()
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

        var diff = SqlMetadataService.BuildLineDiff(
            databaseDefinition,
            fileText,
            contextLines: 1,
            diffMode: "summary",
            maxHunks: null,
            maxDiffLinesPerSide: null);

        Assert.False(diff.Equal);
        Assert.Equal("summary", diff.Mode);
        Assert.False(diff.Truncated);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(3, hunk.DatabaseStartLine);
        Assert.Equal(5, hunk.DatabaseEndLine);
        Assert.Empty(hunk.DatabaseLines);
        Assert.Empty(hunk.FileLines);
        Assert.Equal(new(1, 1, 0), SqlMetadataService.BuildChangedLineSummary(diff));
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
        var firstBodyDifference = SqlMetadataService.FindFirstBodyDifference(databaseDefinition, fileText);

        Assert.Equal(databaseNormalized, fileNormalized);
        Assert.Null(firstBodyDifference);
    }

    [Fact]
    public void NormalizeSqlModuleTokens_DistinguishesCommentOnlyFromBodyChanges()
    {
        const string left = """
                            CREATE PROCEDURE dbo.Sample
                            AS
                            -- old comment
                            SELECT Value = 1;
                            """;
        const string right = """
                             CREATE OR ALTER PROCEDURE dbo.Sample AS
                             /* new comment */
                             SELECT Value=1;
                             GO
                             """;
        const string changed = """
                               CREATE OR ALTER PROCEDURE dbo.Sample AS
                               SELECT Value=2;
                               """;

        Assert.Equal(
            SqlMetadataService.NormalizeSqlModuleTokens(left, includeComments: false),
            SqlMetadataService.NormalizeSqlModuleTokens(right, includeComments: false));
        Assert.NotEqual(
            SqlMetadataService.NormalizeSqlModuleTokens(left, includeComments: true),
            SqlMetadataService.NormalizeSqlModuleTokens(right, includeComments: true));
        Assert.NotEqual(
            SqlMetadataService.NormalizeSqlModuleTokens(left, includeComments: false),
            SqlMetadataService.NormalizeSqlModuleTokens(changed, includeComments: false));
    }

    [Theory]
    [InlineData(null, "shape", false, false)]
    [InlineData("write_contract", "write_contract", false, true)]
    [InlineData("performance", "performance", true, false)]
    [InlineData("full", "full", true, true)]
    public void BuildDescribeTablePreset_ReturnsExpectedIncludes(
        string? input,
        string expectedMode,
        bool expectedIndexes,
        bool expectedDefaults)
    {
        var preset = SqlMetadataService.BuildDescribeTablePreset(input);

        Assert.Equal(expectedMode, preset.Mode);
        Assert.Equal(expectedIndexes, preset.IncludeIndexes);
        Assert.Equal(expectedDefaults, preset.IncludeDefaults);
    }

    [Theory]
    [InlineData(true, true, true, true, "exact_match")]
    [InlineData(false, true, true, true, "wrapper_only")]
    [InlineData(false, false, true, true, "format_only")]
    [InlineData(false, false, true, false, "comment_only")]
    [InlineData(false, false, false, false, "body_changed")]
    public void ClassifyModuleFileDifference_ReturnsThreeLayerClassification(
        bool exactMatch,
        bool bodyMatch,
        bool semanticMatch,
        bool formatAndCommentMatch,
        string expected)
    {
        Assert.Equal(
            expected,
            SqlMetadataService.ClassifyModuleFileDifference(
                exactMatch,
                bodyMatch,
                semanticMatch,
                formatAndCommentMatch));
    }

    [Fact]
    public void FindFirstBodyDifference_ReturnsOriginalLineNumbersAfterWrapperNormalization()
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
                                SELECT 2
                                GO
                                """;

        var firstBodyDifference = SqlMetadataService.FindFirstBodyDifference(databaseDefinition, fileText);

        Assert.NotNull(firstBodyDifference);
        Assert.Equal(3, firstBodyDifference.BodyLine);
        Assert.Equal(3, firstBodyDifference.DatabaseLine);
        Assert.Equal(7, firstBodyDifference.FileLine);

        var diff = SqlMetadataService.BuildLineDiff(
            databaseDefinition,
            fileText,
            contextLines: 1,
            diffMode: "summary",
            maxHunks: null,
            maxDiffLinesPerSide: null);
        var nextActions = SqlMetadataService.BuildModuleCompareNextActions(firstBodyDifference, diff);

        Assert.Contains(nextActions, action => action.Contains("database line 3", StringComparison.Ordinal)
                                               && action.Contains("local file line 7", StringComparison.Ordinal));
        Assert.Contains(nextActions, action => action.Contains("diffMode=compact", StringComparison.Ordinal));
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

            var discovery = SqlMetadataService.FindModuleSqlFileDiscovery(root, "dbo", "SampleProc", null, null, null);

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

            var discovery = SqlMetadataService.FindModuleSqlFileDiscovery(root, "dbo", "SampleProc", null, null, null);

            Assert.True(discovery.Ambiguous);
            Assert.Null(discovery.SelectedCandidate);
            Assert.Equal(2, discovery.CandidateCount);
            Assert.Equal(discovery.Candidates[0].Score, discovery.Candidates[1].Score);
            Assert.NotEmpty(discovery.SuggestedPatterns);
            Assert.All(discovery.Candidates, candidate => Assert.Contains("exact object file name", candidate.Reasons));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindModuleSqlFileDiscovery_AppliesExcludePatterns()
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
                ["**/*.sql"],
                ["archive/**"],
                null);

            var candidate = Assert.Single(discovery.Candidates);
            Assert.Equal("procedures/SampleProc.sql", candidate.RelativePath);
            Assert.Equal(["archive/**"], discovery.ExcludePatterns);
            Assert.Same(candidate, discovery.SelectedCandidate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeRepoCompareExcludePatterns_CombinesConfiguredAndRequestedPatterns()
    {
        var patterns = SqlMetadataService.MergeRepoCompareExcludePatterns(
            ["backup/**", "domain2/**"],
            ["domain2/**", "archive/**"]);

        Assert.Equal(["backup/**", "domain2/**", "archive/**"], patterns);
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
                null,
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
