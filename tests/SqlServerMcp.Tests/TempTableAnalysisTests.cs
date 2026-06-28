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
}
