using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class DeploymentVerificationTests
{
    [Fact]
    public void TableScriptAnalyzer_BuildsFinalStructureAcrossCreateAlterAndIndex()
    {
        const string script = """
                              CREATE TABLE dbo.Sample
                              (
                                  Id int IDENTITY(1,1) NOT NULL,
                                  Code nvarchar(50) NULL CONSTRAINT DF_Sample_Code DEFAULT (N''),
                                  Qty decimal(18,2) NOT NULL,
                                  CONSTRAINT PK_Sample PRIMARY KEY CLUSTERED (Id),
                                  CONSTRAINT UQ_Sample_Code UNIQUE NONCLUSTERED (Code),
                                  CONSTRAINT CK_Sample_Qty CHECK (Qty >= 0),
                                  CONSTRAINT FK_Sample_Parent FOREIGN KEY (Id) REFERENCES dbo.Parent(Id)
                              );
                              ALTER TABLE dbo.Sample ADD Enabled bit NOT NULL CONSTRAINT DF_Sample_Enabled DEFAULT (1);
                              CREATE NONCLUSTERED INDEX IX_Sample_Qty ON dbo.Sample(Qty DESC) INCLUDE(Code) WHERE Qty > 0;
                              """;

        var analysis = TableScriptAnalyzer.Analyze(script, "dbo", "Sample");

        Assert.True(analysis.SyntaxValid);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == "error");
        Assert.NotNull(analysis.Table);
        Assert.Equal(4, analysis.Table.Columns.Length);
        Assert.Equal("nvarchar(50)", analysis.Table.Columns.Single(column => column.Name == "Code").TypeSignature);
        Assert.Equal("decimal(18,2)", analysis.Table.Columns.Single(column => column.Name == "Qty").TypeSignature);
        Assert.Equal(3, analysis.Table.Indexes.Length);
        Assert.Equal(2, analysis.Table.KeyConstraints.Length);
        Assert.Equal(2, analysis.Table.DefaultConstraints.Length);
        Assert.Single(analysis.Table.CheckConstraints);
        Assert.Single(analysis.Table.ForeignKeys);
        var index = analysis.Table.Indexes.Single(value => value.Name == "IX_Sample_Qty");
        Assert.True(index.KeyColumns.Single().Descending);
        Assert.Equal(["Code"], index.IncludedColumns);
    }

    [Fact]
    public void TableScriptAnalyzer_OptionallyComparesTableAndColumnDescriptions()
    {
        const string script = """
                              CREATE TABLE dbo.Sample (Id int NOT NULL);
                              EXEC sys.sp_addextendedproperty
                                  @name=N'MS_Description', @value=N'Sample table',
                                  @level0type=N'SCHEMA', @level0name=N'dbo',
                                  @level1type=N'TABLE', @level1name=N'Sample';
                              EXEC sys.sp_addextendedproperty
                                  @name=N'MS_Description', @value=N'Primary id',
                                  @level0type=N'SCHEMA', @level0name=N'dbo',
                                  @level1type=N'TABLE', @level1name=N'Sample',
                                  @level2type=N'COLUMN', @level2name=N'Id';
                              """;
        var analysis = TableScriptAnalyzer.Analyze(script, "dbo", "Sample");
        var expected = Assert.IsType<TableSchemaModel>(analysis.Table);
        var actual = expected with
        {
            Description = "Different table text",
            Columns = [expected.Columns[0] with { Description = "Different column text" }]
        };

        Assert.Equal("Sample table", expected.Description);
        Assert.Equal("Primary id", expected.Columns[0].Description);
        Assert.True(TableScriptAnalyzer.Compare(expected, actual).Equivalent);

        var comparison = TableScriptAnalyzer.Compare(expected, actual, includeDescriptions: true);
        Assert.False(comparison.Equivalent);
        Assert.Contains(comparison.Differences, difference => difference.Section == "table_description");
        Assert.Contains(comparison.Differences, difference => difference.Section == "column_description");
    }

    [Fact]
    public void TableScriptAnalyzer_CompareIgnoresGeneratedConstraintNamesWhenLocalNameIsAbsent()
    {
        var expected = new TableSchemaModel(
            "dbo",
            "Sample",
            [new(1, "Id", "int", false, false, false, null, null, "0")],
            [],
            [],
            [new(null, "Id", "0")],
            [],
            []);
        var actual = expected with
        {
            Columns = [expected.Columns[0] with { DefaultConstraintName = "DF__Sample__Id__1234", DefaultDefinition = "((0))" }],
            DefaultConstraints = [new("DF__Sample__Id__1234", "Id", "((0))")]
        };

        var comparison = TableScriptAnalyzer.Compare(expected, actual);

        Assert.True(comparison.Equivalent);
        Assert.Empty(comparison.Differences);
    }

    [Fact]
    public void TableScriptAnalyzer_CompareNormalizesEquivalentSqlServerExpressions()
    {
        var expected = EmptyTable(
        [
            new(1, "CreatedAt", "datetime", false, false, false, null, null, "dbo.fn()"),
            new(2, "Status", "int", false, false, false, null, null, null)
        ]) with
        {
            DefaultConstraints = [new(null, "CreatedAt", "dbo.fn()")],
            CheckConstraints = [new(null, "Status IN (01, 2.0, 3) AND /* local */ CreatedAt IS NOT NULL")]
        };
        var actual = expected with
        {
            Columns =
            [
                expected.Columns[0] with { DefaultDefinition = "([dbo].[fn]())" },
                expected.Columns[1]
            ],
            DefaultConstraints = [new("DF__Sample__Created", "CreatedAt", "([dbo].[fn]())")],
            CheckConstraints =
            [
                new(
                    "CK__Sample__Status",
                    "(([CreatedAt] IS NOT NULL) AND (([Status]=(3) OR [Status]=(2)) OR [Status]=(1.00)))")
            ]
        };

        var comparison = TableScriptAnalyzer.Compare(expected, actual);

        Assert.True(comparison.Equivalent);
        Assert.Empty(comparison.Differences);
    }

    [Fact]
    public void TableScriptAnalyzer_CompareReportsChangedColumnAndUnexpectedIndex()
    {
        var expected = EmptyTable([new(1, "Id", "int", false, false, false, null, null, null)]);
        var actual = EmptyTable([new(1, "Id", "bigint", false, false, false, null, null, null)]) with
        {
            Indexes = [new("IX_Extra", "NONCLUSTERED", false, false, [new("Id", false)], [], null)]
        };

        var comparison = TableScriptAnalyzer.Compare(expected, actual);

        Assert.False(comparison.Equivalent);
        Assert.Contains(comparison.Differences, difference => difference.Section == "column" && difference.Kind == "changed");
        Assert.Contains(comparison.Differences, difference => difference.Section == "index" && difference.Kind == "unexpected");
    }

    [Fact]
    public void ConfigPatchAnalyzer_ParsesAliasReplaceAndLiteralAssignments()
    {
        const string script = """
                              DECLARE @old nvarchar(max)=N'old-token';
                              DECLARE @new nvarchar(max)=N'new-token';
                              UPDATE U
                              SET U.sql_text=REPLACE(U.sql_text, @old, @new),
                                  U.title=N'Updated title'
                              FROM dbo.UiConfig U
                              WHERE U.page_key=N'page-1' AND U.sql_text LIKE N'%old-token%';
                              """;

        var analysis = ConfigPatchAnalyzer.Analyze(script);

        Assert.True(analysis.SyntaxValid);
        Assert.Equal(2, analysis.Patches.Length);
        var replace = analysis.Patches.Single(patch => patch.Column == "sql_text");
        Assert.Equal("dbo", replace.Schema);
        Assert.Equal("UiConfig", replace.Table);
        Assert.Equal("replace", replace.Operation);
        Assert.Equal("old-token", replace.OldValue);
        Assert.Equal("new-token", replace.NewValue);
        Assert.Contains(replace.Predicates, predicate => predicate.Column == "page_key" && predicate.Value == "page-1");
        var literal = analysis.Patches.Single(patch => patch.Column == "title");
        Assert.Equal("set_literal", literal.Operation);
        Assert.Equal("Updated title", literal.ExpectedValue);
    }

    [Theory]
    [InlineData("new-token", "deployed")]
    [InlineData("old-token", "not_deployed")]
    [InlineData("old-token new-token", "partially_deployed")]
    [InlineData("unrelated", "inconclusive")]
    public void ConfigPatchAnalyzer_EvaluatesReplaceState(string current, string expectedState)
    {
        var patch = new ConfigPatchExpectation(
            "dbo",
            "UiConfig",
            "sql_text",
            [new("page_key", "page-1")],
            "replace",
            null,
            "old-token",
            "new-token",
            1,
            1);

        Assert.Equal(expectedState, ConfigPatchAnalyzer.EvaluateState(patch, current));
    }

    [Fact]
    public void FinalizeTableComparisonResult_KeepsLeadingDifferencesWithinBudget()
    {
        var differences = Enumerable.Range(1, 8)
            .Select(index => new
            {
                section = "column",
                name = $"Column{index}",
                kind = "changed",
                expected = new string('E', 320),
                actual = new string('A', 320)
            })
            .ToArray();
        var result = new Dictionary<string, object?>
        {
            ["schema"] = "dbo",
            ["name"] = "Sample",
            ["filePath"] = @"C:\work\Sample.sql",
            ["status"] = "definition_mismatch",
            ["deploymentState"] = "definition_mismatch",
            ["equivalent"] = false,
            ["staticValidationState"] = "valid",
            ["warnings"] = Array.Empty<string>(),
            ["descriptionsCompared"] = false,
            ["differenceCount"] = 8,
            ["differenceSummary"] = new Dictionary<string, int> { ["check_constraint"] = 8 },
            ["diagnosticCount"] = 0,
            ["local"] = new string('L', 5000),
            ["target"] = new string('T', 5000),
            ["differences"] = differences
        };

        var finalized = SqlMetadataService.FinalizeTableComparisonResult(
            result,
            includeDetails: true,
            fields: null,
            maxTotalTokens: 512);

        Assert.Equal(true, finalized["truncated"]);
        Assert.Equal(true, finalized["detailsIncluded"]);
        Assert.Equal("definition_mismatch", finalized["deploymentState"]);
        Assert.False(finalized.ContainsKey("local"));
        Assert.False(finalized.ContainsKey("target"));
        var returned = Assert.IsAssignableFrom<IEnumerable<object>>(finalized["differences"]);
        var returnedCount = returned.Count();
        Assert.InRange(returnedCount, 1, 7);
        Assert.Equal(8 - returnedCount, finalized["omittedDifferenceCount"]);
    }

    [Fact]
    public void BuildDeploymentErrorItem_MapsMissingLocalFileToInconclusive()
    {
        var item = SqlMetadataService.BuildDeploymentErrorItem(
            "module",
            "dbo",
            "Sample",
            @"C:\missing\Sample.sql",
            new SqlMcpException(ErrorCodes.LocalMissing, "Local compare file was not found."));

        Assert.Equal("local_missing", item["status"]);
        Assert.Equal("inconclusive", item["deploymentState"]);
        Assert.Equal(ErrorCodes.LocalMissing, item["errorCode"]);
    }

    [Theory]
    [InlineData(new[] { "deployed", "equivalent" }, "deployed")]
    [InlineData(new[] { "not_deployed", "not_deployed" }, "not_deployed")]
    [InlineData(new[] { "deployed", "not_deployed" }, "partially_deployed")]
    [InlineData(new[] { "equivalent", "definition_mismatch" }, "definition_mismatch")]
    [InlineData(new[] { "deployed", "inconclusive" }, "inconclusive")]
    [InlineData(new[] { "definition_mismatch", "inconclusive" }, "inconclusive")]
    public void AggregateDeploymentState_ReturnsUnifiedState(string[] states, string expected)
    {
        Assert.Equal(expected, SqlMetadataService.AggregateDeploymentState(states));
    }

    private static TableSchemaModel EmptyTable(TableColumnSpec[] columns) => new(
        "dbo",
        "Sample",
        columns,
        [],
        [],
        [],
        [],
        []);
}
