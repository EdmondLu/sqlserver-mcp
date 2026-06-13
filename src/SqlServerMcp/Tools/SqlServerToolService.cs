using System.Diagnostics;
using Microsoft.Data.SqlClient;
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

    public Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("test_connection", () => _metadataService.TestConnectionAsync(cancellationToken));
    }

    public Task<string> HealthCheckAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("health_check", () => _metadataService.HealthCheckAsync(cancellationToken));
    }

    public Task<string> FindObjectsAsync(
        string keyword,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync("find_objects", () => _metadataService.FindObjectsAsync(keyword, objectTypes, limit, cancellationToken));
    }

    public Task<string> DescribeTableAsync(
        string schema,
        string name,
        bool includeIndexes,
        bool includeConstraints,
        bool includeForeignKeys,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "describe_table",
            () => _metadataService.DescribeTableAsync(schema, name, includeIndexes, includeConstraints, includeForeignKeys, cancellationToken),
            schema,
            name);
    }

    public Task<string> GetObjectOverviewAsync(
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

    public Task<string> FindColumnAsync(
        string column,
        bool exact,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync("find_column", () => _metadataService.FindColumnAsync(column, exact, limit, cancellationToken));
    }

    public Task<string> GetIndexesAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync("get_indexes", () => _metadataService.GetIndexesAsync(schema, name, cancellationToken), schema, name);
    }

    public Task<string> GetConstraintsAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync("get_constraints", () => _metadataService.GetConstraintsAsync(schema, name, cancellationToken), schema, name);
    }

    public Task<string> GetForeignKeysAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync("get_foreign_keys", () => _metadataService.GetForeignKeysAsync(schema, name, cancellationToken), schema, name);
    }

    public Task<string> SearchSqlModulesAsync(
        string keyword,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync("search_sql_modules", () => _metadataService.SearchSqlModulesAsync(keyword, objectTypes, limit, cancellationToken));
    }

    public Task<string> GetModuleDefinitionAsync(string schema, string name, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "get_module_definition",
            () => _metadataService.GetModuleDefinitionAsync(schema, name, cancellationToken),
            schema,
            name);
    }

    public Task<string> GetDependenciesAsync(
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

    public Task<string> FindUsageAsync(
        string name,
        string? schema,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "find_usage",
            () => _metadataService.FindUsageAsync(name, schema, objectTypes, limit, cancellationToken),
            schema,
            name);
    }

    public Task<string> RunReadonlyQueryAsync(string sql, int? maxRows, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "run_readonly_query",
            () => _metadataService.RunReadonlyQueryAsync(sql, maxRows, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<string> ExplainQueryPlanAsync(string sql, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "explain_query_plan",
            () => _metadataService.ExplainQueryPlanAsync(sql, cancellationToken),
            sql: _options.Logging.LogSql ? sql : null);
    }

    public Task<string> ReloadConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connectionFactory.Reload();
        return Task.FromResult(JsonResponse.Success(new
        {
            reloaded = true,
            message = "Credential cache and SQL connection pools were cleared."
        }));
    }

    private async Task<string> ExecuteAsync(
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

            return JsonResponse.Success(result);
        }
        catch (SqlMcpException ex)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ex.ErrorCode, ex.Message);
            return JsonResponse.Error(ex.ErrorCode, ex.Message, ex.Detail, ex.Hint);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.SqlTimeout, ex.Message);
            return JsonResponse.Error(ErrorCodes.SqlTimeout, "SQL command timed out.", ex.Message);
        }
        catch (SqlException ex) when (ex.Number == 1222)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.SqlLockTimeout, ex.Message);
            return JsonResponse.Error(ErrorCodes.SqlLockTimeout, "SQL lock timeout.", ex.Message);
        }
        catch (SqlException ex) when (ex.Message.Contains("SHOWPLAN", StringComparison.OrdinalIgnoreCase))
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.ShowplanPermissionRequired, ex.Message);
            return JsonResponse.Error(
                ErrorCodes.ShowplanPermissionRequired,
                "SQL Server denied SHOWPLAN permission.",
                ex.Message,
                "Grant SHOWPLAN to the MCP login, for example: GRANT SHOWPLAN TO [readonly_user];");
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.SqlTimeout, ex.Message);
            return JsonResponse.Error(ErrorCodes.SqlTimeout, "Operation was cancelled.", ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogToolError(toolName, stopwatch.ElapsedMilliseconds, schema, objectName, sql, ErrorCodes.UnknownError, ex.Message);
            return JsonResponse.Error(ErrorCodes.UnknownError, "Unexpected MCP tool error.", ex.Message);
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
