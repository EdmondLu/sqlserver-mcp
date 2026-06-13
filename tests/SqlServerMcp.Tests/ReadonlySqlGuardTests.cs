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
