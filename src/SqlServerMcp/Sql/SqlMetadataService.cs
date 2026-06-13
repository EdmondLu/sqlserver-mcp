using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Sql;

public sealed class SqlMetadataService
{
    private readonly SqlServerMcpOptions _options;
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ReadonlySqlGuard _sqlGuard;

    public SqlMetadataService(
        SqlServerMcpOptions options,
        SqlConnectionFactory connectionFactory,
        ReadonlySqlGuard sqlGuard)
    {
        _options = options;
        _connectionFactory = connectionFactory;
        _sqlGuard = sqlGuard;
    }

    public async Task<object> TestConnectionAsync(CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               database_name=DB_NAME(),
                               login_name=SUSER_SNAME(),
                               user_name=USER_NAME(),
                               server_name=@@SERVERNAME;
                           """;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SqlMcpException(ErrorCodes.SqlConnectionFailed, "Connection test returned no rows.");
        }

        return new
        {
            databaseName = reader.GetString(0),
            loginName = reader.GetString(1),
            userName = reader.GetString(2),
            serverName = reader.GetString(3)
        };
    }

    public async Task<object> HealthCheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        object? connection = null;
        object? permissions = null;
        object? error = null;

        try
        {
            const string sql = """
                               SELECT
                                   database_name=DB_NAME(),
                                   login_name=SUSER_SNAME(),
                                   user_name=USER_NAME(),
                                   server_name=@@SERVERNAME,
                                   has_select=HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'SELECT'),
                                   has_view_definition=HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'),
                                   has_showplan=HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'SHOWPLAN'),
                                   is_db_datareader=IS_ROLEMEMBER(N'db_datareader'),
                                   is_db_datawriter=IS_ROLEMEMBER(N'db_datawriter');
                               """;

            var rows = await QueryAsync(
                sql,
                [],
                reader => new
                {
                    databaseName = reader.GetString("database_name"),
                    loginName = reader.GetString("login_name"),
                    userName = reader.GetString("user_name"),
                    serverName = reader.GetString("server_name"),
                    hasSelect = reader.GetNullableInt32("has_select") == 1,
                    hasViewDefinition = reader.GetNullableInt32("has_view_definition") == 1,
                    hasShowplan = reader.GetNullableInt32("has_showplan") == 1,
                    isDbDatareader = reader.GetNullableInt32("is_db_datareader") == 1,
                    isDbDatawriter = reader.GetNullableInt32("is_db_datawriter") == 1
                },
                cancellationToken);

            var row = rows.Single();
            connection = new
            {
                ok = true,
                row.databaseName,
                row.loginName,
                row.userName,
                row.serverName
            };
            permissions = new
            {
                row.hasSelect,
                row.hasViewDefinition,
                row.hasShowplan,
                row.isDbDatareader,
                row.isDbDatawriter,
                recommendations = new[]
                {
                    row.hasViewDefinition ? null : $"GRANT VIEW DEFINITION TO {row.userName};",
                    row.hasShowplan ? null : $"GRANT SHOWPLAN TO {row.userName};"
                }.OfType<string>().ToArray()
            };
        }
        catch (SqlMcpException ex)
        {
            connection = new { ok = false };
            error = new
            {
                errorCode = ex.ErrorCode,
                message = ex.Message,
                detail = ex.Detail,
                hint = ex.Hint
            };
        }
        catch (Exception ex)
        {
            connection = new { ok = false };
            error = new
            {
                errorCode = ErrorCodes.UnknownError,
                message = ex.Message
            };
        }

        stopwatch.Stop();
        return new
        {
            ok = error is null,
            config = new
            {
                configPath = _options.ConfigPath,
                server = _options.Server,
                database = _options.Database,
                credentialTarget = _options.CredentialTarget,
                runtime = new
                {
                    logDirectory = _options.Runtime.LogDirectory,
                    cacheDirectory = _options.Runtime.CacheDirectory,
                    tempDirectory = _options.Runtime.TempDirectory,
                    logDirectoryExists = Directory.Exists(_options.Runtime.LogDirectory),
                    cacheDirectoryExists = Directory.Exists(_options.Runtime.CacheDirectory),
                    tempDirectoryExists = Directory.Exists(_options.Runtime.TempDirectory)
                }
            },
            limits = _options.Limits,
            security = _options.Security,
            connection,
            permissions,
            error,
            elapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    public async Task<object> FindObjectsAsync(
        string keyword,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        var terms = SplitKeyword(keyword);
        if (terms.Count == 0)
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "keyword is required.");
        }

        var objectTypeCodes = ObjectTypeMapper.MapObjectTypes(objectTypes);
        var effectiveLimit = _options.Limits.ClampRows(limit);
        var rowLimit = Math.Min(effectiveLimit * 20, 5000);
        var parameters = new List<SqlParameter>
        {
            new("@rowLimit", SqlDbType.Int) { Value = rowLimit }
        };

        var typePredicates = BuildInPredicate("O.type", "type", objectTypeCodes, parameters);
        var termPredicates = new List<string>();

        for (var i = 0; i < terms.Count; i++)
        {
            var parameterName = $"@term{i}";
            parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 4000) { Value = $"%{terms[i]}%" });
            termPredicates.Add($"""
                                O.name LIKE {parameterName}
                                OR S.name LIKE {parameterName}
                                OR CONVERT(NVARCHAR(4000), OEP.value) LIKE {parameterName}
                                OR C.name LIKE {parameterName}
                                OR CONVERT(NVARCHAR(4000), CEP.value) LIKE {parameterName}
                                """);
        }

        var sql = $"""
                   SELECT TOP (@rowLimit)
                       object_id=O.object_id,
                       schema_name=S.name,
                       object_name=O.name,
                       object_type=O.type,
                       object_type_desc=O.type_desc,
                       description=CONVERT(NVARCHAR(4000), OEP.value),
                       column_name=C.name,
                       column_description=CONVERT(NVARCHAR(4000), CEP.value),
                       matched_object_name=CASE WHEN ({BuildLikeAny("O.name", terms.Count)}) THEN 1 ELSE 0 END,
                       matched_schema_name=CASE WHEN ({BuildLikeAny("S.name", terms.Count)}) THEN 1 ELSE 0 END,
                       matched_description=CASE WHEN ({BuildLikeAny("CONVERT(NVARCHAR(4000), OEP.value)", terms.Count)}) THEN 1 ELSE 0 END,
                       matched_column_name=CASE WHEN ({BuildLikeAny("C.name", terms.Count)}) THEN 1 ELSE 0 END,
                       matched_column_description=CASE WHEN ({BuildLikeAny("CONVERT(NVARCHAR(4000), CEP.value)", terms.Count)}) THEN 1 ELSE 0 END
                   FROM sys.objects O
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   LEFT JOIN sys.extended_properties OEP ON OEP.class=1
                       AND OEP.major_id=O.object_id
                       AND OEP.minor_id=0
                       AND OEP.name=N'MS_Description'
                   LEFT JOIN sys.columns C ON C.object_id=O.object_id
                   LEFT JOIN sys.extended_properties CEP ON CEP.class=1
                       AND CEP.major_id=C.object_id
                       AND CEP.minor_id=C.column_id
                       AND CEP.name=N'MS_Description'
                   WHERE O.is_ms_shipped=0
                       AND {typePredicates}
                       AND ({string.Join(" OR ", termPredicates.Select(p => $"({p})"))})
                   ORDER BY O.name, C.column_id;
                   """;

        var rows = await QueryAsync(sql, parameters, reader => new ObjectMatchRow(
            reader.GetInt32("object_id"),
            reader.GetString("schema_name"),
            reader.GetString("object_name"),
            reader.GetString("object_type"),
            reader.GetString("object_type_desc"),
            reader.GetNullableString("description"),
            reader.GetNullableString("column_name"),
            reader.GetNullableString("column_description"),
            reader.GetBooleanFromInt("matched_object_name"),
            reader.GetBooleanFromInt("matched_schema_name"),
            reader.GetBooleanFromInt("matched_description"),
            reader.GetBooleanFromInt("matched_column_name"),
            reader.GetBooleanFromInt("matched_column_description")),
            cancellationToken);

        var items = rows
            .GroupBy(row => row.ObjectId)
            .Take(effectiveLimit)
            .Select(group =>
            {
                var first = group.First();
                var matchedBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var matchedColumns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var row in group)
                {
                    if (row.MatchedObjectName) matchedBy.Add("object_name");
                    if (row.MatchedSchemaName) matchedBy.Add("schema_name");
                    if (row.MatchedDescription) matchedBy.Add("description");
                    if (row.MatchedColumnName) matchedBy.Add("column_name");
                    if (row.MatchedColumnDescription) matchedBy.Add("column_description");
                    if ((row.MatchedColumnName || row.MatchedColumnDescription) && !string.IsNullOrWhiteSpace(row.ColumnName))
                    {
                        matchedColumns.Add(row.ColumnName);
                    }
                }

                return new
                {
                    schema = first.SchemaName,
                    name = first.ObjectName,
                    type = ObjectTypeMapper.ToPublicType(first.ObjectType),
                    typeDesc = first.ObjectTypeDesc,
                    description = first.Description,
                    matchedBy = matchedBy.ToArray(),
                    matchedColumns = matchedColumns.ToArray()
                };
            })
            .ToArray();

        return new
        {
            items,
            count = items.Length,
            truncated = rows.Select(row => row.ObjectId).Distinct().Count() > effectiveLimit
        };
    }

    public async Task<object> DescribeTableAsync(
        string schema,
        string name,
        bool includeIndexes,
        bool includeConstraints,
        bool includeForeignKeys,
        CancellationToken cancellationToken)
    {
        var resolution = await GetStructureObjectAsync(schema, name, cancellationToken);
        var dbObject = resolution.Object;
        var columns = await GetColumnsAsync(dbObject.ObjectId, cancellationToken);

        return new
        {
            schema = dbObject.Schema,
            name = dbObject.Name,
            type = ObjectTypeMapper.ToPublicType(dbObject.Type),
            typeDesc = dbObject.TypeDesc,
            description = dbObject.Description,
            resolution = BuildResolutionInfo(resolution),
            columns,
            indexes = includeIndexes ? await GetIndexesCoreAsync(dbObject.ObjectId, cancellationToken) : null,
            constraints = includeConstraints ? await GetConstraintsCoreAsync(dbObject.ObjectId, cancellationToken) : null,
            foreignKeys = includeForeignKeys ? await GetForeignKeysCoreAsync(dbObject.ObjectId, cancellationToken) : null
        };
    }

    public async Task<object> GetObjectOverviewAsync(
        string schema,
        string name,
        bool includeColumns,
        bool includeIndexes,
        bool includeDependencies,
        CancellationToken cancellationToken)
    {
        var structureResolution = MapStructureObjectName(name) is not null
            ? await GetStructureObjectAsync(schema, name, cancellationToken)
            : null;
        var dbObject = structureResolution?.Object
            ?? await GetObjectAsync(
                schema,
                name,
                ["U", "V", "P", "PC", "FN", "IF", "TF", "FS", "FT", "TR"],
                cancellationToken);

        var isTableOrView = dbObject.Type is "U" or "V";
        var storage = isTableOrView
            ? await GetStorageSummaryAsync(dbObject.ObjectId, cancellationToken)
            : null;

        return new
        {
            schema = dbObject.Schema,
            name = dbObject.Name,
            type = ObjectTypeMapper.ToPublicType(dbObject.Type),
            typeDesc = dbObject.TypeDesc,
            description = dbObject.Description,
            createDate = dbObject.CreateDate,
            modifyDate = dbObject.ModifyDate,
            resolution = structureResolution is null ? null : BuildResolutionInfo(structureResolution),
            storage,
            columns = includeColumns && isTableOrView
                ? await GetColumnsAsync(dbObject.ObjectId, cancellationToken)
                : null,
            indexes = includeIndexes && isTableOrView
                ? await GetIndexesCoreAsync(dbObject.ObjectId, cancellationToken)
                : null,
            constraints = isTableOrView
                ? await GetConstraintsCoreAsync(dbObject.ObjectId, cancellationToken)
                : null,
            foreignKeys = isTableOrView
                ? await GetForeignKeysCoreAsync(dbObject.ObjectId, cancellationToken)
                : null,
            dependencies = includeDependencies
                ? await GetDependenciesAsync(dbObject.Schema, dbObject.Name, null, cancellationToken)
                : null
        };
    }

    public async Task<object> FindColumnAsync(
        string column,
        bool exact,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            throw new SqlMcpException(ErrorCodes.ColumnNotFound, "column is required.");
        }

        var effectiveLimit = _options.Limits.ClampRows(limit);
        var sql = $"""
                   SELECT TOP (@limit)
                       schema_name=S.name,
                       object_name=O.name,
                       object_type=O.type,
                       object_type_desc=O.type_desc,
                       column_name=C.name,
                       data_type=T.name,
                       max_length=C.max_length,
                       precision=C.precision,
                       scale=C.scale,
                       is_nullable=C.is_nullable,
                       column_id=C.column_id,
                       description=CONVERT(NVARCHAR(4000), EP.value)
                   FROM sys.columns C
                   INNER JOIN sys.objects O ON O.object_id=C.object_id
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   INNER JOIN sys.types T ON T.user_type_id=C.user_type_id
                   LEFT JOIN sys.extended_properties EP ON EP.class=1
                       AND EP.major_id=C.object_id
                       AND EP.minor_id=C.column_id
                       AND EP.name=N'MS_Description'
                   WHERE O.type IN (N'U', N'V')
                       AND O.is_ms_shipped=0
                       AND C.name {(exact ? "= @column" : "LIKE @column")}
                   ORDER BY C.name, S.name, O.name;
                   """;

        var parameterValue = exact ? column : $"%{column}%";
        var rows = await QueryAsync(
            sql,
            [
                new("@limit", SqlDbType.Int) { Value = effectiveLimit },
                new("@column", SqlDbType.NVarChar, 256) { Value = parameterValue }
            ],
            reader => new
            {
                schema = reader.GetString("schema_name"),
                objectName = reader.GetString("object_name"),
                objectType = ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                objectTypeDesc = reader.GetString("object_type_desc"),
                columnName = reader.GetString("column_name"),
                dataType = reader.GetString("data_type"),
                maxLength = NormalizeMaxLength(reader.GetInt16("max_length"), reader.GetString("data_type")),
                precision = reader.GetByte("precision"),
                scale = reader.GetByte("scale"),
                nullable = reader.GetBoolean("is_nullable"),
                ordinal = reader.GetInt32("column_id"),
                description = reader.GetNullableString("description")
            },
            cancellationToken);

        return new
        {
            items = rows,
            count = rows.Count
        };
    }

    public async Task<object> GetIndexesAsync(string schema, string name, CancellationToken cancellationToken)
    {
        var resolution = await GetStructureObjectAsync(schema, name, cancellationToken);
        var dbObject = resolution.Object;
        return new
        {
            schema = dbObject.Schema,
            name = dbObject.Name,
            resolution = BuildResolutionInfo(resolution),
            indexes = await GetIndexesCoreAsync(dbObject.ObjectId, cancellationToken)
        };
    }

    public async Task<object> GetConstraintsAsync(string schema, string name, CancellationToken cancellationToken)
    {
        var resolution = await GetStructureObjectAsync(schema, name, cancellationToken);
        var dbObject = resolution.Object;
        return new
        {
            schema = dbObject.Schema,
            name = dbObject.Name,
            resolution = BuildResolutionInfo(resolution),
            constraints = await GetConstraintsCoreAsync(dbObject.ObjectId, cancellationToken)
        };
    }

    public async Task<object> GetForeignKeysAsync(string schema, string name, CancellationToken cancellationToken)
    {
        var resolution = await GetStructureObjectAsync(schema, name, cancellationToken);
        var dbObject = resolution.Object;
        return new
        {
            schema = dbObject.Schema,
            name = dbObject.Name,
            resolution = BuildResolutionInfo(resolution),
            foreignKeys = await GetForeignKeysCoreAsync(dbObject.ObjectId, cancellationToken)
        };
    }

    public async Task<object> SearchSqlModulesAsync(
        string keyword,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        var terms = SplitKeyword(keyword);
        if (terms.Count == 0)
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "keyword is required.");
        }

        var objectTypeCodes = ObjectTypeMapper.MapModuleTypes(objectTypes);
        var effectiveLimit = _options.Limits.ClampRows(limit);
        var parameters = new List<SqlParameter>
        {
            new("@limit", SqlDbType.Int) { Value = effectiveLimit }
        };

        var termPredicates = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            var parameterName = $"@term{i}";
            parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 4000) { Value = $"%{terms[i]}%" });
            termPredicates.Add($"M.definition LIKE {parameterName}");
        }

        var typePredicates = BuildInPredicate("O.type", "type", objectTypeCodes, parameters);
        var sql = $"""
                   SELECT TOP (@limit)
                       schema_name=S.name,
                       object_name=O.name,
                       object_type=O.type,
                       object_type_desc=O.type_desc,
                       definition=M.definition,
                       modify_date=O.modify_date
                   FROM sys.sql_modules M
                   INNER JOIN sys.objects O ON O.object_id=M.object_id
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   WHERE O.is_ms_shipped=0
                       AND {typePredicates}
                       AND ({string.Join(" OR ", termPredicates)})
                   ORDER BY O.modify_date DESC, S.name, O.name;
                   """;

        var rows = await QueryAsync(
            sql,
            parameters,
            reader =>
            {
                var definition = reader.GetNullableString("definition") ?? string.Empty;
                return new
                {
                    schema = reader.GetString("schema_name"),
                    name = reader.GetString("object_name"),
                    type = ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                    typeDesc = reader.GetString("object_type_desc"),
                    matchedSnippet = BuildSnippet(definition, terms[0]),
                    modifyDate = reader.GetDateTime("modify_date")
                };
            },
            cancellationToken);

        return new
        {
            items = rows,
            count = rows.Count
        };
    }

    public async Task<object> GetModuleDefinitionAsync(string schema, string name, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT TOP 1
                               schema_name=S.name,
                               object_name=O.name,
                               object_type=O.type,
                               object_type_desc=O.type_desc,
                               definition=OBJECT_DEFINITION(O.object_id)
                           FROM sys.objects O
                           INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                           WHERE S.name=@schema
                               AND O.name=@name
                               AND O.type IN (N'V', N'P', N'PC', N'FN', N'IF', N'TF', N'TR');
                           """;

        var rows = await QueryAsync(
            sql,
            [
                new("@schema", SqlDbType.NVarChar, 128) { Value = schema },
                new("@name", SqlDbType.NVarChar, 128) { Value = name }
            ],
            reader => new
            {
                schema = reader.GetString("schema_name"),
                name = reader.GetString("object_name"),
                type = ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                typeDesc = reader.GetString("object_type_desc"),
                definition = reader.GetNullableString("definition")
            },
            cancellationToken);

        var module = rows.SingleOrDefault();
        if (module is null)
        {
            throw new SqlMcpException(ErrorCodes.ObjectNotFound, $"Module '{schema}.{name}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(module.definition))
        {
            throw new SqlMcpException(
                ErrorCodes.ViewDefinitionPermissionRequired,
                $"Definition for '{schema}.{name}' is not available.",
                "OBJECT_DEFINITION returned NULL.",
                "Grant VIEW DEFINITION to the MCP SQL login, or inspect the module definition from source control.");
        }

        return module;
    }

    public async Task<object> GetDependenciesAsync(
        string schema,
        string name,
        string? direction,
        CancellationToken cancellationToken)
    {
        var dbObject = await GetObjectAsync(
            schema,
            name,
            ["U", "V", "P", "PC", "FN", "IF", "TF", "FS", "FT", "TR"],
            cancellationToken);

        var normalizedDirection = string.IsNullOrWhiteSpace(direction)
            ? "both"
            : direction.Trim().ToLowerInvariant();

        if (normalizedDirection is not ("incoming" or "outgoing" or "both"))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "direction must be incoming, outgoing, or both.");
        }

        return new
        {
            schema = dbObject.Schema,
            name = dbObject.Name,
            type = ObjectTypeMapper.ToPublicType(dbObject.Type),
            outgoing = normalizedDirection is "outgoing" or "both"
                ? await GetOutgoingDependenciesAsync(dbObject.ObjectId, cancellationToken)
                : null,
            incoming = normalizedDirection is "incoming" or "both"
                ? await GetIncomingDependenciesAsync(dbObject.ObjectId, cancellationToken)
                : null,
            textMatches = normalizedDirection is "incoming" or "both"
                ? await GetIncomingTextMatchesAsync(dbObject.Schema, dbObject.Name, dbObject.ObjectId, 30, cancellationToken)
                : null
        };
    }

    public async Task<object> FindUsageAsync(
        string name,
        string? schema,
        string[]? objectTypes,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "name is required.");
        }

        var effectiveLimit = _options.Limits.ClampRows(limit);
        var columnMatches = await FindUsageColumnMatchesAsync(name, schema, effectiveLimit, cancellationToken);
        var moduleMatches = await FindUsageModuleMatchesAsync(name, schema, objectTypes, effectiveLimit, cancellationToken);

        return new
        {
            name,
            schema,
            columnMatches,
            moduleMatches,
            columnMatchCount = columnMatches.Length,
            moduleMatchCount = moduleMatches.Length
        };
    }

    public async Task<object> ExplainQueryPlanAsync(string sql, CancellationToken cancellationToken)
    {
        _sqlGuard.ValidateShowplanQuery(sql);

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        await ExecuteNonQueryAsync(connection, "SET SHOWPLAN_XML ON;", cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, sql);
            command.CommandTimeout = _options.Limits.CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var plans = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    plans.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }

            stopwatch.Stop();
            return new
            {
                statementCount = plans.Count,
                showplanXml = plans,
                elapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, "SET SHOWPLAN_XML OFF;", CancellationToken.None);
        }
    }

    public async Task<object> RunReadonlyQueryAsync(string sql, int? maxRows, CancellationToken cancellationToken)
    {
        _sqlGuard.ValidateReadonlyQuery(sql);

        var effectiveMaxRows = _options.Limits.ClampRows(maxRows);
        var resultLimitBytes = _options.Limits.MaxResultMb * 1024L * 1024L;
        var stopwatch = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, object?>>();
        var columns = new List<object>();
        long estimatedBytes = 0;
        var truncated = false;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(
            connection,
            $"SET LOCK_TIMEOUT {_options.Limits.LockTimeoutMs};\n{sql}");
        command.CommandTimeout = _options.Limits.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new
            {
                name = reader.GetName(i),
                dataType = reader.GetDataTypeName(i)
            });
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= effectiveMaxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = ReadValue(reader, i);
                row[reader.GetName(i)] = value;
                estimatedBytes += EstimateBytes(value);
            }

            if (estimatedBytes > resultLimitBytes)
            {
                throw new SqlMcpException(
                    ErrorCodes.ResultTooLarge,
                    "Query result exceeded configured maxResultMb.",
                    $"Estimated payload exceeded {_options.Limits.MaxResultMb} MB.",
                    "Reduce selected columns, add filters, or lower maxRows.");
            }

            rows.Add(row);
        }

        stopwatch.Stop();
        return new
        {
            columns,
            rows,
            rowCount = rows.Count,
            truncated,
            elapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<DbObjectInfo> GetObjectAsync(
        string schema,
        string name,
        string[] typeCodes,
        CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter>
        {
            new("@schema", SqlDbType.NVarChar, 128) { Value = schema },
            new("@name", SqlDbType.NVarChar, 128) { Value = name }
        };

        var typePredicates = BuildInPredicate("O.type", "type", typeCodes, parameters);
        var sql = $"""
                   SELECT TOP 1
                       object_id=O.object_id,
                       schema_name=S.name,
                       object_name=O.name,
                       object_type=O.type,
                       object_type_desc=O.type_desc,
                       create_date=O.create_date,
                       modify_date=O.modify_date,
                       description=CONVERT(NVARCHAR(4000), EP.value)
                   FROM sys.objects O
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   LEFT JOIN sys.extended_properties EP ON EP.class=1
                       AND EP.major_id=O.object_id
                       AND EP.minor_id=0
                       AND EP.name=N'MS_Description'
                   WHERE S.name=@schema
                       AND O.name=@name
                       AND {typePredicates};
                   """;

        var rows = await QueryAsync(
            sql,
            parameters,
            reader => new DbObjectInfo(
                reader.GetInt32("object_id"),
                reader.GetString("schema_name"),
                reader.GetString("object_name"),
                reader.GetString("object_type").Trim(),
                reader.GetString("object_type_desc"),
                reader.GetDateTime("create_date"),
                reader.GetDateTime("modify_date"),
                reader.GetNullableString("description")),
            cancellationToken);

        return rows.SingleOrDefault()
            ?? throw new SqlMcpException(ErrorCodes.ObjectNotFound, $"Object '{schema}.{name}' was not found.");
    }

    private async Task<StructureObjectResolution> GetStructureObjectAsync(
        string schema,
        string name,
        CancellationToken cancellationToken)
    {
        var mappedName = MapStructureObjectName(name);
        if (mappedName is null)
        {
            return new StructureObjectResolution(
                await GetObjectAsync(schema, name, ["U", "V"], cancellationToken),
                schema,
                name,
                null,
                false,
                false);
        }

        try
        {
            return new StructureObjectResolution(
                await GetObjectAsync(schema, mappedName, ["U", "V"], cancellationToken),
                schema,
                name,
                mappedName,
                true,
                false);
        }
        catch (SqlMcpException ex) when (ex.ErrorCode == ErrorCodes.ObjectNotFound)
        {
            return new StructureObjectResolution(
                await GetObjectAsync(schema, name, ["U", "V"], cancellationToken),
                schema,
                name,
                mappedName,
                false,
                true);
        }
    }

    internal static string? MapStructureObjectName(string name)
    {
        string[] prefixes = ["vwpr_", "vwtr_", "vwp_", "vwt_"];
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return null;
    }

    private static object? BuildResolutionInfo(StructureObjectResolution resolution)
    {
        if (resolution.MappedName is null)
        {
            return null;
        }

        return new
        {
            requestedSchema = resolution.RequestedSchema,
            requestedName = resolution.RequestedName,
            mappedName = resolution.MappedName,
            resolvedSchema = resolution.Object.Schema,
            resolvedName = resolution.Object.Name,
            resolvedFromPrefix = resolution.ResolvedFromPrefix,
            usedFallback = resolution.UsedFallback
        };
    }

    private async Task<object?> GetStorageSummaryAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               row_count=SUM(PS.row_count),
                               reserved_mb=CONVERT(decimal(18,2), SUM(AU.total_pages) * 8.0 / 1024.0),
                               used_mb=CONVERT(decimal(18,2), SUM(AU.used_pages) * 8.0 / 1024.0),
                               data_mb=CONVERT(decimal(18,2), SUM(AU.data_pages) * 8.0 / 1024.0)
                           FROM sys.dm_db_partition_stats PS
                           LEFT JOIN sys.allocation_units AU ON AU.container_id=PS.partition_id
                           WHERE PS.object_id=@objectId
                               AND PS.index_id IN (0, 1);
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                rowCount = reader.IsDBNull(reader.GetOrdinal("row_count")) ? 0L : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("row_count"))),
                reservedMb = reader.IsDBNull(reader.GetOrdinal("reserved_mb")) ? 0m : reader.GetDecimal(reader.GetOrdinal("reserved_mb")),
                usedMb = reader.IsDBNull(reader.GetOrdinal("used_mb")) ? 0m : reader.GetDecimal(reader.GetOrdinal("used_mb")),
                dataMb = reader.IsDBNull(reader.GetOrdinal("data_mb")) ? 0m : reader.GetDecimal(reader.GetOrdinal("data_mb"))
            },
            cancellationToken);

        return rows.SingleOrDefault();
    }

    private async Task<object[]> GetOutgoingDependenciesAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               referenced_server_name=D.referenced_server_name,
                               referenced_database_name=D.referenced_database_name,
                               referenced_schema_name=COALESCE(S.name, D.referenced_schema_name),
                               referenced_entity_name=COALESCE(O.name, D.referenced_entity_name),
                               referenced_minor_name=C.name,
                               referenced_type=O.type,
                               referenced_type_desc=O.type_desc,
                               referenced_class_desc=D.referenced_class_desc,
                               is_caller_dependent=D.is_caller_dependent,
                               is_ambiguous=D.is_ambiguous
                           FROM sys.sql_expression_dependencies D
                           LEFT JOIN sys.objects O ON O.object_id=D.referenced_id
                           LEFT JOIN sys.schemas S ON S.schema_id=O.schema_id
                           LEFT JOIN sys.columns C ON C.object_id=D.referenced_id
                               AND C.column_id=D.referenced_minor_id
                           WHERE D.referencing_id=@objectId
                           ORDER BY referenced_schema_name, referenced_entity_name, referenced_minor_name;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                referencedServer = reader.GetNullableString("referenced_server_name"),
                referencedDatabase = reader.GetNullableString("referenced_database_name"),
                referencedSchema = reader.GetNullableString("referenced_schema_name"),
                referencedName = reader.GetNullableString("referenced_entity_name"),
                referencedColumn = reader.GetNullableString("referenced_minor_name"),
                referencedType = ObjectTypeMapper.ToPublicType(reader.GetNullableString("referenced_type") ?? string.Empty),
                referencedTypeDesc = reader.GetNullableString("referenced_type_desc"),
                referencedClassDesc = reader.GetNullableString("referenced_class_desc"),
                isCallerDependent = reader.GetBoolean("is_caller_dependent"),
                isAmbiguous = reader.GetBoolean("is_ambiguous")
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> GetIncomingDependenciesAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               referencing_schema_name=S.name,
                               referencing_entity_name=O.name,
                               referencing_type=O.type,
                               referencing_type_desc=O.type_desc,
                               referenced_minor_name=C.name,
                               is_caller_dependent=D.is_caller_dependent,
                               is_ambiguous=D.is_ambiguous
                           FROM sys.sql_expression_dependencies D
                           INNER JOIN sys.objects O ON O.object_id=D.referencing_id
                           INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                           LEFT JOIN sys.columns C ON C.object_id=D.referenced_id
                               AND C.column_id=D.referenced_minor_id
                           WHERE D.referenced_id=@objectId
                           ORDER BY S.name, O.name, D.referenced_minor_id;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                referencingSchema = reader.GetString("referencing_schema_name"),
                referencingName = reader.GetString("referencing_entity_name"),
                referencingType = ObjectTypeMapper.ToPublicType(reader.GetString("referencing_type")),
                referencingTypeDesc = reader.GetString("referencing_type_desc"),
                referencedColumn = reader.GetNullableString("referenced_minor_name"),
                isCallerDependent = reader.GetBoolean("is_caller_dependent"),
                isAmbiguous = reader.GetBoolean("is_ambiguous")
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> GetIncomingTextMatchesAsync(
        string schema,
        string name,
        int objectId,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT TOP (@limit)
                               schema_name=S.name,
                               object_name=O.name,
                               object_type=O.type,
                               object_type_desc=O.type_desc,
                               definition=M.definition,
                               modify_date=O.modify_date
                           FROM sys.sql_modules M
                           INNER JOIN sys.objects O ON O.object_id=M.object_id
                           INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                           WHERE O.object_id<>@objectId
                               AND O.is_ms_shipped=0
                               AND (
                                   M.definition LIKE @schemaDotName
                                   OR M.definition LIKE @bracketSchemaDotName
                                   OR M.definition LIKE @name
                                   OR M.definition LIKE @bracketName
                               )
                           ORDER BY O.modify_date DESC, S.name, O.name;
                           """;

        var rows = await QueryAsync(
            sql,
            [
                new("@limit", SqlDbType.Int) { Value = limit },
                new("@objectId", SqlDbType.Int) { Value = objectId },
                new("@schemaDotName", SqlDbType.NVarChar, 4000) { Value = $"%{schema}.{name}%" },
                new("@bracketSchemaDotName", SqlDbType.NVarChar, 4000) { Value = $"%[{schema}].[{name}]%" },
                new("@name", SqlDbType.NVarChar, 4000) { Value = $"%{name}%" },
                new("@bracketName", SqlDbType.NVarChar, 4000) { Value = $"%[{name}]%" }
            ],
            reader =>
            {
                var definition = reader.GetNullableString("definition") ?? string.Empty;
                return new
                {
                    schema = reader.GetString("schema_name"),
                    name = reader.GetString("object_name"),
                    type = ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                    typeDesc = reader.GetString("object_type_desc"),
                    matchedSnippet = BuildSnippet(definition, name),
                    modifyDate = reader.GetDateTime("modify_date")
                };
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> FindUsageColumnMatchesAsync(
        string name,
        string? schema,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = $"""
                   SELECT TOP (@limit)
                       schema_name=S.name,
                       object_name=O.name,
                       object_type=O.type,
                       object_type_desc=O.type_desc,
                       column_name=C.name,
                       column_id=C.column_id
                   FROM sys.columns C
                   INNER JOIN sys.objects O ON O.object_id=C.object_id
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   WHERE O.type IN (N'U', N'V')
                       AND O.is_ms_shipped=0
                       AND C.name=@name
                       {(string.IsNullOrWhiteSpace(schema) ? string.Empty : "AND S.name=@schema")}
                   ORDER BY S.name, O.name, C.column_id;
                   """;

        var parameters = new List<SqlParameter>
        {
            new("@limit", SqlDbType.Int) { Value = limit },
            new("@name", SqlDbType.NVarChar, 128) { Value = name }
        };

        if (!string.IsNullOrWhiteSpace(schema))
        {
            parameters.Add(new("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        }

        var rows = await QueryAsync(
            sql,
            parameters,
            reader => new
            {
                schema = reader.GetString("schema_name"),
                objectName = reader.GetString("object_name"),
                objectType = ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                objectTypeDesc = reader.GetString("object_type_desc"),
                columnName = reader.GetString("column_name"),
                ordinal = reader.GetInt32("column_id")
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> FindUsageModuleMatchesAsync(
        string name,
        string? schema,
        string[]? objectTypes,
        int limit,
        CancellationToken cancellationToken)
    {
        var typeCodes = ObjectTypeMapper.MapModuleTypes(objectTypes);
        var parameters = new List<SqlParameter>
        {
            new("@limit", SqlDbType.Int) { Value = limit },
            new("@name", SqlDbType.NVarChar, 4000) { Value = $"%{name}%" },
            new("@bracketName", SqlDbType.NVarChar, 4000) { Value = $"%[{name}]%" }
        };

        var schemaPredicate = string.Empty;
        if (!string.IsNullOrWhiteSpace(schema))
        {
            parameters.Add(new("@schemaDotName", SqlDbType.NVarChar, 4000) { Value = $"%{schema}.{name}%" });
            parameters.Add(new("@bracketSchemaDotName", SqlDbType.NVarChar, 4000) { Value = $"%[{schema}].[{name}]%" });
            schemaPredicate = "OR M.definition LIKE @schemaDotName OR M.definition LIKE @bracketSchemaDotName";
        }

        var typePredicates = BuildInPredicate("O.type", "type", typeCodes, parameters);
        var sql = $"""
                   SELECT TOP (@limit)
                       schema_name=S.name,
                       object_name=O.name,
                       object_type=O.type,
                       object_type_desc=O.type_desc,
                       definition=M.definition,
                       modify_date=O.modify_date
                   FROM sys.sql_modules M
                   INNER JOIN sys.objects O ON O.object_id=M.object_id
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   WHERE O.is_ms_shipped=0
                       AND {typePredicates}
                       AND (
                           M.definition LIKE @name
                           OR M.definition LIKE @bracketName
                           {schemaPredicate}
                       )
                   ORDER BY O.modify_date DESC, S.name, O.name;
                   """;

        var rows = await QueryAsync(
            sql,
            parameters,
            reader =>
            {
                var definition = reader.GetNullableString("definition") ?? string.Empty;
                return new
                {
                    schema = reader.GetString("schema_name"),
                    name = reader.GetString("object_name"),
                    type = ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                    typeDesc = reader.GetString("object_type_desc"),
                    matchedSnippet = BuildSnippet(definition, name),
                    modifyDate = reader.GetDateTime("modify_date")
                };
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> GetColumnsAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               column_id=C.column_id,
                               column_name=C.name,
                               data_type=T.name,
                               max_length=C.max_length,
                               precision=C.precision,
                               scale=C.scale,
                               is_nullable=C.is_nullable,
                               is_identity=CONVERT(bit, CASE WHEN IC.column_id IS NULL THEN 0 ELSE 1 END),
                               is_computed=C.is_computed,
                               computed_definition=CC.definition,
                               default_constraint_name=DC.name,
                               default_definition=DC.definition,
                               description=CONVERT(NVARCHAR(4000), EP.value)
                           FROM sys.columns C
                           INNER JOIN sys.types T ON T.user_type_id=C.user_type_id
                           LEFT JOIN sys.identity_columns IC ON IC.object_id=C.object_id
                               AND IC.column_id=C.column_id
                           LEFT JOIN sys.computed_columns CC ON CC.object_id=C.object_id
                               AND CC.column_id=C.column_id
                           LEFT JOIN sys.default_constraints DC ON DC.parent_object_id=C.object_id
                               AND DC.parent_column_id=C.column_id
                           LEFT JOIN sys.extended_properties EP ON EP.class=1
                               AND EP.major_id=C.object_id
                               AND EP.minor_id=C.column_id
                               AND EP.name=N'MS_Description'
                           WHERE C.object_id=@objectId
                           ORDER BY C.column_id;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                ordinal = reader.GetInt32("column_id"),
                name = reader.GetString("column_name"),
                dataType = reader.GetString("data_type"),
                maxLength = NormalizeMaxLength(reader.GetInt16("max_length"), reader.GetString("data_type")),
                precision = reader.GetByte("precision"),
                scale = reader.GetByte("scale"),
                nullable = reader.GetBoolean("is_nullable"),
                identity = reader.GetBoolean("is_identity"),
                computed = reader.GetBoolean("is_computed"),
                computedDefinition = reader.GetNullableString("computed_definition"),
                defaultConstraintName = reader.GetNullableString("default_constraint_name"),
                defaultDefinition = reader.GetNullableString("default_definition"),
                description = reader.GetNullableString("description")
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> GetIndexesCoreAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               index_id=I.index_id,
                               index_name=I.name,
                               type_desc=I.type_desc,
                               is_unique=I.is_unique,
                               is_primary_key=I.is_primary_key,
                               has_filter=I.has_filter,
                               filter_definition=I.filter_definition,
                               key_ordinal=IC.key_ordinal,
                               index_column_id=IC.index_column_id,
                               is_included_column=IC.is_included_column,
                               is_descending_key=IC.is_descending_key,
                               column_name=C.name
                           FROM sys.indexes I
                           LEFT JOIN sys.index_columns IC ON IC.object_id=I.object_id
                               AND IC.index_id=I.index_id
                           LEFT JOIN sys.columns C ON C.object_id=IC.object_id
                               AND C.column_id=IC.column_id
                           WHERE I.object_id=@objectId
                               AND I.index_id>0
                           ORDER BY I.index_id, IC.key_ordinal, IC.index_column_id;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new IndexRow(
                reader.GetInt32("index_id"),
                reader.GetNullableString("index_name"),
                reader.GetString("type_desc"),
                reader.GetBoolean("is_unique"),
                reader.GetBoolean("is_primary_key"),
                reader.GetBoolean("has_filter"),
                reader.GetNullableString("filter_definition"),
                reader.GetByte("key_ordinal"),
                reader.GetInt32("index_column_id"),
                reader.GetBoolean("is_included_column"),
                reader.GetBoolean("is_descending_key"),
                reader.GetNullableString("column_name")),
            cancellationToken);

        return rows
            .GroupBy(row => row.IndexId)
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    name = first.IndexName,
                    type = first.TypeDesc,
                    isUnique = first.IsUnique,
                    isPrimaryKey = first.IsPrimaryKey,
                    keyColumns = group
                        .Where(row => !row.IsIncludedColumn && row.KeyOrdinal > 0 && row.ColumnName is not null)
                        .OrderBy(row => row.KeyOrdinal)
                        .Select(row => new { name = row.ColumnName, descending = row.IsDescendingKey })
                        .ToArray(),
                    includedColumns = group
                        .Where(row => row.IsIncludedColumn && row.ColumnName is not null)
                        .OrderBy(row => row.IndexColumnId)
                        .Select(row => row.ColumnName)
                        .ToArray(),
                    filterDefinition = first.HasFilter ? first.FilterDefinition : null
                };
            })
            .Cast<object>()
            .ToArray();
    }

    private async Task<object> GetConstraintsCoreAsync(int objectId, CancellationToken cancellationToken)
    {
        var keyConstraints = await GetKeyConstraintsAsync(objectId, cancellationToken);
        var defaultConstraints = await GetDefaultConstraintsAsync(objectId, cancellationToken);
        var checkConstraints = await GetCheckConstraintsAsync(objectId, cancellationToken);

        return new
        {
            keyConstraints,
            defaultConstraints,
            checkConstraints
        };
    }

    private async Task<object[]> GetKeyConstraintsAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               constraint_name=KC.name,
                               constraint_type=KC.type,
                               constraint_type_desc=KC.type_desc,
                               column_name=C.name,
                               key_ordinal=IC.key_ordinal,
                               is_descending_key=IC.is_descending_key
                           FROM sys.key_constraints KC
                           INNER JOIN sys.index_columns IC ON IC.object_id=KC.parent_object_id
                               AND IC.index_id=KC.unique_index_id
                               AND IC.key_ordinal>0
                           INNER JOIN sys.columns C ON C.object_id=IC.object_id
                               AND C.column_id=IC.column_id
                           WHERE KC.parent_object_id=@objectId
                           ORDER BY KC.name, IC.key_ordinal;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                Name = reader.GetString("constraint_name"),
                Type = reader.GetString("constraint_type"),
                TypeDesc = reader.GetString("constraint_type_desc"),
                ColumnName = reader.GetString("column_name"),
                KeyOrdinal = reader.GetByte("key_ordinal"),
                Descending = reader.GetBoolean("is_descending_key")
            },
            cancellationToken);

        return rows
            .GroupBy(row => row.Name)
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    name = first.Name,
                    type = first.Type,
                    typeDesc = first.TypeDesc,
                    columns = group.OrderBy(row => row.KeyOrdinal)
                        .Select(row => new { name = row.ColumnName, descending = row.Descending })
                        .ToArray()
                };
            })
            .Cast<object>()
            .ToArray();
    }

    private async Task<object[]> GetDefaultConstraintsAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               constraint_name=DC.name,
                               column_name=C.name,
                               definition=DC.definition
                           FROM sys.default_constraints DC
                           INNER JOIN sys.columns C ON C.object_id=DC.parent_object_id
                               AND C.column_id=DC.parent_column_id
                           WHERE DC.parent_object_id=@objectId
                           ORDER BY C.column_id;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                name = reader.GetString("constraint_name"),
                column = reader.GetString("column_name"),
                definition = reader.GetNullableString("definition")
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> GetCheckConstraintsAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               constraint_name=CC.name,
                               definition=CC.definition,
                               is_disabled=CC.is_disabled,
                               is_not_trusted=CC.is_not_trusted
                           FROM sys.check_constraints CC
                           WHERE CC.parent_object_id=@objectId
                           ORDER BY CC.name;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                name = reader.GetString("constraint_name"),
                definition = reader.GetNullableString("definition"),
                isDisabled = reader.GetBoolean("is_disabled"),
                isNotTrusted = reader.GetBoolean("is_not_trusted")
            },
            cancellationToken);

        return rows.Cast<object>().ToArray();
    }

    private async Task<object[]> GetForeignKeysCoreAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               foreign_key_name=FK.name,
                               parent_schema=PS.name,
                               parent_table=PT.name,
                               parent_column=PC.name,
                               referenced_schema=RS.name,
                               referenced_table=RT.name,
                               referenced_column=RC.name,
                               constraint_column_id=FKC.constraint_column_id,
                               delete_action=FK.delete_referential_action_desc,
                               update_action=FK.update_referential_action_desc,
                               is_disabled=FK.is_disabled,
                               is_not_trusted=FK.is_not_trusted,
                               direction=CASE WHEN FK.parent_object_id=@objectId THEN N'outgoing' ELSE N'incoming' END
                           FROM sys.foreign_keys FK
                           INNER JOIN sys.foreign_key_columns FKC ON FKC.constraint_object_id=FK.object_id
                           INNER JOIN sys.tables PT ON PT.object_id=FK.parent_object_id
                           INNER JOIN sys.schemas PS ON PS.schema_id=PT.schema_id
                           INNER JOIN sys.columns PC ON PC.object_id=FKC.parent_object_id
                               AND PC.column_id=FKC.parent_column_id
                           INNER JOIN sys.tables RT ON RT.object_id=FK.referenced_object_id
                           INNER JOIN sys.schemas RS ON RS.schema_id=RT.schema_id
                           INNER JOIN sys.columns RC ON RC.object_id=FKC.referenced_object_id
                               AND RC.column_id=FKC.referenced_column_id
                           WHERE FK.parent_object_id=@objectId
                               OR FK.referenced_object_id=@objectId
                           ORDER BY FK.name, FKC.constraint_column_id;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new ForeignKeyRow(
                reader.GetString("foreign_key_name"),
                reader.GetString("parent_schema"),
                reader.GetString("parent_table"),
                reader.GetString("parent_column"),
                reader.GetString("referenced_schema"),
                reader.GetString("referenced_table"),
                reader.GetString("referenced_column"),
                reader.GetInt32("constraint_column_id"),
                reader.GetString("delete_action"),
                reader.GetString("update_action"),
                reader.GetBoolean("is_disabled"),
                reader.GetBoolean("is_not_trusted"),
                reader.GetString("direction")),
            cancellationToken);

        return rows
            .GroupBy(row => row.Name)
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    name = first.Name,
                    direction = first.Direction,
                    parentTable = new { schema = first.ParentSchema, name = first.ParentTable },
                    parentColumns = group.OrderBy(row => row.Ordinal).Select(row => row.ParentColumn).ToArray(),
                    referencedTable = new { schema = first.ReferencedSchema, name = first.ReferencedTable },
                    referencedColumns = group.OrderBy(row => row.Ordinal).Select(row => row.ReferencedColumn).ToArray(),
                    deleteAction = first.DeleteAction,
                    updateAction = first.UpdateAction,
                    isDisabled = first.IsDisabled,
                    isNotTrusted = first.IsNotTrusted
                };
            })
            .Cast<object>()
            .ToArray();
    }

    private async Task<List<T>> QueryAsync<T>(
        string sql,
        IEnumerable<SqlParameter> parameters,
        Func<SqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = CreateCommand(connection, sql);
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        catch (SqlMcpException)
        {
            throw;
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            throw new SqlMcpException(ErrorCodes.SqlTimeout, "SQL command timed out.", ex.Message, null, ex);
        }
        catch (SqlException ex) when (ex.Number == 1222)
        {
            throw new SqlMcpException(ErrorCodes.SqlLockTimeout, "SQL lock timeout.", ex.Message, null, ex);
        }
        catch (SqlException ex)
        {
            throw new SqlMcpException(ErrorCodes.UnknownError, "SQL command failed.", ex.Message, null, ex);
        }
    }

    private async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql)
    {
        return new SqlCommand(sql, connection)
        {
            CommandTimeout = _options.Limits.CommandTimeoutSeconds
        };
    }

    private object? ReadValue(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var dataType = reader.GetDataTypeName(ordinal);
        if (dataType.Contains("binary", StringComparison.OrdinalIgnoreCase)
            || dataType.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            var length = reader.GetBytes(ordinal, 0, null, 0, 0);
            return $"<binary length={length}>";
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => TruncateText(text),
            DateTime dateTime => dateTime.ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O"),
            TimeSpan timeSpan => timeSpan.ToString(),
            Guid guid => guid.ToString(),
            _ => value
        };
    }

    private string TruncateText(string text)
    {
        return text.Length <= _options.Limits.MaxTextLength
            ? text
            : string.Concat(text.AsSpan(0, _options.Limits.MaxTextLength), "...<truncated>");
    }

    private static long EstimateBytes(object? value)
    {
        if (value is null)
        {
            return 4;
        }

        return Encoding.UTF8.GetByteCount(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static IReadOnlyList<string> SplitKeyword(string keyword)
    {
        return keyword
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private static string BuildLikeAny(string expression, int termCount)
    {
        return string.Join(" OR ", Enumerable.Range(0, termCount).Select(i => $"{expression} LIKE @term{i}"));
    }

    private static string BuildInPredicate(
        string expression,
        string parameterPrefix,
        IReadOnlyList<string> values,
        List<SqlParameter> parameters)
    {
        if (values.Count == 0)
        {
            return "1=0";
        }

        var parameterNames = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            var parameterName = $"@{parameterPrefix}{i}";
            parameterNames.Add(parameterName);
            parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 2) { Value = values[i] });
        }

        return $"{expression} IN ({string.Join(", ", parameterNames)})";
    }

    private static int? NormalizeMaxLength(short maxLength, string dataType)
    {
        if (maxLength < 0)
        {
            return -1;
        }

        return dataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
               || dataType.Equals("nchar", StringComparison.OrdinalIgnoreCase)
               || dataType.Equals("sysname", StringComparison.OrdinalIgnoreCase)
            ? maxLength / 2
            : maxLength;
    }

    private static string BuildSnippet(string definition, string keyword)
    {
        if (string.IsNullOrEmpty(definition))
        {
            return string.Empty;
        }

        var compact = string.Join(" ", definition.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var index = compact.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return compact.Length <= 240 ? compact : string.Concat(compact.AsSpan(0, 240), "...");
        }

        var start = Math.Max(0, index - 100);
        var length = Math.Min(240, compact.Length - start);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < compact.Length ? "..." : string.Empty;
        return string.Concat(prefix, compact.AsSpan(start, length), suffix);
    }

    private sealed record DbObjectInfo(
        int ObjectId,
        string Schema,
        string Name,
        string Type,
        string TypeDesc,
        DateTime CreateDate,
        DateTime ModifyDate,
        string? Description);

    private sealed record StructureObjectResolution(
        DbObjectInfo Object,
        string RequestedSchema,
        string RequestedName,
        string? MappedName,
        bool ResolvedFromPrefix,
        bool UsedFallback);

    private sealed record ObjectMatchRow(
        int ObjectId,
        string SchemaName,
        string ObjectName,
        string ObjectType,
        string ObjectTypeDesc,
        string? Description,
        string? ColumnName,
        string? ColumnDescription,
        bool MatchedObjectName,
        bool MatchedSchemaName,
        bool MatchedDescription,
        bool MatchedColumnName,
        bool MatchedColumnDescription);

    private sealed record IndexRow(
        int IndexId,
        string? IndexName,
        string TypeDesc,
        bool IsUnique,
        bool IsPrimaryKey,
        bool HasFilter,
        string? FilterDefinition,
        byte KeyOrdinal,
        int IndexColumnId,
        bool IsIncludedColumn,
        bool IsDescendingKey,
        string? ColumnName);

    private sealed record ForeignKeyRow(
        string Name,
        string ParentSchema,
        string ParentTable,
        string ParentColumn,
        string ReferencedSchema,
        string ReferencedTable,
        string ReferencedColumn,
        int Ordinal,
        string DeleteAction,
        string UpdateAction,
        bool IsDisabled,
        bool IsNotTrusted,
        string Direction);
}

internal static class SqlDataReaderExtensions
{
    public static string GetString(this SqlDataReader reader, string name)
    {
        return reader.GetString(reader.GetOrdinal(name));
    }

    public static string? GetNullableString(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));
    }

    public static int GetInt32(this SqlDataReader reader, string name)
    {
        return reader.GetInt32(reader.GetOrdinal(name));
    }

    public static int? GetNullableInt32(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static short GetInt16(this SqlDataReader reader, string name)
    {
        return reader.GetInt16(reader.GetOrdinal(name));
    }

    public static byte GetByte(this SqlDataReader reader, string name)
    {
        return reader.GetByte(reader.GetOrdinal(name));
    }

    public static bool GetBoolean(this SqlDataReader reader, string name)
    {
        return reader.GetBoolean(reader.GetOrdinal(name));
    }

    public static bool GetBooleanFromInt(this SqlDataReader reader, string name)
    {
        return Convert.ToInt32(reader.GetValue(reader.GetOrdinal(name)), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public static DateTime GetDateTime(this SqlDataReader reader, string name)
    {
        return reader.GetDateTime(reader.GetOrdinal(name));
    }
}

internal static class ObjectTypeMapper
{
    private static readonly Dictionary<string, string[]> PublicToSqlType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["table"] = ["U"],
        ["view"] = ["V"],
        ["procedure"] = ["P", "PC"],
        ["function"] = ["FN", "IF", "TF", "FS", "FT"],
        ["trigger"] = ["TR"]
    };

    public static string[] MapObjectTypes(string[]? objectTypes)
    {
        return MapTypes(objectTypes, includeTriggers: false);
    }

    public static string[] MapModuleTypes(string[]? objectTypes)
    {
        return MapTypes(objectTypes, includeTriggers: true)
            .Where(type => type is "V" or "P" or "PC" or "FN" or "IF" or "TF" or "FS" or "FT" or "TR")
            .ToArray();
    }

    public static string ToPublicType(string sqlType)
    {
        var normalized = sqlType.Trim();
        return normalized switch
        {
            "U" => "table",
            "V" => "view",
            "P" or "PC" => "procedure",
            "FN" or "IF" or "TF" or "FS" or "FT" => "function",
            "TR" => "trigger",
            _ => normalized
        };
    }

    private static string[] MapTypes(string[]? objectTypes, bool includeTriggers)
    {
        if (objectTypes is null || objectTypes.Length == 0)
        {
            return PublicToSqlType
                .Where(pair => includeTriggers || !pair.Key.Equals("trigger", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return objectTypes
            .Where(type => PublicToSqlType.ContainsKey(type))
            .SelectMany(type => PublicToSqlType[type])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
