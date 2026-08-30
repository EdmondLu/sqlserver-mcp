using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class ReadonlySqlGuardTests
{
    [Theory]
    [InlineData("SELECT TOP 10 A.id FROM dbo.TableA A LEFT JOIN dbo.TableB B ON B.id=A.id ORDER BY A.id DESC;")]
    [InlineData("WITH x AS (SELECT TOP 100 A.id FROM dbo.TableA A) SELECT A.id FROM x A;")]
    [InlineData("SELECT N'UPDATE dbo.TableA SET name = N''x''' AS sample_text;")]
    [InlineData("SELECT * FROM sys.dm_db_partition_stats;")]
    [InlineData("SELECT * FROM sys.dm_exec_describe_first_result_set(N'SELECT 1 AS id', NULL, 0);")]
    public void ValidateReadonlyQuery_AllowsExpectedSelects(string sql)
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        guard.ValidateReadonlyQuery(sql);
    }

    [Theory]
    [InlineData("UPDATE dbo.TableA SET name = N'x';")]
    [InlineData("DELETE FROM dbo.TableA;")]
    [InlineData("EXEC dbo.SomeProcedure;")]
    [InlineData("WAITFOR DELAY '00:00:10';")]
    [InlineData("SELECT * INTO dbo.NewTable FROM dbo.TableA;")]
    [InlineData("USE OtherDb; SELECT * FROM dbo.TableA;")]
    [InlineData("SELECT * FROM OtherDb.dbo.TableA;")]
    [InlineData("SELECT * FROM sys.dm_exec_requests;")]
    [InlineData("SELECT * FROM OPENQUERY([RemoteServer], 'SELECT 1');")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\secret.txt', SINGLE_CLOB) AS contents;")]
    [InlineData("SELECT * FROM OPENDATASOURCE('MSOLEDBSQL', 'Server=remote;Trusted_Connection=yes;').SampleDb.dbo.TableA;")]
    [InlineData("SELECT * FROM ##items;")]
    public void ValidateReadonlyQuery_RejectsBlockedSql(string sql)
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        var ex = Assert.Throws<SqlMcpException>(() => guard.ValidateReadonlyQuery(sql));

        Assert.Contains(ex.ErrorCode, new[] { ErrorCodes.SqlGuardRejected, ErrorCodes.SqlParseFailed });
    }

    [Fact]
    public void ValidateShowplanQuery_UsesSameReadonlyRules()
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        guard.ValidateShowplanQuery("SELECT TOP 1 A.id FROM dbo.TableA A;");
        var ex = Assert.Throws<SqlMcpException>(() => guard.ValidateShowplanQuery("EXEC dbo.SomeProcedure;"));

        Assert.Equal(ErrorCodes.SqlGuardRejected, ex.ErrorCode);
    }

    [Fact]
    public void ValidateReadonlyQuery_RejectsSequenceAdvancement()
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        var ex = Assert.Throws<SqlMcpException>(() => guard.ValidateReadonlyQuery(
            "SELECT NEXT VALUE FOR dbo.PersistentSequence AS id;"));

        Assert.Equal(ErrorCodes.SqlGuardRejected, ex.ErrorCode);
        Assert.Contains("persistent side effects", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReadonlyBatch_AllowsOnlyTempWritesAndFinalSelect()
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        guard.ValidateReadonlyBatch(
            """
            DECLARE @minimum INT = 1;
            CREATE TABLE #items (id INT NOT NULL);
            INSERT INTO #items (id)
            SELECT A.id FROM dbo.TableA A WHERE A.id >= @minimum;
            SELECT id FROM #items ORDER BY id;
            """);
    }

    [Fact]
    public void ValidateReadonlyBatch_AllowsLocalVariablesTempMutationsAndMultipleResults()
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        guard.ValidateReadonlyBatch(
            """
            DECLARE @minimum INT = 1;
            SET @minimum = @minimum + 1;
            CREATE TABLE #items (id INT NOT NULL, label NVARCHAR(50) NULL);
            INSERT INTO #items (id, label) VALUES (1, N'UPDATE dbo.Real SET x=1'), (2, N'keep');
            UPDATE i SET label = N'changed' FROM #items AS i WHERE id = @minimum;
            SELECT id, label FROM #items ORDER BY id;
            DELETE i FROM #items AS i WHERE id < @minimum;
            WITH remaining AS (SELECT id FROM #items)
            SELECT id FROM remaining ORDER BY id;
            """);
    }

    [Fact]
    public void ValidateReadonlyBatch_AllowsCommentsAndKeywordsInsideStrings()
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        guard.ValidateReadonlyBatch(
            """
            -- EXEC dbo.Danger; UPDATE dbo.Real SET value = 1;
            DECLARE @text nvarchar(200) = N'DELETE FROM dbo.Real; BEGIN TRAN;';
            CREATE TABLE #messages(value nvarchar(200));
            INSERT INTO #messages(value) VALUES (@text);
            SELECT value FROM #messages; /* DROP TABLE dbo.Real; */
            """);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.TableA(id) SELECT 1; SELECT 1;")]
    [InlineData("EXEC dbo.SomeProcedure; SELECT 1;")]
    [InlineData("CREATE TABLE dbo.RealTable(id INT); SELECT 1;")]
    [InlineData("UPDATE dbo.TableA SET id=2; SELECT 1;")]
    [InlineData("DELETE FROM dbo.TableA; SELECT 1;")]
    [InlineData("BEGIN TRANSACTION; SELECT 1; ROLLBACK TRANSACTION;")]
    [InlineData("SET NOCOUNT ON; SELECT 1;")]
    [InlineData("DROP TABLE #items; SELECT 1;")]
    [InlineData("DECLARE @sql nvarchar(max)=N'SELECT 1'; EXEC(@sql); SELECT 1;")]
    [InlineData("CREATE TABLE #items(id int); INSERT INTO #items EXEC dbo.SomeProcedure; SELECT 1;")]
    [InlineData("CREATE TABLE #items(id int); UPDATE t SET id=2 FROM dbo.TableA AS a CROSS APPLY (SELECT id FROM #items AS t) AS nested; SELECT 1;")]
    [InlineData("CREATE TABLE #items(id int); INSERT INTO #items VALUES(1); UPDATE #items SET id=2 OUTPUT inserted.id INTO dbo.Audit; SELECT 1;")]
    [InlineData("SELECT NEXT VALUE FOR dbo.PersistentSequence AS id;")]
    [InlineData("WITH changed AS (SELECT id FROM dbo.TableA) UPDATE dbo.TableA SET id=2; SELECT 1;")]
    [InlineData("SELECT 1; GO SELECT 2;")]
    public void ValidateReadonlyBatch_RejectsBusinessWritesAndUnsupportedStatements(string sql)
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        var ex = Assert.Throws<SqlMcpException>(() => guard.ValidateReadonlyBatch(sql));

        Assert.Contains(ex.ErrorCode, new[] { ErrorCodes.SqlGuardRejected, ErrorCodes.SqlParseFailed });
    }

    [Theory]
    [InlineData("CREATE TABLE ##items(id int); SELECT id FROM ##items;")]
    [InlineData("CREATE TABLE [##items](id int); SELECT id FROM [##items];")]
    [InlineData("SELECT id FROM ##items;")]
    [InlineData("SELECT 1 AS id INTO ##items; SELECT id FROM ##items;")]
    [InlineData("INSERT INTO ##items(id) VALUES(1); SELECT id FROM ##items;")]
    [InlineData("UPDATE ##items SET id=2; SELECT id FROM ##items;")]
    [InlineData("UPDATE g SET id=2 FROM ##items AS g; SELECT id FROM ##items;")]
    [InlineData("DELETE FROM ##items; SELECT 1;")]
    [InlineData("DELETE g FROM ##items AS g; SELECT 1;")]
    [InlineData("CREATE TABLE #items(id int); UPDATE #items SET id=2 OUTPUT inserted.id INTO ##audit; SELECT id FROM #items;")]
    public void ValidateReadonlyBatch_RejectsGlobalTempObjectsBeforeConnecting(string sql)
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        var ex = Assert.Throws<SqlMcpException>(() => guard.ValidateReadonlyBatch(sql));

        Assert.Equal(ErrorCodes.SqlGuardRejected, ex.ErrorCode);
    }

    [Fact]
    public void ValidateReadonlyBatch_RejectsBatchWithoutResultSet()
    {
        var guard = new ReadonlySqlGuard(CreateOptions());

        var ex = Assert.Throws<SqlMcpException>(() => guard.ValidateReadonlyBatch(
            "CREATE TABLE #items(id int); INSERT INTO #items VALUES(1);"));

        Assert.Equal(ErrorCodes.SqlGuardRejected, ex.ErrorCode);
        Assert.NotNull(ex.ErrorDetails);
        Assert.Contains("At least one SELECT", ex.Detail, StringComparison.Ordinal);
    }

    private static SqlServerMcpOptions CreateOptions()
    {
        return new SqlServerMcpOptions
        {
            Server = "localhost,1433",
            Database = "SampleDb",
            CredentialTarget = "sqlserver-mcp/SampleDb",
            Security = new SecurityOptions
            {
                AllowDmvQueries = true,
                AllowServerLevelDmv = false,
                AllowCrossDatabase = false,
                AllowSystemDatabases = false
            }
        };
    }
}
