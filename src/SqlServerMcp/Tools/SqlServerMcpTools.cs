using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SqlServerMcp.Tools;

[McpServerToolType]
public static class SqlServerMcpTools
{
    [McpServerTool(ReadOnly = true), Description("Test the SQL Server connection and return server, database, login, and database user.")]
    public static Task<CallToolResult> TestConnection(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.TestConnectionAsync(cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return serverVersion, config, connection, runtime directory, and SQL permission health for this MCP server.")]
    public static Task<CallToolResult> HealthCheck(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.HealthCheckAsync(cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("First-step discovery for tables, views, procedures, and functions by object name, schema, columns, or MS_Description. Use before overview/definition tools when the exact object is unknown.")]
    public static Task<CallToolResult> FindObjects(
        SqlServerToolService service,
        [Description("Keyword text. Space-separated terms are matched independently.")] string keyword,
        [Description("Object types to include: table, view, procedure, function.")] string[]? objectTypes = null,
        [Description("Maximum objects to return.")] int? limit = null,
        [Description("Opaque pagination cursor returned by a previous find_objects call.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindObjectsAsync(keyword, objectTypes, limit, cursor, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Resolve an object name, report whether it exists, and rank similar tables, views, procedures, and functions including vwp_/vwt_/vwpr_/vwtr_ physical-name mappings.")]
    public static Task<CallToolResult> ResolveObject(
        SqlServerToolService service,
        [Description("Object name, optionally schema-qualified such as dbo.puRequest.")] string name,
        [Description("Optional schema when name is not schema-qualified.")] string? schema = null,
        [Description("Object types to include: table, view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Maximum candidates to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.ResolveObjectAsync(name, schema, objectTypes, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Describe a SQL Server table or view with compact shape, write_contract, keys, performance, or full presets and explicit include overrides.")]
    public static Task<CallToolResult> DescribeTable(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table or view name.")] string name,
        [Description("Preset: shape (default), write_contract, keys, performance, or full.")] string? mode = null,
        [Description("Optional column names to return.")] string[]? columns = null,
        [Description("Override whether index metadata is included.")] bool? includeIndexes = null,
        [Description("Override whether primary key, unique, default, and check constraints are included.")] bool? includeConstraints = null,
        [Description("Override whether outgoing and incoming foreign keys are included.")] bool? includeForeignKeys = null,
        [Description("Override whether per-column default metadata is included.")] bool? includeDefaults = null,
        [Description("Override whether object and column descriptions are included.")] bool? includeDescriptions = null,
        CancellationToken cancellationToken = default)
    {
        return service.DescribeTableAsync(
            schema,
            name,
            mode,
            columns,
            includeIndexes,
            includeConstraints,
            includeForeignKeys,
            includeDefaults,
            includeDescriptions,
            cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Object card for a known table, view, procedure, function, or trigger: metadata, dates, storage estimate, columns, indexes, constraints, foreign keys, triggers, and optional dependencies.")]
    public static Task<CallToolResult> GetObjectOverview(
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
    public static Task<CallToolResult> FindColumn(
        SqlServerToolService service,
        [Description("Column name or search text.")] string column,
        [Description("When true, match the exact column name; otherwise use LIKE.")] bool exact = true,
        [Description("Maximum rows to return.")] int? limit = null,
        [Description("Opaque pagination cursor returned by a previous find_column call.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindColumnAsync(column, exact, limit, cursor, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Profile one column with NULL/empty counts, length statistics and distribution, values at the declared limit, optional format checks, and bounded samples.")]
    public static Task<CallToolResult> ProfileColumn(
        SqlServerToolService service,
        [Description("Schema name.")] string schema,
        [Description("Table or view name.")] string name,
        [Description("Column name.")] string column,
        [Description("Optional format check: json, numeric, or csv.")] string? expectedFormat = null,
        [Description("Maximum sample values to return, from 0 to 20.")] int sampleLimit = 5,
        CancellationToken cancellationToken = default)
    {
        return service.ProfileColumnAsync(schema, name, column, expectedFormat, sampleLimit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return index metadata for a table or view.")]
    public static Task<CallToolResult> GetIndexes(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table or view name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetIndexesAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return primary key, unique, default, and check constraints for a table or view.")]
    public static Task<CallToolResult> GetConstraints(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table or view name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetConstraintsAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return outgoing and incoming foreign keys for a table.")]
    public static Task<CallToolResult> GetForeignKeys(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Table name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.GetForeignKeysAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Search SQL module definitions by keyword and return snippets. Use get_module_definition with keyword/startLine for deeper inspection, or compare_module_to_file/compare_module_to_repo to confirm whether a local SQL file matches the database object.")]
    public static Task<CallToolResult> SearchSqlModules(
        SqlServerToolService service,
        [Description("Keyword text to search in sys.sql_modules.definition.")] string keyword,
        [Description("Object types to include: view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Maximum modules to return.")] int? limit = null,
        [Description("Opaque pagination cursor returned by a previous search_sql_modules call.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return service.SearchSqlModulesAsync(keyword, objectTypes, limit, cursor, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return a view, procedure, function, or trigger definition. Optionally return a line range or keyword-centered slices with line numbers. Response includes compare hints for local SQL file vs database object checks.")]
    public static Task<CallToolResult> GetModuleDefinition(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        [Description("Optional keyword. When no line range is supplied, only matching lines plus context are returned.")] string? keyword = null,
        [Description("Optional additional keywords. A line matching any keyword is selected.")] string[]? keywords = null,
        [Description("Optional 1-based start line. Use with endLine to return a slice.")] int? startLine = null,
        [Description("Optional 1-based end line. Defaults to the final line when startLine is supplied.")] int? endLine = null,
        [Description("Context lines around keyword matches. Defaults to 3 and is capped.")] int? contextLines = null,
        [Description("Optional context lines before each keyword match; overrides contextLines for the leading side.")] int? beforeLines = null,
        [Description("Optional context lines after each keyword match; overrides contextLines for the trailing side.")] int? afterLines = null,
        [Description("Maximum keyword matches to select before applying the line cap.")] int? maxMatches = null,
        [Description("Optional 1-based keyword-match occurrence to select.")] int? occurrence = null,
        [Description("Merge overlapping keyword windows. Defaults to true.")] bool collapseOverlaps = true,
        [Description("Include a structured lines array with lineNumber and text. Returned automatically for partial selections.")] bool includeLineNumbers = false,
        CancellationToken cancellationToken = default)
    {
        return service.GetModuleDefinitionAsync(
            schema,
            name,
            keyword,
            keywords,
            startLine,
            endLine,
            contextLines,
            beforeLines,
            afterLines,
            maxMatches,
            occurrence,
            collapseOverlaps,
            includeLineNumbers,
            cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Parse and validate a T-SQL module script without executing it or writing to the database. Reports syntax, variables, INSERT shape, objects, columns, user-defined types, temp tables, wrapper status, and dynamic-SQL warnings.")]
    public static Task<CallToolResult> ValidateTsqlScript(
        SqlServerToolService service,
        [Description("T-SQL script text to validate. The script is never executed.")] string script,
        [Description("Optional source label used in the response.")] string? sourceName = null,
        CancellationToken cancellationToken = default)
    {
        return service.ValidateTsqlScriptAsync(script, sourceName, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Read and statically validate a local .sql file without executing it or writing to the database.")]
    public static Task<CallToolResult> ValidateTsqlFile(
        SqlServerToolService service,
        [Description("Absolute local .sql file path.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        return service.ValidateTsqlFileAsync(filePath, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Compare a SQL Server module definition with a known local .sql file path. Use to confirm whether a repository SQL file has been executed to the database, whether the database procedure/function/view/trigger matches the local file, and what local-vs-target diff remains.")]
    public static Task<CallToolResult> CompareModuleToFile(
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
    public static Task<CallToolResult> CompareModuleToRepo(
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

    [McpServerTool(ReadOnly = true), Description("Validate and compare an ordered deployment set of local SQL module files, returning exact/wrapper/format/comment/body changes plus target_missing and local_missing states independently per item.")]
    public static Task<CallToolResult> CompareModulesToFiles(
        SqlServerToolService service,
        [Description("Modules in deployment order, each with schema, name, and absolute filePath.")] DeploymentModuleInput[] modules,
        [Description("Diff output mode: summary, compact, or full. Defaults to summary for batch use.")] string? diffMode = null,
        CancellationToken cancellationToken = default)
    {
        return service.CompareModulesToFilesAsync(modules, diffMode, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Analyze local temp table usage inside a stored procedure, function, trigger, or view definition, including CREATE TABLE, SELECT INTO, writes, reads, joins, and line numbers.")]
    public static Task<CallToolResult> AnalyzeModuleTempTables(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Module name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return service.AnalyzeModuleTempTablesAsync(schema, name, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return incoming and/or outgoing SQL Server dependencies for an object using sys.sql_expression_dependencies plus text-search fallback for incoming module references.")]
    public static Task<CallToolResult> GetDependencies(
        SqlServerToolService service,
        [Description("Schema name, usually dbo.")] string schema,
        [Description("Object name.")] string name,
        [Description("Dependency direction: incoming, outgoing, or both.")] string? direction = "both",
        CancellationToken cancellationToken = default)
    {
        return service.GetDependenciesAsync(schema, name, direction, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find modules that call or reference a target object, separating confirmed sys.sql_expression_dependencies edges from static module matches and reporting caller transaction signals.")]
    public static Task<CallToolResult> GetCallers(
        SqlServerToolService service,
        [Description("Target schema name.")] string schema,
        [Description("Target object name.")] string name,
        [Description("Caller module types to include: view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Maximum caller matches to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.GetCallersAsync(schema, name, objectTypes, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find objects called or referenced by a SQL module, separating confirmed dependencies, static procedure calls, and dynamic-SQL warnings.")]
    public static Task<CallToolResult> GetCallees(
        SqlServerToolService service,
        [Description("Caller schema name.")] string schema,
        [Description("Caller module name.")] string name,
        [Description("Maximum callee matches to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.GetCalleesAsync(schema, name, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Build a bounded SQL dependency graph from confirmed sys.sql_expression_dependencies edges.")]
    public static Task<CallToolResult> GetDependencyGraph(
        SqlServerToolService service,
        [Description("Root object schema.")] string schema,
        [Description("Root object name.")] string name,
        [Description("Traversal direction: callers, callees, or both.")] string direction = "both",
        [Description("Maximum graph depth, from 1 to 8.")] int maxDepth = 3,
        [Description("Maximum graph edges to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.GetDependencyGraphAsync(schema, name, direction, maxDepth, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Literal usage discovery for a table, column, procedure, function, or token. Returns confirmed dependencies and ranked identifier, dynamic-SQL, text, comment, or regex matches with source locations, context, confidence, and stable pagination.")]
    public static Task<CallToolResult> FindUsage(
        SqlServerToolService service,
        [Description("Object, column, or token name to search for.")] string name,
        [Description("Optional schema to prioritize two-part module matches.")] string? schema = null,
        [Description("Module object types to include: view, procedure, function, trigger.")] string[]? objectTypes = null,
        [Description("Match mode: exact_identifier, exact_text, contains, or regex. Omit for ranked matching that prioritizes confirmed dependencies and identifier matches.")] string? matchMode = null,
        [Description("Maximum matches to return on this page.")] int? limit = null,
        [Description("Opaque pagination cursor returned by a previous find_usage call.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindUsageAsync(name, schema, objectTypes, matchMode, limit, cursor, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Search configured application/configuration text plus key/name/label metadata by keyword, prioritizing usable/enabled rows when available. Targets are allow-listed in sqlserver_mcp.json textSearch.targets.")]
    public static Task<CallToolResult> SearchConfigText(
        SqlServerToolService service,
        [Description("Keyword text. Space-separated terms are matched independently.")] string keyword,
        [Description("Optional configured text search profile name. When omitted, all enabled profiles are searched.")] string? profile = null,
        [Description("Maximum matches to return; capped by server config.")] int? limit = null,
        [Description("Include full configured search target metadata in the response. Defaults to false to keep results compact.")] bool includeTargets = false,
        [Description("When true, only return rows whose usable/enabled/active-style column is true. Targets without such a column ignore this filter.")] bool usableOnly = false,
        [Description("Opaque pagination cursor returned by a previous search_config_text call.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return service.SearchConfigTextAsync(keyword, profile, limit, includeTargets, usableOnly, cursor, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Locate database and configured application consumers of a field, combining exact column discovery, module usage, and allow-listed page/low-code text search.")]
    public static Task<CallToolResult> FindFieldConsumers(
        SqlServerToolService service,
        [Description("Field or column name.")] string column,
        [Description("Optional schema for module matching.")] string? schema = null,
        [Description("Optional configured text-search profile.")] string? profile = null,
        [Description("Maximum results per source.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindFieldConsumersAsync(column, schema, profile, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find configured pages or low-code entries that reference a table or view through allow-listed text-search targets.")]
    public static Task<CallToolResult> FindPageByTable(
        SqlServerToolService service,
        [Description("Table or view name, optionally schema-qualified.")] string name,
        [Description("Optional configured text-search profile.")] string? profile = null,
        [Description("Maximum configuration matches.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindPageByTableAsync(name, profile, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Find configured pages or low-code entries that reference a save procedure through allow-listed text-search targets.")]
    public static Task<CallToolResult> FindPageBySaveProcedure(
        SqlServerToolService service,
        [Description("Save procedure name, optionally schema-qualified.")] string name,
        [Description("Optional configured text-search profile.")] string? profile = null,
        [Description("Maximum configuration matches.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return service.FindPageBySaveProcedureAsync(name, profile, limit, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Run one guarded read-only SELECT or WITH CTE query with optional named parameters. Use describe_query_result first when only result shape is needed.")]
    public static Task<CallToolResult> RunReadonlyQuery(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query.")] string sql,
        [Description("Optional named SQL parameters, for example { \"id\": 123, \"name\": \"abc\" }. Use @id and @name in SQL.")] Dictionary<string, object?>? parameters = null,
        [Description("Maximum rows to return; capped by server config.")] int? maxRows = null,
        [Description("Opaque pagination cursor returned by a previous run_readonly_query call. The query should have deterministic ORDER BY for stable pages.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return service.RunReadonlyQueryAsync(sql, parameters, maxRows, cursor, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Run a guarded multi-statement diagnostic batch that allows only DECLARE, CREATE TABLE #temp, INSERT into #temp, and a final SELECT. The batch runs in a transaction that is always rolled back.")]
    public static Task<CallToolResult> RunReadonlyBatch(
        SqlServerToolService service,
        [Description("Guarded diagnostic batch ending in SELECT. Business-table writes, EXEC, dynamic SQL, DDL beyond #temp creation, and cross-database violations are rejected.")] string sql,
        [Description("Optional named SQL parameters.")] Dictionary<string, object?>? parameters = null,
        [Description("Maximum rows from the final SELECT.")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        return service.RunReadonlyBatchAsync(sql, parameters, maxRows, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Describe the result columns for one guarded read-only SELECT or WITH CTE query without executing it, optionally applying explicit UI SQL template replacements first.")]
    public static Task<CallToolResult> DescribeQueryResult(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query to describe.")] string sql,
        [Description("Optional named SQL parameters used to infer parameter definitions.")] Dictionary<string, object?>? parameters = null,
        [Description("Optional UI SQL template replacements such as { \"0\": \"1=1\" } for placeholders like {0}. Values are raw SQL fragments and the final SQL is still validated as read-only.")] Dictionary<string, object?>? templateValues = null,
        CancellationToken cancellationToken = default)
    {
        return service.DescribeQueryResultAsync(sql, parameters, templateValues, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return an actionable SQL Server estimated-plan summary without executing the target query; raw SHOWPLAN XML is optional.")]
    public static Task<CallToolResult> ExplainQueryPlan(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query to explain.")] string sql,
        [Description("Include raw SHOWPLAN XML. Defaults to false to keep the response compact.")] bool includeXml = false,
        CancellationToken cancellationToken = default)
    {
        return service.ExplainQueryPlanAsync(sql, includeXml, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Return only the compact estimated execution-plan summary for a guarded read-only SELECT or WITH CTE query.")]
    public static Task<CallToolResult> ExplainQueryPlanSummary(
        SqlServerToolService service,
        [Description("Single read-only SELECT or WITH CTE query to explain.")] string sql,
        CancellationToken cancellationToken = default)
    {
        return service.ExplainQueryPlanSummaryAsync(sql, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Run independent metadata lookups in parallel. Supported operations: resolve_object, describe_table, get_indexes, find_column, get_module_definition, and compare_module_to_file.")]
    public static Task<CallToolResult> BatchMetadata(
        SqlServerToolService service,
        [Description("Independent metadata requests; each item returns its own success or error result.")] MetadataBatchRequest[] requests,
        CancellationToken cancellationToken = default)
    {
        return service.BatchMetadataAsync(requests, cancellationToken);
    }

    [McpServerTool(ReadOnly = true), Description("Clear cached Credential Manager values, SQL connection pools, and metadata snapshots. The next DB request will reload them.")]
    public static Task<CallToolResult> ReloadConnection(
        SqlServerToolService service,
        CancellationToken cancellationToken)
    {
        return service.ReloadConnectionAsync(cancellationToken);
    }
}

public sealed record DeploymentModuleInput(string Schema, string Name, string FilePath);

public sealed record MetadataBatchRequest(
    string Operation,
    string? Schema = null,
    string? Name = null,
    string? Column = null,
    string? FilePath = null);
