using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class TsqlScriptAnalyzerTests
{
    [Fact]
    public void Analyze_ReportsUndeclaredVariableInsertMismatchAndTempColumn()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                                  @id INT
                              AS
                              BEGIN
                                  CREATE TABLE #items (id INT, name NVARCHAR(20));
                                  INSERT INTO #items (id, name)
                                  SELECT @id;
                                  SELECT X.missing, @not_declared
                                  FROM #items X;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.True(analysis.SyntaxValid);
        Assert.True(analysis.HasCreateOrAlter);
        Assert.Contains(analysis.Diagnostics, item => item.Category == "undeclared_variable");
        Assert.Contains(analysis.Diagnostics, item => item.Category == "insert_column_count_mismatch");
        Assert.Contains(analysis.Diagnostics, item => item.Category == "unresolved_column");
    }

    [Fact]
    public void Analyze_ReportsSyntaxAndDynamicSqlSeparately()
    {
        const string dynamicScript = """
                                     CREATE OR ALTER PROCEDURE dbo.Sample
                                     AS
                                     EXEC(N'SELECT * FROM dbo.SomeTable');
                                     """;

        var dynamicAnalysis = TsqlScriptAnalyzer.Analyze(dynamicScript);
        var invalidAnalysis = TsqlScriptAnalyzer.Analyze("CREATE OR ALTER PROCEDURE dbo.Bad AS SELECT FROM;");

        Assert.Contains(dynamicAnalysis.Diagnostics, item => item.Category == "dynamic_sql_unverified");
        Assert.False(invalidAnalysis.SyntaxValid);
        Assert.Contains(invalidAnalysis.Diagnostics, item => item.Category == "syntax_error");
    }

    [Fact]
    public void Analyze_DoesNotTreatSelectStarAsKnownInsertCountMismatch()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  CREATE TABLE #target (id INT, name NVARCHAR(20));
                                  CREATE TABLE #source (id INT, name NVARCHAR(20));
                                  INSERT INTO #target (id, name) SELECT * FROM #source;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "insert_column_count_mismatch");
    }

    [Fact]
    public void Analyze_ResolvesAliasesPerQueryScopeAndDeclaresTableVariables()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  DECLARE @Stage TABLE(stage_id BIGINT, quantity DECIMAL(19,2));

                                  SELECT A.UserName
                                  FROM dbo.smUser A;

                                  SELECT A.material_category
                                  FROM dbo.vwp_puRequestHdr A;

                                  SELECT A.id, E.quantity, F.active_count
                                  FROM dbo.bdWovenYarnWarpingPlanStage A
                                  OUTER APPLY (
                                      SELECT A1.quantity
                                      FROM dbo.mmYarnSuiteLedger A1
                                      WHERE A1.stage_id = A.id
                                  ) E
                                  OUTER APPLY (
                                      SELECT active_count = COUNT(1)
                                      FROM dbo.mmYarnSuiteWovenConsume A1
                                      WHERE A1.stage_id = A.id
                                  ) F;

                                  SELECT A.stage_id
                                  FROM @Stage A;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "undeclared_variable");
        var tableVariable = Assert.Single(analysis.TableVariables);
        Assert.Equal("@Stage", tableVariable.Name);
        Assert.Contains("stage_id", tableVariable.Columns);

        Assert.Equal(
            "smUser",
            FindColumn(analysis, "A", "UserName").Binding?.Name);
        Assert.Equal(
            "vwp_puRequestHdr",
            FindColumn(analysis, "A", "material_category").Binding?.Name);
        Assert.Equal(
            "mmYarnSuiteLedger",
            FindColumn(analysis, "A1", "quantity").Binding?.Name);
        Assert.Equal(
            "table_variable",
            FindColumn(analysis, "A", "stage_id", "table_variable").Binding?.Kind);
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "analysis_inconclusive"
                          && diagnostic.Severity == "warning");
    }

    [Fact]
    public void Analyze_ReportsUnknownTableVariableColumnWithoutMarkingItUndeclared()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  DECLARE @Stage TABLE(stage_id BIGINT);
                                  SELECT A.missing FROM @Stage A;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "undeclared_variable");
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "unresolved_column"
                          && diagnostic.Message.Contains("@Stage", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DoesNotTreatExecuteLabelsOrUpdateTargetAliasesAsObjectsOrVariables()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                                  @value INT
                              AS
                              BEGIN
                                  DECLARE @Stage TABLE(stage_id BIGINT);
                                  EXEC dbo.ApplyChange @named_parameter=@value;

                                  UPDATE A
                                  SET A.stage_id=B.stage_id
                                  FROM dbo.RealStage A
                                  JOIN @Stage B ON B.stage_id=A.stage_id;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "undeclared_variable");
        Assert.DoesNotContain(
            analysis.ObjectReferences,
            reference => reference.Name == "A");
        var stageColumns = analysis.ColumnReferences.Where(reference =>
                reference.Identifiers.Length >= 2
                && reference.Identifiers[^2] == "A"
                && reference.Identifiers[^1] == "stage_id").ToArray();
        var tableVariableColumns = analysis.ColumnReferences.Where(reference =>
                reference.Identifiers.Length >= 2
                && reference.Identifiers[^2] == "B"
                && reference.Identifiers[^1] == "stage_id").ToArray();
        Assert.NotEmpty(stageColumns);
        Assert.NotEmpty(tableVariableColumns);
        Assert.All(
            stageColumns,
            reference => Assert.Equal("RealStage", reference.Binding?.Name));
        Assert.All(
            tableVariableColumns,
            reference => Assert.Equal("table_variable", reference.Binding?.Kind));
    }

    private static TsqlColumnReference FindColumn(
        TsqlScriptAnalysis analysis,
        string qualifier,
        string column,
        string? bindingKind = null)
    {
        return Assert.Single(
            analysis.ColumnReferences,
            reference =>
                reference.Identifiers.Length >= 2
                && reference.Identifiers[^2].Equals(qualifier, StringComparison.OrdinalIgnoreCase)
                && reference.Identifiers[^1].Equals(column, StringComparison.OrdinalIgnoreCase)
                && (bindingKind is null
                    || reference.Binding?.Kind.Equals(bindingKind, StringComparison.OrdinalIgnoreCase) == true));
    }
}
