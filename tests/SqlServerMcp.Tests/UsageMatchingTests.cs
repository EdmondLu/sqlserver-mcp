using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class UsageMatchingTests
{
    [Fact]
    public void AnalyzeUsageMatches_TreatsUnderscoresAsLiteralIdentifierCharacters()
    {
        var source = new SqlMetadataService.UsageModuleSource(
            "dbo",
            "Caller",
            "procedure",
            "SQL_STORED_PROCEDURE",
            """
            CREATE OR ALTER PROCEDURE dbo.Caller
            AS
            EXEC dbo.spappDSCM_domain_puRequest_UnAudit;
            EXEC dbo.spappDSCMXdomainXpuRequestXUnAudit;
            """,
            DateTime.UtcNow);

        var matches = SqlMetadataService.AnalyzeUsageMatches(
            source,
            "spappDSCM_domain_puRequest_UnAudit",
            "dbo",
            "ranked");

        var match = Assert.Single(matches);
        Assert.Equal("two_part_identifier", match.MatchKind);
        Assert.Equal(3, match.LineNumber);
        Assert.True(match.ColumnNumber > 0);
        Assert.Contains("spappDSCM_domain_puRequest_UnAudit", match.Context, StringComparison.Ordinal);
        Assert.True(match.Confidence >= 0.99);
    }

    [Fact]
    public void AnalyzeUsageMatches_ClassifiesDynamicSqlSeparately()
    {
        var source = new SqlMetadataService.UsageModuleSource(
            "dbo",
            "Caller",
            "procedure",
            "SQL_STORED_PROCEDURE",
            "EXEC(N'EXEC reserve_yarn_suite_ids');",
            DateTime.UtcNow);

        var match = Assert.Single(SqlMetadataService.AnalyzeUsageMatches(
            source,
            "reserve_yarn_suite_ids",
            null,
            "ranked"));

        Assert.Equal("dynamic_sql_string", match.MatchKind);
        Assert.Equal("reserve_yarn_suite_ids", match.MatchedText);
    }

    [Fact]
    public void AnalyzeUsageMatches_DemotesCommentsAndExcludesThemFromExactIdentifierMode()
    {
        var source = new SqlMetadataService.UsageModuleSource(
            "dbo",
            "Caller",
            "procedure",
            "SQL_STORED_PROCEDURE",
            "-- EXEC dbo.TargetProc;\nSELECT 1;",
            DateTime.UtcNow);

        var ranked = Assert.Single(SqlMetadataService.AnalyzeUsageMatches(
            source,
            "TargetProc",
            "dbo",
            "ranked"));
        var exact = SqlMetadataService.AnalyzeUsageMatches(
            source,
            "TargetProc",
            "dbo",
            "exact_identifier");

        Assert.Equal("comment_text", ranked.MatchKind);
        Assert.True(ranked.Confidence < 0.5);
        Assert.Empty(exact);
    }

    [Theory]
    [InlineData(null, "ranked")]
    [InlineData("exact_identifier", "exact_identifier")]
    [InlineData("REGEX", "regex")]
    public void NormalizeUsageMatchMode_ReturnsSupportedModes(string? input, string expected)
    {
        Assert.Equal(expected, SqlMetadataService.NormalizeUsageMatchMode(input));
    }
}
