using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class HealthPermissionTests
{
    [Fact]
    public void BuildServerLevelDmvAccess_BlocksOnSqlPermissionWhenPolicyAllows()
    {
        var access = SqlMetadataService.BuildServerLevelDmvAccess(
            "16.0.1000.6",
            hasViewServerPerformanceState: false,
            hasViewServerState: false,
            allowDmvQueries: true,
            allowServerLevelDmv: true);

        Assert.Equal("VIEW SERVER PERFORMANCE STATE", access.SqlServer.RequiredPermission);
        Assert.False(access.SqlServer.HasRequiredPermission);
        Assert.True(access.McpPolicy.Allowed);
        Assert.False(access.EffectiveAccess);
        Assert.Equal(["SQL_SERVER_PERMISSION"], access.BlockedBy);
        Assert.Contains(access.Recommendations, item => item.Contains("error 300", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildServerLevelDmvAccess_BlocksOnMcpPolicyWhenSqlPermissionExists()
    {
        var access = SqlMetadataService.BuildServerLevelDmvAccess(
            "16.0.1000.6",
            hasViewServerPerformanceState: true,
            hasViewServerState: false,
            allowDmvQueries: true,
            allowServerLevelDmv: false);

        Assert.True(access.SqlServer.HasRequiredPermission);
        Assert.False(access.McpPolicy.Allowed);
        Assert.False(access.EffectiveAccess);
        Assert.Equal(["MCP_POLICY"], access.BlockedBy);
    }

    [Fact]
    public void BuildServerLevelDmvAccess_AllowsOnlyWhenSqlPermissionAndPolicyAllow()
    {
        var access = SqlMetadataService.BuildServerLevelDmvAccess(
            "16.0.1000.6",
            hasViewServerPerformanceState: true,
            hasViewServerState: false,
            allowDmvQueries: true,
            allowServerLevelDmv: true);

        Assert.True(access.SqlServer.HasRequiredPermission);
        Assert.True(access.McpPolicy.Allowed);
        Assert.True(access.EffectiveAccess);
        Assert.Empty(access.BlockedBy);
        Assert.Empty(access.Recommendations);
    }

    [Fact]
    public void BuildServerLevelDmvAccess_ReportsBothIndependentBlockers()
    {
        var access = SqlMetadataService.BuildServerLevelDmvAccess(
            "16.0.1000.6",
            hasViewServerPerformanceState: false,
            hasViewServerState: false,
            allowDmvQueries: true,
            allowServerLevelDmv: false);

        Assert.Equal(["SQL_SERVER_PERMISSION", "MCP_POLICY"], access.BlockedBy);
        Assert.Equal(2, access.Recommendations.Length);
    }

    [Fact]
    public void QuoteSqlIdentifier_EscapesDomainHyphenAndClosingBracket()
    {
        var quoted = SqlMetadataService.QuoteSqlIdentifier("DOMAIN\\ops-user]qa");

        Assert.Equal("[DOMAIN\\ops-user]]qa]", quoted);
    }

    [Fact]
    public void BuildHealthPermissionGuidance_UsesDatabaseUserAndServerLoginSafely()
    {
        var serverAccess = SqlMetadataService.BuildServerLevelDmvAccess(
            "16.0.1000.6",
            hasViewServerPerformanceState: false,
            hasViewServerState: false,
            allowDmvQueries: true,
            allowServerLevelDmv: true);

        var guidance = SqlMetadataService.BuildHealthPermissionGuidance(
            "TDSCM-]QA",
            "DOMAIN\\login-user]prod",
            "DOMAIN\\db-user]qa",
            hasViewDefinition: false,
            hasShowplan: false,
            serverAccess);

        Assert.False(guidance.AutomaticallyExecuted);
        Assert.Equal(3, guidance.AdminSql.Length);
        Assert.Contains(
            guidance.AdminSql,
            item => item.Scope == "database"
                    && item.PrincipalType == "database_user"
                    && item.RequiredPermission == "VIEW DEFINITION"
                    && item.Sql == "USE [TDSCM-]]QA]; GRANT VIEW DEFINITION TO [DOMAIN\\db-user]]qa];");
        Assert.Contains(
            guidance.AdminSql,
            item => item.Scope == "database"
                    && item.RequiredPermission == "SHOWPLAN"
                    && item.Sql.EndsWith("GRANT SHOWPLAN TO [DOMAIN\\db-user]]qa];", StringComparison.Ordinal));
        Assert.Contains(
            guidance.AdminSql,
            item => item.Scope == "server"
                    && item.PrincipalType == "login"
                    && item.RequiredPermission == "VIEW SERVER PERFORMANCE STATE"
                    && item.Sql == "GRANT VIEW SERVER PERFORMANCE STATE TO [DOMAIN\\login-user]]prod];");
        Assert.All(guidance.AdminSql, item => Assert.True(item.SuggestionOnly));
    }
}
