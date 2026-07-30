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
}
