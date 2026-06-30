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

    [McpServerTool(ReadOnly = true), Description("Return serverVersion, config, connection, runtime directory, and SQL permission health for this MCP server.")]
    public static Task<string> HealthCheck(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.HealthCheckAsync(cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("First-step discovery for tables, views, procedures, and functions by object name, schema, columns, or MS_Description. Use before overview/definition tools when the exact object is unknown.")]
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

    [McpServerTool(ReadOnly = true), Description("Object card for a known table, view, procedure, function, or trigger: metadata, dates, storage estimate, columns, indexes, constraints, foreign keys, triggers, and optional dependencies.")]
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

    [McpServerTool(ReadOnly = true), Description("Search SQL module definitions by keyword and return snippets. Use get_module_definition with keyword/startLine for deeper inspection, or compare_module_to_file/compare_module_to_repo to confirm whether a local SQL file matches the database object.")]
    public static Task<string> SearchSqlModules(
        SqlServerToolService service,
        [Description("Keyword text to search in sys.sql_modules.definition.")] string keyword,
        [Description("Object types to include: view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Maximum modules to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.SearchSqlModulesAsync(keyword, objectTypes, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return a view, procedure, function, or trigger definition. Optionally return a line range or keyword-centered slices with line numbers. Response includes compare hints for local SQL file vs database object checks.")]
    public static Task<string> GetModuleDefinition(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        [Description("Optional keyword. When no line range is supplied, only matching lines plus context are returned.")] string? keyword = null,
        [Description("Optional 1-based start line. Use with endLine to return a slice.")] int? startLine = null,
        [Description("Optional 1-based end line. Defaults to the final line when startLine is supplied.")] int? endLine = null,
        [Description("Context lines around keyword matches. Defaults to 3 and is capped.")] int? contextLines = null,
        [Description("Include a structured lines array with lineNumber and text. Returned automatically for partial selections.")] bool includeLineNumbers = false,
        CancellationToken cancellationToken = default)
    {
        return service.GetModuleDefinitionAsync(
            schema,
            name,
            keyword,
            startLine,
            endLine,
            contextLines,
            includeLineNumbers,
            cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Compare a SQL Server module definition with a known local .sql file path. Use to confirm whether a repository SQL file has been executed to the database, whether the database procedure/function/view/trigger matches the local file, and what local-vs-target diff remains.")]
    public static Task<string> CompareModuleToFile(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        [Description("Absolute local file path to compare with the database module definition.")] string filePath,
        [Description("Context lines around the changed block. Defaults to 5 and is capped.")] int? contextLines = null,
        [Description("Diff output mode: summary, compact, or full. Defaults to compact.")] string? diffMode = null,
        [Description("Maximum diff hunks to include. Defaults depend on diffMode and are capped.")] int? maxHunks = null,
        [Description("Maximum returned diff lines per side per hunk. Defaults depend on diffMode and are capped.")] int? maxDiffLinesPerSide = null,
        CancellationToken cancellationToken = default)
    {
        return service.CompareModuleToFileAsync(
            schema,
            name,
            filePath,
            contextLines,
            diffMode,
            maxHunks,
            maxDiffLinesPerSide,
            cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Auto-discover matching .sql files under a local repository/folder, then compare the best unambiguous candidate with a SQL Server module definition. Use when checking whether a repo SQL script has already been deployed to the target database.")]
    public static Task<string> CompareModuleToRepo(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        [Description("Optional repository/folder root. Defaults to the MCP process current directory.")] string? root = null,
        [Description("Optional path glob patterns such as **/*.sql, procedures/*.sql, or *proc*.sql. Defaults to **/*.sql.")] string[]? patterns = null,
        [Description("Optional path glob patterns to exclude from candidate discovery, such as backup/** or domain2/**. Merged with compare.repoExcludePatterns from config.")] string[]? excludePatterns = null,
        [Description("Maximum ranked candidates to return when discovery is empty or ambiguous. Defaults to 10 and is capped.")] int? maxCandidates = null,
        [Description("Context lines around the changed block after a file is selected. Defaults to 5 and is capped.")] int? contextLines = null,
        [Description("Diff output mode: summary, compact, or full. Defaults to compact.")] string? diffMode = null,
        [Description("Maximum diff hunks to include. Defaults depend on diffMode and are capped.")] int? maxHunks = null,
        [Description("Maximum returned diff lines per side per hunk. Defaults depend on diffMode and are capped.")] int? maxDiffLinesPerSide = null,
        CancellationToken cancellationToken = default)
    {
        return service.CompareModuleToRepoAsync(
            schema,
            name,
            root,
            patterns,
            excludePatterns,
            maxCandidates,
            contextLines,
            diffMode,
            maxHunks,
            maxDiffLinesPerSide,
            cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Analyze local temp table usage inside a stored procedure, function, trigger, or view definition, including CREATE TABLE, SELECT INTO, writes, reads, joins, and line numbers.")]
    public static Task<string> AnalyzeModuleTempTables(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.AnalyzeModuleTempTablesAsync(schema, name, cancellationToken);
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

    [McpServerTool(ReadOnly = true), Description("Usage discovery for a table, column, procedure, function, or token across column names and SQL module definitions, returning structured matches and snippets.")]
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

    [McpServerTool(ReadOnly = true), Description("Search configured application/configuration text plus key/name/label metadata by keyword, prioritizing usable/enabled rows when available. Targets are allow-listed in sqlserver_mcp.json textSearch.targets.")]
    public static Task<string> SearchConfigText(
        SqlServerToolService service,
        [Description("Keyword text. Space-separated terms are matched independently.")] string keyword,
        [Description("Optional configured text search profile name. When omitted, all enabled profiles are searched.")] string? profile = null,
        [Description("Maximum matches to return; capped by server config.")] int? limit = null,
        [Description("Include full configured search target metadata in the response. Defaults to false to keep results compact.")] bool includeTargets = false,
        [Description("When true, only return rows whose usable/enabled/active-style column is true. Targets without such a column ignore this filter.")] bool usableOnly = false,
        CancellationToken cancellationToken = default)
    {
        return service.SearchConfigTextAsync(keyword, profile, limit, includeTargets, usableOnly, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Run one guarded read-only SELECT or WITH CTE query with optional named parameters. Use describe_query_result first when only result shape is needed.")]
    public static Task<string> RunReadonlyQuery(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query.")] string sql,
        [Description("Optional named SQL parameters, for example { \"id\": 123, \"name\": \"abc\" }. Use @id and @name in SQL.")] Dictionary<string, object?>? parameters = null,
        [Description("Maximum rows to return; capped by server config.")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        return service.RunReadonlyQueryAsync(sql, parameters, maxRows, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Describe the result columns for one guarded read-only SELECT or WITH CTE query without executing it, optionally applying explicit UI SQL template replacements first.")]
    public static Task<string> DescribeQueryResult(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query to describe.")] string sql,
        [Description("Optional named SQL parameters used to infer parameter definitions.")] Dictionary<string, object?>? parameters = null,
        [Description("Optional UI SQL template replacements such as { \"0\": \"1=1\" } for placeholders like {0}. Values are raw SQL fragments and the final SQL is still validated as read-only.")] Dictionary<string, object?>? templateValues = null,
        CancellationToken cancellationToken = default)
    {
        return service.DescribeQueryResultAsync(sql, parameters, templateValues, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return SQL Server SHOWPLAN XML for one guarded read-only SELECT or WITH CTE query without executing the target query.")]
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
