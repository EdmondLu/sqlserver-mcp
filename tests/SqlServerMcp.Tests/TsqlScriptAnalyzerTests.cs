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
        Assert.DoesNotContain(
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

    [Fact]
    public void Analyze_DoesNotTreatDeleteTargetAliasAsAnObject()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                                  @billId BIGINT
                              AS
                              BEGIN
                                  DELETE A
                                  FROM dbo.puWovenYarnSurplusAllocation A
                                  JOIN #MaterialScope B ON B.material_sku_id=A.material_sku_id
                                  WHERE A.bill_id=@billId;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.ObjectReferences,
            reference => reference.Name.Equals("A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            analysis.ObjectReferences,
            reference => reference.Name.Equals("puWovenYarnSurplusAllocation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "external_temp_table"
                          && diagnostic.Severity == "warning");
        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void Analyze_CallerProvidedTempTableIsAWarningContract()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  SELECT S.ItemId, S.Total
                                  FROM #woven_calc_summary S;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        var external = Assert.Single(analysis.ExternalTempTables);
        Assert.Equal("#woven_calc_summary", external.Name);
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "external_temp_table"
                          && diagnostic.Severity == "warning");
        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "unresolved_object"
                          || diagnostic.Severity == "error");
        Assert.Contains(
            analysis.ColumnReferences,
            reference => reference.Binding?.Kind == "external_temp_table_contract");
    }

    [Fact]
    public void Analyze_BindsProjectedColumnsForCteDerivedTableAndCrossApply()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  WITH Base AS
                                  (
                                      SELECT item_id=A.id, A.name
                                      FROM dbo.Source A
                                  )
                                  SELECT C.item_id, C.name, D.total
                                  FROM Base C
                                  CROSS APPLY
                                  (
                                      SELECT total=SUM(X.qty)
                                      FROM dbo.Detail X
                                      WHERE X.item_id=C.item_id
                                  ) D;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "analysis_inconclusive");
        Assert.Contains(
            analysis.ColumnReferences,
            reference => reference.Identifiers is ["C", "item_id"]
                         && reference.Binding?.Kind == "cte");
        Assert.Contains(
            analysis.ColumnReferences,
            reference => reference.Identifiers is ["D", "total"]
                         && reference.Binding?.Kind == "derived_table");
    }

    [Fact]
    public void Analyze_CollapsesMultipleUnresolvedDerivedAliasesIntoOneWarning()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  SELECT D.unknown_one, D.unknown_two, E.unknown_three
                                  FROM dbo.Source A
                                  CROSS APPLY (SELECT A.*) D
                                  CROSS APPLY (SELECT D.*) E;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Category == "analysis_inconclusive");
        Assert.Contains("3 column references", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 aliases", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_ReportsUnknownColumnFromCompleteDerivedProjection()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              SELECT D.missing
                              FROM (SELECT known=A.id FROM dbo.Source A) D;
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "unresolved_column"
                          && diagnostic.Severity == "error"
                          && diagnostic.Message.Contains("Derived table", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_BindsRepeatedCteNamesToTheNearestDefinition()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  ;WITH best AS
                                  (
                                      SELECT id=A.id FROM dbo.Source A
                                  )
                                  SELECT B.id FROM best B;

                                  ;WITH best AS
                                  (
                                      SELECT code=A.code FROM dbo.Source A
                                  )
                                  SELECT B.code FROM best B;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category is "analysis_inconclusive" or "unresolved_column");
    }

    [Fact]
    public void Analyze_WarnsWhenCatchWritesBeforeDoomedTransactionGuard()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  BEGIN TRY
                                      BEGIN TRAN;
                                      SELECT 1;
                                      COMMIT;
                                  END TRY
                                  BEGIN CATCH
                                      IF OBJECT_ID('tempdb..#table_temp') IS NOT NULL
                                          DROP TABLE #table_temp;
                                      EXEC dbo.WriteErrorLog;
                                      IF XACT_STATE() = -1 THROW;
                                  END CATCH;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Category == "doomed_transaction_write_before_guard");
        Assert.Equal("warning", diagnostic.Severity);
        Assert.Contains("3930", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE", diagnostic.Reference, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXECUTE", diagnostic.Reference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_DoesNotWarnWhenDoomedTransactionGuardPrecedesCatchWrites()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  BEGIN TRY
                                      BEGIN TRAN;
                                      SELECT 1;
                                      COMMIT;
                                  END TRY
                                  BEGIN CATCH
                                      IF XACT_STATE() = -1 THROW;
                                      IF OBJECT_ID('tempdb..#table_temp') IS NOT NULL
                                          DROP TABLE #table_temp;
                                      EXEC dbo.WriteErrorLog;
                                  END CATCH;
                              END
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category == "doomed_transaction_write_before_guard");
    }

    [Fact]
    public void Analyze_WarnsWhenCatchWritesWithoutDoomedTransactionGuard()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  BEGIN TRY
                                      SELECT 1;
                                  END TRY
                                  BEGIN CATCH
                                      UPDATE dbo.LogTable SET status = 0;
                                      EXEC dbo.LogError;
                                      THROW;
                                  END CATCH
                              END;
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);
        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Category == "doomed_transaction_write_without_guard");

        Assert.Contains("UPDATE", diagnostic.Reference);
        Assert.Contains("EXECUTE", diagnostic.Reference);
    }

    [Fact]
    public void Analyze_WarnsForSelectIntoAndCreateTableWithoutGuard()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  BEGIN TRY
                                      SELECT 1;
                                  END TRY
                                  BEGIN CATCH
                                      SELECT error_number = ERROR_NUMBER() INTO #error_log;
                                      CREATE TABLE #fallback_log (error_number int);
                                      THROW;
                                  END CATCH
                              END;
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);
        var diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Category == "doomed_transaction_write_without_guard");

        Assert.Contains("SELECT INTO", diagnostic.Reference);
        Assert.Contains("CREATE TABLE", diagnostic.Reference);
    }

    [Fact]
    public void Analyze_DoesNotWarnForSelectIntoAndCreateTableAfterGuard()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  BEGIN TRY
                                      SELECT 1;
                                  END TRY
                                  BEGIN CATCH
                                      IF XACT_STATE() = -1 THROW;
                                      SELECT error_number = ERROR_NUMBER() INTO #error_log;
                                      CREATE TABLE #fallback_log (error_number int);
                                  END CATCH
                              END;
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Category is "doomed_transaction_write_before_guard" or "doomed_transaction_write_without_guard");
    }

    [Fact]
    public void Analyze_NestedTryCatchProducesOneDiagnosticPerUnsafeCatch()
    {
        const string script = """
                              CREATE OR ALTER PROCEDURE dbo.Sample
                              AS
                              BEGIN
                                  BEGIN TRY
                                      SELECT 1;
                                  END TRY
                                  BEGIN CATCH
                                      UPDATE dbo.OuterLog SET status = 0;
                                      BEGIN TRY
                                          SELECT 2;
                                      END TRY
                                      BEGIN CATCH
                                          INSERT dbo.InnerLog(status) VALUES (0);
                                          THROW;
                                      END CATCH
                                      THROW;
                                  END CATCH
                              END;
                              """;

        var analysis = TsqlScriptAnalyzer.Analyze(script);
        var diagnostics = analysis.Diagnostics
            .Where(item => item.Category == "doomed_transaction_write_without_guard")
            .ToArray();

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, item => item.Reference == "UPDATE");
        Assert.Contains(diagnostics, item => item.Reference == "INSERT");
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
