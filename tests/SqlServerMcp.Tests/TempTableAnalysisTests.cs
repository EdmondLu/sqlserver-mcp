using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class TempTableAnalysisTests
{
    [Fact]
    public void AnalyzeTempTables_FindsCreateColumnsAndUsage()
    {
        const string definition = """
                                  CREATE PROC dbo.Sample
                                  AS
                                  BEGIN
                                      CREATE TABLE #Plan
                                      (
                                          Id int NOT NULL,
                                          Code nvarchar(50) NULL
                                      );

                                      INSERT INTO #Plan (Id, Code)
                                      SELECT Id, Code FROM dbo.PlanSource;

                                      SELECT p.Id
                                      FROM #Plan p
                                      JOIN #Other o ON o.Id = p.Id;
                                  END
                                  """;

        var analysis = SqlMetadataService.AnalyzeTempTables(definition);

        Assert.Equal(2, analysis.TempTables.Length);
        var plan = analysis.TempTables.Single(table => table.Name == "#Plan");
        Assert.Equal(4, plan.FirstLine);
        Assert.Contains(plan.Creations, creation => creation.Operation == "create_table" && creation.LineNumber == 4);
        Assert.Contains(plan.Columns, column => column.Name == "Id" && column.Definition.Contains("int"));
        Assert.Contains(plan.Columns, column => column.Name == "Code" && column.Definition.Contains("nvarchar"));
        Assert.Contains(plan.References, reference => reference.Operation == "insert" && reference.LineNumber == 10);
        Assert.Contains(plan.References, reference => reference.Operation == "read" && reference.LineNumber == 14);

        var other = analysis.TempTables.Single(table => table.Name == "#Other");
        Assert.Contains(other.References, reference => reference.Operation == "join");
    }

    [Fact]
    public void AnalyzeTempTables_FindsSelectInto()
    {
        const string definition = """
                                  SELECT *
                                  INTO #Result
                                  FROM dbo.Source;
                                  UPDATE #Result SET Name = N'x';
                                  """;

        var analysis = SqlMetadataService.AnalyzeTempTables(definition);

        var result = Assert.Single(analysis.TempTables);
        Assert.Equal("#Result", result.Name);
        Assert.Contains(result.Creations, creation => creation.Operation == "select_into");
        Assert.Contains(result.References, reference => reference.Operation == "update");
    }

    [Fact]
    public void AnalyzeTempTables_TracksColumnsAcrossMultilineStatements()
    {
        const string definition = """
                                  CREATE PROC dbo.Sample
                                  AS
                                  BEGIN
                                      CREATE TABLE #Plan
                                      (
                                          Id int NOT NULL,
                                          Code nvarchar(50) NULL
                                      );

                                      INSERT INTO
                                          #Plan
                                          (
                                              Id,
                                              Code
                                          )
                                      SELECT S.Id, S.Code
                                      FROM dbo.Source S;

                                      SELECT
                                          S.Id AS Id,
                                          S.Code AS Code,
                                          S.Name AS Name
                                      INTO #Selected
                                      FROM dbo.Source S;

                                      UPDATE p
                                      SET
                                          p.Code = s.Code
                                      FROM #Plan p
                                      JOIN #Selected s ON s.Id = p.Id;
                                  END
                                  """;

        var analysis = SqlMetadataService.AnalyzeTempTables(definition);

        var plan = analysis.TempTables.Single(table => table.Name == "#Plan");
        Assert.Contains(plan.References, reference =>
            reference.Operation == "insert"
            && reference.Columns.Contains("Id")
            && reference.Columns.Contains("Code"));
        Assert.Contains(plan.References, reference =>
            reference.Operation == "update"
            && reference.Columns.Contains("Code"));
        Assert.Contains(plan.ColumnFlow, flow =>
            flow.Column == "Code"
            && flow.OperationCounts.ContainsKey("insert")
            && flow.OperationCounts.ContainsKey("update"));

        var selected = analysis.TempTables.Single(table => table.Name == "#Selected");
        Assert.Contains(selected.Creations, creation =>
            creation.Operation == "select_into"
            && creation.Columns.Contains("Id")
            && creation.Columns.Contains("Code")
            && creation.Columns.Contains("Name"));
        Assert.Contains(selected.ColumnFlow, flow =>
            flow.Column == "Name"
            && flow.OperationCounts.ContainsKey("select_into"));
    }
}
