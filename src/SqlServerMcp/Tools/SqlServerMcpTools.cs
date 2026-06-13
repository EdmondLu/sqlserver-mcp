using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SqlServerMcp.Tools;

[McpServerToolType]
public static class SqlServerMcpTools
{
    [McpServerTool(ReadOnly = true), Description("Test the SQL Server connection and return server, database, login, and database user.")]
    public static Task<string> TestConnection(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.TestConnectionAsync(cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return config, connection, runtime directory, and SQL permission health for this MCP server.")]
    public static Task<string> HealthCheck(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.HealthCheckAsync(cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find SQL Server tables, views, procedures, and functions by object name, schema, columns, or MS_Description.")]
    public static Task<string> FindObjects(
        SqlServerToolService service,
        [Description("Keyword text. Space-separated terms are matched independently.")] string keyword,
        [Description("Object types to include: table, view, procedure, function.")] string[]? objectTypes = null,
        [Description("Maximum objects to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindObjectsAsync(keyword, objectTypes, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Describe a SQL Server table or view, including columns and optional indexes, constraints, and foreign keys.")]
    public static Task<string> DescribeTable(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table or view name.")] string name,
        [Description("Include index metadata.")] bool includeIndexes = true,
        [Description("Include primary key, unique, default, and check constraints.")] bool includeConstraints = true,
        [Description("Include outgoing and incoming foreign keys.")] bool includeForeignKeys = true,
        CancellationToken cancellationToken = default)
    {
        return service.DescribeTableAsync(schema, name, includeIndexes, includeConstraints, includeForeignKeys, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return a compact overview for a table, view, procedure, function, or trigger: object metadata, storage estimate, columns, indexes, constraints, foreign keys, and optional dependencies.")]
    public static Task<string> GetObjectOverview(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Object name.")] string name,
        [Description("Include table/view column metadata.")] bool includeColumns = true,
        [Description("Include table/view index metadata.")] bool includeIndexes = true,
        [Description("Include incoming/outgoing dependency metadata.")] bool includeDependencies = true,
        CancellationToken cancellationToken = default)
    {
        return service.GetObjectOverviewAsync(schema, name, includeColumns, includeIndexes, includeDependencies, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find tables and views that contain a column name.")]
    public static Task<string> FindColumn(
        SqlServerToolService service,
        [Description("Column name or search text.")] string column,
        [Description("When true, match the exact column name; otherwise use LIKE.")] bool exact = true,
        [Description("Maximum rows to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindColumnAsync(column, exact, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return index metadata for a table or view.")]
    public static Task<string> GetIndexes(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table or view name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetIndexesAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return primary key, unique, default, and check constraints for a table or view.")]
    public static Task<string> GetConstraints(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table or view name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetConstraintsAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return outgoing and incoming foreign keys for a table.")]
    public static Task<string> GetForeignKeys(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetForeignKeysAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Search view, procedure, function, and trigger definitions by keyword.")]
    public static Task<string> SearchSqlModules(
        SqlServerToolService service,
        [Description("Keyword text to search in sys.sql_modules.definition.")] string keyword,
        [Description("Object types to include: view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Maximum modules to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.SearchSqlModulesAsync(keyword, objectTypes, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return a view, procedure, function, or trigger definition. Returns a clear VIEW DEFINITION error if SQL Server hides the definition.")]
    public static Task<string> GetModuleDefinition(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetModuleDefinitionAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return incoming and/or outgoing SQL Server dependencies for an object using sys.sql_expression_dependencies plus text-search fallback for incoming module references.")]
    public static Task<string> GetDependencies(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Object name.")] string name,
        [Description("Dependency direction: incoming, outgoing, or both.")] string? direction = "both",
        CancellationToken cancellationToken = default)
    {
        return service.GetDependenciesAsync(schema, name, direction, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find where a table, column, procedure, function, or token is used in columns and SQL module definitions, returning structured matches and snippets.")]
    public static Task<string> FindUsage(
        SqlServerToolService service,
        [Description("Object, column, or token name to search for.")] string name,
        [Description("Optional schema to prioritize two-part module matches.")] string? schema = null,
        [Description("Module object types to include: view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Maximum matches per section.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindUsageAsync(name, schema, objectTypes, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Run one read-only SELECT or WITH CTE query. Complex joins are allowed; writes, DDL, EXEC, WAITFOR, SELECT INTO, USE, and cross-database references are rejected.")]
    public static Task<string> RunReadonlyQuery(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query.")] string sql,
        [Description("Maximum rows to return; capped by server config.")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        return service.RunReadonlyQueryAsync(sql, maxRows, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return SQL Server SHOWPLAN XML for one read-only SELECT or WITH CTE query without executing the target query.")]
    public static Task<string> ExplainQueryPlan(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query to explain.")] string sql,
        CancellationToken cancellationToken = default)
    {
        return service.ExplainQueryPlanAsync(sql, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Clear cached Credential Manager values and SQL connection pools. The next DB request will read the credential again.")]
    public static Task<string> ReloadConnection(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.ReloadConnectionAsync(cancellationToken);
    }
}
