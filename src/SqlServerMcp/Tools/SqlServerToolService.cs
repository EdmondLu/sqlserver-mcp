using System.Diagnostics;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Protocol;
using Serilog;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tools;

public sealed class SqlServerToolService
{
    private readonly SqlServerMcpOptions _options;
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly SqlMetadataService _metadataService;
    private readonly ILogger _logger;

    public SqlServerToolService(
        SqlServerMcpOptions options,
        SqlConnectionFactory connectionFactory,
        SqlMetadataService metadataService,
        ILogger logger)
    {
        _options = options;
        _connectionFactory = connectionFactory;
        _metadataService = metadataService;
        _logger = logger;
    }

    public Task<CallToolResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("test_connection", () => _metadataService.TestConnectionAsync(cancellationToken));
    }

    public Task<CallToolResult> HealthCheckAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("health_check", () => _metadataService.HealthCheckAsync(cancellationToken));
    }

    public Task<CallToolResult> FindObjectsAsync(
        string keyword,
        string[]? objectTypes,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync("find_objects", () => _metadataService.FindObjectsAsync(keyword, objectTypes, limit, cursor, cancellationToken));
    }

    public Task<CallToolResult> ResolveObjectAsync(
        string name,
        string? schema,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "resolve_object",
            () => _metadataService.ResolveObjectAsync(name, schema, objectTypes, limit, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> DescribeTableAsync(
        string schema,
        string name,
        string? mode,
        string[]? columns,
        bool? includeIndexes,
        bool? includeConstraints,
        bool? includeForeignKeys,
        bool? includeDefaults,
        bool? includeDescriptions,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "describe_table",
            () => _metadataService.DescribeTableAsync(
                schema,
                name,
                mode,
                columns,
                includeIndexes,
                includeConstraints,
                includeForeignKeys,
                includeDefaults,
                includeDescriptions,
                cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> GetObjectOverviewAsync(
        string schema,
        string name,
        bool includeColumns,
        bool includeIndexes,
        bool includeDependencies,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_object_overview",
            () => _metadataService.GetObjectOverviewAsync(schema, name, includeColumns, includeIndexes, includeDependencies, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> FindColumnAsync(
        string column,
        bool exact,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync("find_column", () => _metadataService.FindColumnAsync(column, exact, limit, cursor, cancellationToken));
    }

    public Task<CallToolResult> ProfileColumnAsync(
        string schema,
        string name,
        string column,
        string? expectedFormat,
        int sampleLimit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "profile_column",
            () => _metadataService.ProfileColumnAsync(
                schema,
                name,
                column,
                expectedFormat,
                sampleLimit,
                cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> GetIndexesAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync("get_indexes", () => _metadataService.GetIndexesAsync(schema, name, cancellationToken), schema, name);
    }

    public Task<CallToolResult> GetConstraintsAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync("get_constraints", () => _metadataService.GetConstraintsAsync(schema, name, cancellationToken), schema, name);
    }

    public Task<CallToolResult> GetForeignKeysAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync("get_foreign_keys", () => _metadataService.GetForeignKeysAsync(schema, name, cancellationToken), schema, name);
    }

    public Task<CallToolResult> SearchSqlModulesAsync(
        string keyword,
        string[]? objectTypes,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync("search_sql_modules", () => _metadataService.SearchSqlModulesAsync(keyword, objectTypes, limit, cursor, cancellationToken));
    }

    public Task<CallToolResult> GetModuleDefinitionAsync(
        string schema,
        string name,
        string? keyword,
        string[]? keywords,
        int? startLine,
        int? endLine,
        int? contextLines,
        int? beforeLines,
        int? afterLines,
        int? maxMatches,
        int? occurrence,
        bool collapseOverlaps,
        bool includeLineNumbers,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_module_definition",
            () => _metadataService.GetModuleDefinitionAsync(
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
                cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> ValidateTsqlScriptAsync(
        string script,
        string? sourceName,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "validate_tsql_script",
            () => _metadataService.ValidateTsqlScriptAsync(script, sourceName, cancellationToken));
    }

    public Task<CallToolResult> ValidateTsqlFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "validate_tsql_file",
            () => _metadataService.ValidateTsqlFileAsync(filePath, cancellationToken));
    }

    public Task<CallToolResult> CompareModuleToFileAsync(
        string schema,
        string name,
        string filePath,
        int? contextLines,
        string? diffMode,
        int? maxHunks,
        int? maxDiffLinesPerSide,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "compare_module_to_file",
            () => _metadataService.CompareModuleToFileAsync(
                schema,
                name,
                filePath,
                contextLines,
                diffMode,
                maxHunks,
                maxDiffLinesPerSide,
                cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> CompareModuleToRepoAsync(
        string schema,
        string name,
        string? root,
        string[]? patterns,
        string[]? excludePatterns,
        int? maxCandidates,
        int? contextLines,
        string? diffMode,
        int? maxHunks,
        int? maxDiffLinesPerSide,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "compare_module_to_repo",
            () => _metadataService.CompareModuleToRepoAsync(
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
                cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> CompareModulesToFilesAsync(
        DeploymentModuleInput[] modules,
        string? diffMode,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "compare_modules_to_files",
            () => _metadataService.CompareModulesToFilesAsync(modules, diffMode, cancellationToken));
    }

    public Task<CallToolResult> AnalyzeModuleTempTablesAsync(
        string schema,
        string name,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "analyze_module_temp_tables",
            () => _metadataService.AnalyzeModuleTempTablesAsync(schema, name, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> GetDependenciesAsync(
        string schema,
        string name,
        string? direction,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_dependencies",
            () => _metadataService.GetDependenciesAsync(schema, name, direction, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> GetCallersAsync(
        string schema,
        string name,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_callers",
            () => _metadataService.GetCallersAsync(schema, name, objectTypes, limit, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> GetCalleesAsync(
        string schema,
        string name,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_callees",
            () => _metadataService.GetCalleesAsync(schema, name, limit, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> GetDependencyGraphAsync(
        string schema,
        string name,
        string direction,
        int maxDepth,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_dependency_graph",
            () => _metadataService.GetDependencyGraphAsync(
                schema,
                name,
                direction,
                maxDepth,
                limit,
                cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> FindUsageAsync(
        string name,
        string? schema,
        string[]? objectTypes,
        string? matchMode,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "find_usage",
            () => _metadataService.FindUsageAsync(name, schema, objectTypes, matchMode, limit, cursor, cancellationToken),
            schema,
            name);
    }

    public Task<CallToolResult> SearchConfigTextAsync(
        string keyword,
        string? profile,
        int? limit,
        bool includeTargets,
        bool usableOnly,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "search_config_text",
            () => _metadataService.SearchConfigTextAsync(keyword, profile, limit, includeTargets, usableOnly, cursor, cancellationToken));
    }

    public Task<CallToolResult> FindFieldConsumersAsync(
        string column,
        string? schema,
        string? profile,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "find_field_consumers",
            () => _metadataService.FindFieldConsumersAsync(column, schema, profile, limit, cancellationToken),
            schema,
            column);
    }

    public Task<CallToolResult> FindPageByTableAsync(
        string name,
        string? profile,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "find_page_by_table",
            () => _metadataService.FindPageByConfiguredReferenceAsync(
                name,
                "table_or_view",
                profile,
                limit,
                cancellationToken));
    }

    public Task<CallToolResult> FindPageBySaveProcedureAsync(
        string name,
        string? profile,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "find_page_by_save_procedure",
            () => _metadataService.FindPageByConfiguredReferenceAsync(
                name,
                "save_procedure",
                profile,
                limit,
                cancellationToken));
    }

    public Task<CallToolResult> RunReadonlyQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        int? maxRows,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "run_readonly_query",
            () => _metadataService.RunReadonlyQueryAsync(sql, parameters, maxRows, cursor, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<CallToolResult> RunReadonlyBatchAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "run_readonly_batch",
            () => _metadataService.RunReadonlyBatchAsync(sql, parameters, maxRows, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<CallToolResult> DescribeQueryResultAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyDictionary<string, object?>? templateValues,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "describe_query_result",
            () => _metadataService.DescribeQueryResultAsync(sql, parameters, templateValues, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<CallToolResult> ExplainQueryPlanAsync(
        string sql,
        bool includeXml,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "explain_query_plan",
            () => _metadataService.ExplainQueryPlanAsync(sql, includeXml, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<CallToolResult> ExplainQueryPlanSummaryAsync(string sql, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "explain_query_plan_summary",
            () => _metadataService.ExplainQueryPlanAsync(sql, false, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<CallToolResult> BatchMetadataAsync(
        MetadataBatchRequest[] requests,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "batch_metadata",
            () => _metadataService.BatchMetadataAsync(requests, cancellationToken));
    }

    public Task<CallToolResult> ReloadConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connectionFactory.Reload();
        return Task.FromResult(JsonResponse.SuccessResult(
            "reload_connection",
            new
            {
                reloaded = true,
                message = "Credential cache and SQL connection pools were cleared."
            },
            _options,
            _connectionFactory.CachedUserName,
            0));
    }

    private async Task<CallToolResult> ExecuteAsync(
        string toolName,
        Func<Task<object>> action,
        string? schema = null,
        string? objectName = null,
        string? sql = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            stopwatch.Stop();

            _logger
                .ForContext("tool_name", toolName)
                .ForContext("elapsed_ms", stopwatch.ElapsedMilliseconds)
                .ForContext("database", _options.Database)
                .ForContext("schema", schema)
                .ForContext("object", objectName)
                .ForContext("sql", sql)
                .Information("MCP tool completed");

            return JsonResponse.SuccessResult(
                toolName,
                result,
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds);
        }
        catch (SqlMcpException ex)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ex.ErrorCode, ex.Message);
            var suggestions = ex.Suggestions.Count > 0
                ? ex.Suggestions
                : await GetSqlErrorSuggestionsSafeAsync(ex.SqlErrorNumber, ex.Message, sql);
            return JsonResponse.ErrorResult(
                toolName,
                ex.ErrorCode,
                ex.Message,
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Detail,
                ex.Hint,
                ex.SqlErrorNumber,
                ex.LineNumber,
                suggestions.Count > 0 ? suggestions : BuildSqlErrorSuggestions(ex.ErrorCode));
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.SqlTimeout, ex.Message);
            return JsonResponse.ErrorResult(
                toolName,
                ErrorCodes.SqlTimeout,
                "SQL command timed out.",
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Message,
                sqlErrorNumber: ex.Number,
                lineNumber: ex.LineNumber);
        }
        catch (SqlException ex) when (ex.Number == 1222)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.SqlLockTimeout, ex.Message);
            return JsonResponse.ErrorResult(
                toolName,
                ErrorCodes.SqlLockTimeout,
                "SQL lock timeout.",
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Message,
                sqlErrorNumber: ex.Number,
                lineNumber: ex.LineNumber);
        }
        catch (SqlException ex) when (ex.Message.Contains("SHOWPLAN", StringComparison.OrdinalIgnoreCase))
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.ShowplanPermissionRequired, ex.Message);
            return JsonResponse.ErrorResult(
                toolName,
                ErrorCodes.ShowplanPermissionRequired,
                "SQL Server denied SHOWPLAN permission.",
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Message,
                "Grant SHOWPLAN to the MCP login, for example: GRANT SHOWPLAN TO [readonly_user];",
                ex.Number,
                ex.LineNumber);
        }
        catch (SqlException ex)
        {
            stopwatch.Stop();
            var errorCode = ClassifySqlError(ex.Number);
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, errorCode, ex.Message);
            var suggestions = await GetSqlErrorSuggestionsSafeAsync(ex.Number, ex.Message, sql);
            return JsonResponse.ErrorResult(
                toolName,
                errorCode,
                ex.Message,
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Message,
                BuildSqlErrorHint(errorCode),
                ex.Number,
                ex.LineNumber,
                suggestions.Count > 0 ? suggestions : BuildSqlErrorSuggestions(errorCode));
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.SqlTimeout, ex.Message);
            return JsonResponse.ErrorResult(
                toolName,
                ErrorCodes.SqlTimeout,
                "Operation was cancelled.",
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.UnknownError, ex.Message);
            return JsonResponse.ErrorResult(
                toolName,
                ErrorCodes.UnknownError,
                "Unexpected MCP tool error.",
                _options,
                _connectionFactory.CachedUserName,
                stopwatch.ElapsedMilliseconds,
                ex.Message);
        }
    }

    internal static string ClassifySqlError(int number)
    {
        return number switch
        {
            207 => ErrorCodes.SqlInvalidColumn,
            208 => ErrorCodes.SqlInvalidObject,
            2715 => ErrorCodes.SqlInvalidType,
            201 or 8144 => ErrorCodes.SqlParameterMismatch,
            102 or 156 => ErrorCodes.SqlSyntaxError,
            _ => ErrorCodes.UnknownError
        };
    }

    private static string? BuildSqlErrorHint(string errorCode)
    {
        return errorCode switch
        {
            ErrorCodes.SqlInvalidObject => "Use resolve_object to find the intended table, view, procedure, or function.",
            ErrorCodes.SqlInvalidColumn => "Use describe_table or find_column to inspect available columns.",
            ErrorCodes.SqlInvalidType => "Use validate_tsql_script to verify referenced user-defined types.",
            ErrorCodes.SqlParameterMismatch => "Inspect the target procedure parameters or validate the T-SQL script.",
            ErrorCodes.SqlSyntaxError => "Use validate_tsql_script for line-and-column syntax diagnostics.",
            _ => null
        };
    }

    private static string[] BuildSqlErrorSuggestions(string errorCode)
    {
        return errorCode switch
        {
            ErrorCodes.SqlInvalidObject => ["Call resolve_object with the reported object name."],
            ErrorCodes.SqlInvalidColumn => ["Call describe_table for the source object.", "Call find_column with the reported column name."],
            ErrorCodes.SqlInvalidType => ["Call validate_tsql_script and inspect unresolved types."],
            ErrorCodes.SqlParameterMismatch => ["Compare supplied arguments with the module parameter definition."],
            ErrorCodes.SqlSyntaxError => ["Call validate_tsql_script to get parser diagnostics."],
            _ => []
        };
    }

    private async Task<IReadOnlyList<string>> GetSqlErrorSuggestionsSafeAsync(
        int? sqlErrorNumber,
        string message,
        string? sql)
    {
        if (sqlErrorNumber is not (207 or 208))
        {
            return [];
        }

        try
        {
            return await _metadataService.GetSqlErrorSuggestionsAsync(
                sqlErrorNumber.Value,
                message,
                sql,
                CancellationToken.None);
        }
        catch (Exception suggestionError)
        {
            _logger
                .ForContext("sql_error_number", sqlErrorNumber)
                .ForContext("suggestion_error", suggestionError.Message)
                .Debug("SQL error suggestion lookup failed");
            return [];
        }
    }

    private void LogToolError(
        string toolName,
        long elapsedMs,
        string? schema,
        string? objectName,
        string? sql,
        string errorCode,
        string errorMessage)
    {
        _logger
            .ForContext("tool_name", toolName)
            .ForContext("elapsed_ms", elapsedMs)
            .ForContext("database", _options.Database)
            .ForContext("schema", schema)
            .ForContext("object", objectName)
            .ForContext("sql", sql)
            .ForContext("error_code", errorCode)
            .ForContext("error_message", errorMessage)
            .Warning("MCP tool failed");
    }
}
