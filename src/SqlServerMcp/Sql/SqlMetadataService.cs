using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Sql;

public sealed class SqlMetadataService
{
    private const int DefaultModuleContextLines = 3;
    private const int MaxModuleContextLines = 50;
    private const long MaxCompareFileBytes = 10 * 1024 * 1024;
    private const int DefaultRepoCompareCandidateLimit = 10;
    private const int MaxRepoCompareCandidateLimit = 50;
    private const int MaxRepoCompareFilesScanned = 20_000;
    private const int MaxDiffSyncLookahead = 200;
    private const int MaxDiffHunks = 8;
    private const int MaxDiffLinesPerSide = 120;
    private static readonly Regex TempTableNameRegex = new(@"(?<![#\w])#[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex SqlBatchSeparatorRegex = new(@"^\s*GO(?:\s+\d+)?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlModuleCreateRegex = new(
        @"^\s*(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlSessionSetRegex = new(
        @"^\s*SET\s+(?:ANSI_NULLS|QUOTED_IDENTIFIER)\s+(?:ON|OFF)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] DefaultRepoComparePatterns = ["**/*.sql"];
    private static readonly string[] ModuleSearchNextActions =
    [
        "Use get_module_definition with keyword/startLine for line-numbered slices.",
        "Use compare_module_to_file when you already know the local SQL file path.",
        "Use compare_module_to_repo to auto-discover a matching repository .sql file and compare it with the database module."
    ];
    private static readonly string[] ModuleDefinitionNextActions =
    [
        "Use compare_module_to_file to confirm whether a known local SQL file has been executed to the database.",
        "Use compare_module_to_repo to auto-discover a matching repository .sql file and compare it with this database module."
    ];
    private static readonly string[] ModuleCompareNextActions =
    [
        "If the result differs, deploy the local SQL file to the target database or inspect the diff before changing the database.",
        "If the file path was guessed, use compare_module_to_repo with a narrower root or patterns to confirm the intended source file."
    ];
    private static readonly HashSet<string> RepoCompareSkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        ".hg",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "packages"
    };
    private static readonly string[] UsableColumnCandidates =
    [
        "usable",
        "is_usable",
        "enabled",
        "is_enabled",
        "enable",
        "is_enable",
        "active",
        "is_active"
    ];

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
        object? textSearchValidation = null;
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

            try
            {
                textSearchValidation = await ValidateTextSearchTargetsAsync(cancellationToken);
            }
            catch (SqlMcpException ex)
            {
                textSearchValidation = new
                {
                    ok = false,
                    errorCode = ex.ErrorCode,
                    message = ex.Message,
                    detail = ex.Detail
                };
            }
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
            textSearch = new
            {
                snippetLength = _options.TextSearch.SnippetLength,
                targetCount = _options.TextSearch.Targets.Length,
                enabledTargetCount = _options.TextSearch.Targets.Count(target => target.Enabled),
                profiles = _options.TextSearch.Targets
                    .Where(target => target.Enabled)
                    .Select(target => target.Profile)
                    .Where(profile => !string.IsNullOrWhiteSpace(profile))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(profile => profile)
                    .ToArray(),
                validation = textSearchValidation
            },
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
        var rowLimit = Math.Min((effectiveLimit + 1) * 20, 5000);
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

        var distinctObjectCount = rows.Select(row => row.ObjectId).Distinct().Count();
        var truncated = distinctObjectCount > effectiveLimit || rows.Count >= rowLimit;
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

        var hint = truncated
            ? "Narrow keyword terms, pass objectTypes, or increase limit within the configured server cap."
            : null;

        return new
        {
            items,
            count = items.Length,
            limit = effectiveLimit,
            truncated,
            resultInfo = BuildResultInfo(items.Length, effectiveLimit, truncated, hint, truncated ? "limit" : null),
            hint
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
            triggers = isTableOrView
                ? await GetTriggersCoreAsync(dbObject.ObjectId, cancellationToken)
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
        var queryLimit = effectiveLimit + 1;
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
                new("@limit", SqlDbType.Int) { Value = queryLimit },
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

        var items = rows.Take(effectiveLimit).ToArray();
        var truncated = rows.Count > effectiveLimit;
        var hint = truncated
            ? "Use exact=true for a specific column, add a narrower column search text, or increase limit within the configured cap."
            : null;

        return new
        {
            items,
            count = items.Length,
            limit = effectiveLimit,
            truncated,
            resultInfo = BuildResultInfo(items.Length, effectiveLimit, truncated, hint, truncated ? "limit" : null),
            hint
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
            new("@limit", SqlDbType.Int) { Value = effectiveLimit + 1 }
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

        var items = rows.Take(effectiveLimit).ToArray();
        var truncated = rows.Count > effectiveLimit;

        var hint = truncated
            ? "Use objectTypes or get_module_definition with keyword/startLine to inspect a narrower module slice."
            : null;

        return new
        {
            items,
            count = items.Length,
            limit = effectiveLimit,
            truncated,
            resultInfo = BuildResultInfo(items.Length, effectiveLimit, truncated, hint, truncated ? "limit" : null),
            hint,
            nextActions = ModuleSearchNextActions
        };
    }

    public async Task<object> GetModuleDefinitionAsync(
        string schema,
        string name,
        string? keyword,
        int? startLine,
        int? endLine,
        int? contextLines,
        bool includeLineNumbers,
        CancellationToken cancellationToken)
    {
        var module = await GetModuleDefinitionCoreAsync(schema, name, cancellationToken);
        var definition = module.Definition;
        var slice = BuildModuleDefinitionSlice(
            definition,
            keyword,
            startLine,
            endLine,
            contextLines,
            _options.Limits.MaxRows);

        var hint = slice.Truncated
            ? "Use a narrower keyword, smaller line range, or lower contextLines."
            : null;

        return new
        {
            schema = module.Schema,
            name = module.Name,
            type = module.Type,
            typeDesc = module.TypeDesc,
            createDate = module.CreateDate,
            modifyDate = module.ModifyDate,
            definition = slice.Definition,
            definitionLength = definition.Length,
            definitionSha256 = ComputeSha256Hex(definition),
            lineCount = slice.TotalLines,
            selection = new
            {
                slice.Reason,
                keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                slice.ContextLines,
                slice.StartLine,
                slice.EndLine,
                slice.SelectedLineCount,
                slice.IsPartial,
                slice.Truncated,
                slice.MatchedLines
            },
            resultInfo = BuildResultInfo(
                slice.SelectedLineCount,
                _options.Limits.MaxRows,
                slice.Truncated,
                hint,
                slice.Reason,
                new
                {
                    totalLines = slice.TotalLines,
                    slice.IsPartial,
                    slice.StartLine,
                    slice.EndLine,
                    matchedLineCount = slice.MatchedLines.Length
                }),
            hint,
            nextActions = ModuleDefinitionNextActions,
            lines = includeLineNumbers || slice.IsPartial
                ? slice.Lines
                : null
        };
    }

    public async Task<object> CompareModuleToFileAsync(
        string schema,
        string name,
        string filePath,
        int? contextLines,
        CancellationToken cancellationToken)
    {
        var module = await GetModuleDefinitionCoreAsync(schema, name, cancellationToken);
        var file = GetReadableCompareFile(filePath);
        return await BuildModuleFileComparisonAsync(module, file, contextLines, cancellationToken);
    }

    public async Task<object> CompareModuleToRepoAsync(
        string schema,
        string name,
        string? root,
        string[]? patterns,
        int? maxCandidates,
        int? contextLines,
        CancellationToken cancellationToken)
    {
        var module = await GetModuleDefinitionCoreAsync(schema, name, cancellationToken);
        var discovery = FindModuleSqlFileDiscovery(root, module.Schema, module.Name, patterns, maxCandidates);
        if (discovery.SelectedCandidate is null)
        {
            return new
            {
                schema = module.Schema,
                name = module.Name,
                type = module.Type,
                typeDesc = module.TypeDesc,
                status = discovery.Ambiguous ? "ambiguous" : "no_candidate",
                compared = false,
                candidateCount = discovery.CandidateCount,
                discovery,
                nextActions = new[]
                {
                    "Pass filePath to compare_module_to_file when you know the exact SQL file.",
                    "Pass root or patterns to compare_module_to_repo to narrow repository file discovery."
                }
            };
        }

        var selectedFile = GetReadableCompareFile(discovery.SelectedCandidate.Path);
        var comparison = await BuildModuleFileComparisonAsync(module, selectedFile, contextLines, cancellationToken);

        return new
        {
            schema = module.Schema,
            name = module.Name,
            type = module.Type,
            typeDesc = module.TypeDesc,
            status = "compared",
            compared = true,
            candidateCount = discovery.CandidateCount,
            discovery,
            selectedFile = discovery.SelectedCandidate,
            comparison,
            exactMatch = comparison.ExactMatch,
            normalizedMatch = comparison.NormalizedMatch,
            nextActions = ModuleCompareNextActions
        };
    }

    public async Task<object> AnalyzeModuleTempTablesAsync(
        string schema,
        string name,
        CancellationToken cancellationToken)
    {
        var module = await GetModuleDefinitionCoreAsync(schema, name, cancellationToken);
        var analysis = AnalyzeTempTables(module.Definition);

        return new
        {
            schema = module.Schema,
            name = module.Name,
            type = module.Type,
            typeDesc = module.TypeDesc,
            module.ModifyDate,
            lineCount = SplitDefinitionLines(module.Definition).Length,
            tempTableCount = analysis.TempTables.Length,
            analysis.TempTables
        };
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
        var columnMatchesRaw = await FindUsageColumnMatchesAsync(name, schema, effectiveLimit + 1, cancellationToken);
        var moduleMatchesRaw = await FindUsageModuleMatchesAsync(name, schema, objectTypes, effectiveLimit + 1, cancellationToken);
        var columnMatches = columnMatchesRaw.Take(effectiveLimit).ToArray();
        var moduleMatches = moduleMatchesRaw.Take(effectiveLimit).ToArray();
        var columnMatchesTruncated = columnMatchesRaw.Length > effectiveLimit;
        var moduleMatchesTruncated = moduleMatchesRaw.Length > effectiveLimit;

        var truncated = columnMatchesTruncated || moduleMatchesTruncated;
        var sections = new
        {
            columnMatches = new
            {
                count = columnMatches.Length,
                limit = effectiveLimit,
                truncated = columnMatchesTruncated
            },
            moduleMatches = new
            {
                count = moduleMatches.Length,
                limit = effectiveLimit,
                truncated = moduleMatchesTruncated
            }
        };
        var hint = truncated
            ? "Pass schema/objectTypes, use a more specific token, or inspect modules with get_module_definition keyword slices."
            : null;

        return new
        {
            name,
            schema,
            columnMatches,
            moduleMatches,
            columnMatchCount = columnMatches.Length,
            moduleMatchCount = moduleMatches.Length,
            limit = effectiveLimit,
            truncated,
            sections,
            resultInfo = BuildResultInfo(
                columnMatches.Length + moduleMatches.Length,
                effectiveLimit * 2,
                truncated,
                hint,
                truncated ? "limit" : null,
                sections),
            hint
        };
    }

    public async Task<object> SearchConfigTextAsync(
        string keyword,
        string? profile,
        int? limit,
        bool includeTargets,
        bool usableOnly,
        CancellationToken cancellationToken)
    {
        var terms = SplitKeyword(keyword);
        if (terms.Count == 0)
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "keyword is required.");
        }

        var targets = _options.TextSearch.Targets
            .Where(target => target.Enabled)
            .Where(target => string.IsNullOrWhiteSpace(profile)
                || string.Equals(target.Profile, profile, StringComparison.OrdinalIgnoreCase))
            .Where(IsValidTextSearchTarget)
            .ToArray();

        if (targets.Length == 0)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "No text search targets are configured for search_config_text.",
                string.IsNullOrWhiteSpace(profile) ? null : $"profile={profile}",
                "Add allow-listed textSearch.targets to sqlserver_mcp.json.");
        }

        var effectiveLimit = _options.Limits.ClampRows(limit);
        var columnLookup = await LoadTextSearchTargetColumnsAsync(targets, cancellationToken);
        var items = new List<object>();
        var truncated = false;

        foreach (var target in targets)
        {
            var remaining = effectiveLimit - items.Count;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var usableColumn = FindUsableColumn(target, columnLookup);
            var targetRows = await SearchConfigTextTargetAsync(
                target,
                terms,
                remaining + 1,
                usableColumn,
                usableOnly,
                cancellationToken);
            if (targetRows.Count > remaining)
            {
                truncated = true;
                items.AddRange(targetRows.Take(remaining));
                break;
            }

            items.AddRange(targetRows);
        }

        var hint = truncated
            ? "Use profile, a narrower keyword, or increase limit within the configured server cap."
            : null;

        return new
        {
            keyword,
            profile,
            includeTargets,
            usableOnly,
            searchedTargetCount = targets.Length,
            usableAwareTargetCount = targets.Count(target => FindUsableColumn(target, columnLookup) is not null),
            searchedTargets = includeTargets
                ? targets.Select(target => BuildTextSearchTargetSummary(target, FindUsableColumn(target, columnLookup))).ToArray()
                : null,
            items,
            count = items.Count,
            truncated,
            limit = effectiveLimit,
            resultInfo = BuildResultInfo(items.Count, effectiveLimit, truncated, hint, truncated ? "limit" : null),
            hint
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
                summary = SummarizeShowplanXml(plans),
                showplanXml = plans,
                elapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, "SET SHOWPLAN_XML OFF;", CancellationToken.None);
        }
    }

    public async Task<object> DescribeQueryResultAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyDictionary<string, object?>? templateValues,
        CancellationToken cancellationToken)
    {
        var template = ApplySqlTemplateValues(sql, templateValues);
        _sqlGuard.ValidateReadonlyQuery(template.Sql);

        var parameterSpecs = BuildUserSqlParameters(parameters);
        const string metadataSql = """
                                   SELECT
                                       column_ordinal,
                                       name,
                                       is_nullable,
                                       system_type_name,
                                       system_type_id,
                                       max_length,
                                       precision,
                                       scale,
                                       collation_name,
                                       error_number,
                                       error_severity,
                                       error_state,
                                       error_message
                                   FROM sys.dm_exec_describe_first_result_set(@tsql, @params, 0)
                                   ORDER BY CASE WHEN column_ordinal IS NULL THEN 2147483647 ELSE column_ordinal END;
                                   """;

        var rows = await QueryAsync(
            metadataSql,
            [
                new("@tsql", SqlDbType.NVarChar, -1) { Value = template.Sql },
                new("@params", SqlDbType.NVarChar, -1)
                {
                    Value = parameterSpecs.Count == 0
                        ? DBNull.Value
                        : BuildParameterDefinitionList(parameterSpecs)
                }
            ],
            reader => new QueryResultColumnMetadata(
                reader.GetNullableInt32("column_ordinal"),
                reader.GetNullableString("name"),
                reader.GetNullableInt32("is_nullable"),
                reader.GetNullableString("system_type_name"),
                reader.GetNullableInt32("system_type_id"),
                reader.GetNullableInt32("max_length"),
                reader.GetNullableInt32("precision"),
                reader.GetNullableInt32("scale"),
                reader.GetNullableString("collation_name"),
                reader.GetNullableInt32("error_number"),
                reader.GetNullableInt32("error_severity"),
                reader.GetNullableInt32("error_state"),
                reader.GetNullableString("error_message")),
            cancellationToken);

        var error = rows.FirstOrDefault(row => row.ErrorNumber is not null);

        return new
        {
            ok = error is null,
            parameters = parameterSpecs.Select(ToParameterSummary).ToArray(),
            template = template.Replacements.Length == 0
                ? null
                : new
                {
                    applied = template.Replacements.Any(replacement => replacement.Applied),
                    replacements = template.Replacements.Select(replacement => new
                    {
                        replacement.Placeholder,
                        replacement.Applied,
                        replacement.Occurrences,
                        replacement.ValueLength
                    }).ToArray()
                },
            columns = rows
                .Where(row => row.ColumnOrdinal is not null)
                .Select(row => new
                {
                    ordinal = row.ColumnOrdinal,
                    row.Name,
                    nullable = row.IsNullable == 1,
                    systemTypeName = row.SystemTypeName,
                    row.SystemTypeId,
                    row.MaxLength,
                    row.Precision,
                    row.Scale,
                    row.CollationName
                })
                .ToArray(),
            columnCount = rows.Count(row => row.ColumnOrdinal is not null),
            error = error is null
                ? null
                : new
                {
                    errorNumber = error.ErrorNumber,
                    errorSeverity = error.ErrorSeverity,
                    errorState = error.ErrorState,
                    errorMessage = error.ErrorMessage
                }
        };
    }

    public async Task<object> RunReadonlyQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        _sqlGuard.ValidateReadonlyQuery(sql);

        var parameterSpecs = BuildUserSqlParameters(parameters);
        var effectiveMaxRows = _options.Limits.ClampRows(maxRows);
        var resultLimitBytes = _options.Limits.MaxResultMb * 1024L * 1024L;
        var stopwatch = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, object?>>();
        var columns = new List<object>();
        var truncation = new QueryValueTruncationInfo();
        long estimatedBytes = 0;
        var rowLimitTruncated = false;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(
            connection,
            $"SET LOCK_TIMEOUT {_options.Limits.LockTimeoutMs};\n{sql}");
        command.CommandTimeout = _options.Limits.CommandTimeoutSeconds;
        foreach (var parameter in parameterSpecs.Select(CreateSqlParameter))
        {
            command.Parameters.Add(parameter);
        }

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
                rowLimitTruncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = ReadValue(reader, i, truncation);
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
        var queryTruncated = rowLimitTruncated || truncation.TextValuesTruncated > 0;
        var truncationReasons = new List<string>();
        if (rowLimitTruncated)
        {
            truncationReasons.Add("maxRows");
        }

        if (truncation.TextValuesTruncated > 0)
        {
            truncationReasons.Add("maxTextLength");
        }

        var hint = rowLimitTruncated
            ? "Add WHERE filters, select fewer rows, or raise maxRows within the configured server cap."
            : truncation.TextValuesTruncated > 0
                ? "Select shorter expressions, use SUBSTRING in SQL, or raise limits.maxTextLength in config."
                : null;

        var columnsWithTruncatedText = truncation.ColumnsWithTruncatedText.OrderBy(column => column).ToArray();

        return new
        {
            columns,
            rows,
            rowCount = rows.Count,
            truncated = rowLimitTruncated,
            resultInfo = BuildResultInfo(
                rows.Count,
                effectiveMaxRows,
                queryTruncated,
                hint,
                truncationReasons.Count == 0 ? null : string.Join(",", truncationReasons),
                new
                {
                    rowLimitTruncated,
                    textValuesTruncated = truncation.TextValuesTruncated,
                    columnsWithTruncatedText,
                    maxTextLength = _options.Limits.MaxTextLength,
                    maxResultMb = _options.Limits.MaxResultMb,
                    estimatedBytes
                }),
            truncation = new
            {
                rowLimitTruncated,
                textValuesTruncated = truncation.TextValuesTruncated,
                columnsWithTruncatedText,
                maxRows = effectiveMaxRows,
                maxTextLength = _options.Limits.MaxTextLength,
                maxResultMb = _options.Limits.MaxResultMb,
                estimatedBytes,
                reason = truncationReasons.Count == 0 ? null : string.Join(",", truncationReasons),
                hint
            },
            parameters = parameterSpecs.Select(ToParameterSummary).ToArray(),
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

    private static object BuildResultInfo(
        int returned,
        int? limit,
        bool truncated,
        string? hint,
        string? reason = null,
        object? sections = null)
    {
        return new
        {
            returned,
            limit,
            truncated,
            reason,
            hint,
            sections
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

    private async Task<object[]> GetTriggersCoreAsync(int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT
                               trigger_name=T.name,
                               is_disabled=T.is_disabled,
                               is_instead_of_trigger=T.is_instead_of_trigger,
                               create_date=T.create_date,
                               modify_date=T.modify_date
                           FROM sys.triggers T
                           WHERE T.parent_id=@objectId
                               AND T.is_ms_shipped=0
                           ORDER BY T.name;
                           """;

        var rows = await QueryAsync(
            sql,
            [new("@objectId", SqlDbType.Int) { Value = objectId }],
            reader => new
            {
                name = reader.GetString("trigger_name"),
                isDisabled = reader.GetBoolean("is_disabled"),
                isInsteadOfTrigger = reader.GetBoolean("is_instead_of_trigger"),
                createDate = reader.GetDateTime("create_date"),
                modifyDate = reader.GetDateTime("modify_date")
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

    private async Task<List<object>> SearchConfigTextTargetAsync(
        TextSearchTargetOptions target,
        IReadOnlyList<string> terms,
        int limit,
        string? usableColumn,
        bool usableOnly,
        CancellationToken cancellationToken)
    {
        var textExpression = $"CONVERT(NVARCHAR(MAX), {QuoteIdentifier(target.TextColumn)})";
        var keyExpression = BuildNullableTextColumnExpression(target.KeyColumn);
        var nameExpression = BuildNullableTextColumnExpression(target.NameColumn);
        var createdAtExpression = BuildNullableTextColumnExpression(target.CreatedAtColumn);
        var updatedAtExpression = BuildNullableTextColumnExpression(target.UpdatedAtColumn);
        var createdByExpression = BuildNullableTextColumnExpression(target.CreatedByColumn);
        var updatedByExpression = BuildNullableTextColumnExpression(target.UpdatedByColumn);
        var contentKindExpression = BuildNullableTextColumnExpression(target.ContentKindColumn);
        var labelSelects = target.LabelColumns
            .Select((column, index) => $"label_{index}={BuildNullableTextColumnExpression(column)}")
            .ToArray();
        var searchableColumns = BuildTextSearchQueryColumns(target, textExpression, keyExpression, nameExpression);
        var usableValueExpression = BuildNullableTextColumnExpression(usableColumn);
        var usableStateExpression = BuildUsableStateExpression(usableColumn);

        var parameters = new List<SqlParameter>
        {
            new("@limit", SqlDbType.Int) { Value = limit },
            new("@snippetLength", SqlDbType.Int) { Value = _options.TextSearch.SnippetLength }
        };

        var predicates = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            var parameterName = $"@term{i}";
            parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 4000) { Value = $"%{terms[i]}%" });
            predicates.Add($"({string.Join(" OR ", searchableColumns.Select(column => $"{column.Expression} LIKE {parameterName}"))})");
        }

        var wherePredicate = string.Join(" OR ", predicates);
        if (usableOnly && !string.IsNullOrWhiteSpace(usableColumn))
        {
            wherePredicate = $"({wherePredicate}) AND {usableStateExpression} = 1";
        }

        var qualifiedTable = $"{QuoteIdentifier(target.Schema)}.{QuoteIdentifier(target.Table)}";
        var sql = $"""
                   SET LOCK_TIMEOUT {_options.Limits.LockTimeoutMs};

                   WITH matches AS
                   (
                       SELECT TOP (@limit)
                           key_value={keyExpression},
                           name_value={nameExpression},
                           created_at_value={createdAtExpression},
                           updated_at_value={updatedAtExpression},
                           created_by_value={createdByExpression},
                           updated_by_value={updatedByExpression},
                           content_kind_value={contentKindExpression},
                           usable_value={usableValueExpression},
                           usable_state={usableStateExpression},
                           {BuildOptionalSelectList(labelSelects)}
                           text_value={textExpression}
                       FROM {qualifiedTable}
                       WHERE {wherePredicate}
                       ORDER BY {BuildTextSearchOrderBy(target, usableColumn)}
                   )
                   SELECT
                       key_value,
                       name_value,
                       created_at_value,
                       updated_at_value,
                       created_by_value,
                       updated_by_value,
                       content_kind_value,
                       usable_value,
                       usable_state,
                       {BuildOptionalSelectList(target.LabelColumns.Select((_, index) => $"label_{index}").ToArray())}
                       text_length=LEN(text_value),
                       text_sample=LEFT(text_value, 4000),
                       text_preview=LEFT(text_value, @snippetLength)
                   FROM matches;
                   """;

        var rows = await QueryAsync(
            sql,
            parameters,
            reader => new
            {
                KeyValue = reader.GetNullableString("key_value"),
                NameValue = reader.GetNullableString("name_value"),
                CreatedAt = reader.GetNullableString("created_at_value"),
                UpdatedAt = reader.GetNullableString("updated_at_value"),
                CreatedBy = reader.GetNullableString("created_by_value"),
                UpdatedBy = reader.GetNullableString("updated_by_value"),
                ContentKindValue = reader.GetNullableString("content_kind_value"),
                UsableValue = reader.GetNullableString("usable_value"),
                UsableState = reader.GetNullableInt32("usable_state"),
                LabelValues = BuildTextSearchLabelValues(reader, target.LabelColumns),
                TextLength = reader.GetNullableInt32("text_length"),
                TextSample = reader.GetNullableString("text_sample") ?? string.Empty,
                TextPreview = reader.GetNullableString("text_preview") ?? string.Empty
            },
            cancellationToken);

        return rows
            .Select(row =>
            {
                var match = FindTextSearchMatch(
                    terms,
                    BuildTextSearchMatchInputs(target, row.KeyValue, row.NameValue, row.LabelValues, row.TextSample));
                var matchedSnippet = match is null
                    ? NormalizeSnippet(row.TextPreview)
                    : BuildTextSearchSnippet(match.Value, match.Start, match.Length, _options.TextSearch.SnippetLength);
                var textSnippet = match is null || string.Equals(match.Column, target.TextColumn, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : NormalizeSnippet(row.TextPreview);

                return new
                {
                    source = $"{target.Schema}.{target.Table}.{target.TextColumn}",
                    profile = target.Profile,
                    schema = target.Schema,
                    table = target.Table,
                    textColumn = target.TextColumn,
                    locator = new
                    {
                        keyColumn = NullIfWhiteSpace(target.KeyColumn),
                        keyValue = row.KeyValue,
                        nameColumn = NullIfWhiteSpace(target.NameColumn),
                        nameValue = row.NameValue,
                        labels = BuildTextSearchLabels(row.LabelValues)
                    },
                    audit = new
                    {
                        createdAtColumn = NullIfWhiteSpace(target.CreatedAtColumn),
                        createdAt = row.CreatedAt,
                        updatedAtColumn = NullIfWhiteSpace(target.UpdatedAtColumn),
                        updatedAt = row.UpdatedAt,
                        createdByColumn = NullIfWhiteSpace(target.CreatedByColumn),
                        createdBy = row.CreatedBy,
                        updatedByColumn = NullIfWhiteSpace(target.UpdatedByColumn),
                        updatedBy = row.UpdatedBy
                    },
                    contentKind = BuildContentKindInfo(
                        target.ContentKind,
                        target.ContentKindColumn,
                        row.ContentKindValue,
                        row.TextSample),
                    usable = usableColumn is null
                        ? null
                        : new
                        {
                            column = usableColumn,
                            value = row.UsableValue,
                            isUsable = row.UsableState is null
                                ? (bool?)null
                                : row.UsableState == 1
                        },
                    textLength = row.TextLength,
                    matchColumn = match?.Column,
                    matchKind = match?.Kind,
                    matchedTerm = match?.Term,
                    matchStart = match?.Start,
                    matchLength = match?.Length,
                    matchedSnippet,
                    textSnippet
                };
            })
            .Cast<object>()
            .ToList();
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

    private static SqlParameter CreateSqlParameter(UserSqlParameterSpec spec)
    {
        var parameter = new SqlParameter(spec.Name, spec.DbType)
        {
            Value = spec.Value ?? DBNull.Value
        };

        if (spec.Size is not null)
        {
            parameter.Size = spec.Size.Value;
        }

        if (spec.DbType == SqlDbType.Decimal)
        {
            parameter.Precision = 38;
            parameter.Scale = 10;
        }

        return parameter;
    }

    private async Task<ModuleDefinitionInfo> GetModuleDefinitionCoreAsync(
        string schema,
        string name,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT TOP 1
                               schema_name=S.name,
                               object_name=O.name,
                               object_type=O.type,
                               object_type_desc=O.type_desc,
                               create_date=O.create_date,
                               modify_date=O.modify_date,
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
            reader => new ModuleDefinitionInfo(
                reader.GetString("schema_name"),
                reader.GetString("object_name"),
                ObjectTypeMapper.ToPublicType(reader.GetString("object_type")),
                reader.GetString("object_type_desc"),
                reader.GetDateTime("create_date"),
                reader.GetDateTime("modify_date"),
                reader.GetNullableString("definition") ?? string.Empty),
            cancellationToken);

        var module = rows.SingleOrDefault();
        if (module is null)
        {
            throw new SqlMcpException(ErrorCodes.ObjectNotFound, $"Module '{schema}.{name}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(module.Definition))
        {
            throw new SqlMcpException(
                ErrorCodes.ViewDefinitionPermissionRequired,
                $"Definition for '{schema}.{name}' is not available.",
                "OBJECT_DEFINITION returned NULL.",
                "Grant VIEW DEFINITION to the MCP SQL login, or inspect the module definition from source control.");
        }

        return module;
    }

    private static FileInfo GetReadableCompareFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "filePath is required.");
        }

        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "filePath must be an absolute path.",
                filePath,
                "Pass the full local path to the .sql file.");
        }

        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "Local compare file was not found.", file.FullName);
        }

        if (file.Length > MaxCompareFileBytes)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Local compare file is too large.",
                $"{file.FullName} is {file.Length} bytes; max is {MaxCompareFileBytes} bytes.",
                "Pass a smaller SQL file.");
        }

        return file;
    }

    private static DirectoryInfo GetReadableCompareRoot(string? root)
    {
        var effectiveRoot = string.IsNullOrWhiteSpace(root)
            ? Directory.GetCurrentDirectory()
            : root.Trim();
        var fullPath = Path.GetFullPath(effectiveRoot);
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Repository root was not found.",
                fullPath,
                "Pass an existing local repository or folder path in root.");
        }

        return directory;
    }

    private async Task<ModuleFileComparisonResult> BuildModuleFileComparisonAsync(
        ModuleDefinitionInfo module,
        FileInfo file,
        int? contextLines,
        CancellationToken cancellationToken)
    {
        var fileText = await File.ReadAllTextAsync(file.FullName, Encoding.UTF8, cancellationToken);
        var effectiveContextLines = Math.Clamp(contextLines ?? 5, 0, MaxModuleContextLines);
        var dbNormalized = NormalizeTextForComparison(module.Definition);
        var fileNormalized = NormalizeTextForComparison(fileText);
        var dbSqlNormalized = NormalizeSqlModuleTextForComparison(module.Definition);
        var fileSqlNormalized = NormalizeSqlModuleTextForComparison(fileText);
        var diff = BuildLineDiff(module.Definition, fileText, effectiveContextLines);

        return new ModuleFileComparisonResult(
            module.Schema,
            module.Name,
            module.Type,
            module.TypeDesc,
            new ModuleCompareDatabaseInfo(
                module.CreateDate,
                module.ModifyDate,
                module.Definition.Length,
                SplitDefinitionLines(module.Definition).Length,
                ComputeSha256Hex(module.Definition),
                ComputeSha256Hex(dbNormalized),
                ComputeSha256Hex(dbSqlNormalized)),
            new ModuleCompareFileInfo(
                file.FullName,
                file.Length,
                file.LastWriteTime,
                fileText.Length,
                SplitDefinitionLines(fileText).Length,
                ComputeSha256Hex(fileText),
                ComputeSha256Hex(fileNormalized),
                ComputeSha256Hex(fileSqlNormalized)),
            string.Equals(module.Definition, fileText, StringComparison.Ordinal),
            string.Equals(dbNormalized, fileNormalized, StringComparison.Ordinal),
            string.Equals(dbSqlNormalized, fileSqlNormalized, StringComparison.Ordinal),
            diff,
            ModuleCompareNextActions);
    }

    internal static ModuleSqlFileDiscovery FindModuleSqlFileDiscovery(
        string? root,
        string schema,
        string name,
        string[]? patterns,
        int? maxCandidates)
    {
        var directory = GetReadableCompareRoot(root);
        var effectivePatterns = NormalizeRepoComparePatterns(patterns);
        var effectiveMaxCandidates = Math.Clamp(maxCandidates ?? DefaultRepoCompareCandidateLimit, 1, MaxRepoCompareCandidateLimit);
        var candidates = new List<ModuleSqlFileCandidate>();
        var scannedFileCount = 0;
        var searchTruncated = false;

        foreach (var file in EnumerateSqlFiles(directory))
        {
            if (scannedFileCount >= MaxRepoCompareFilesScanned)
            {
                searchTruncated = true;
                break;
            }

            scannedFileCount++;
            var relativePath = Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/');
            if (!MatchesRepoComparePatterns(relativePath, file.Name, effectivePatterns))
            {
                continue;
            }

            var candidate = BuildModuleSqlFileCandidate(directory, file, relativePath, schema, name);
            if (candidate.Score > 0)
            {
                candidates.Add(candidate);
            }
        }

        var orderedCandidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RelativePath.Length)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visibleCandidates = orderedCandidates.Take(effectiveMaxCandidates).ToArray();
        var topScore = orderedCandidates.FirstOrDefault()?.Score;
        var topScoreCount = topScore is null
            ? 0
            : orderedCandidates.Count(candidate => candidate.Score == topScore.Value);
        var selectedCandidate = topScoreCount == 1
            ? orderedCandidates[0]
            : null;
        var ambiguous = orderedCandidates.Length > 0 && selectedCandidate is null;
        var hint = BuildRepoCompareDiscoveryHint(orderedCandidates.Length, ambiguous, searchTruncated);

        return new ModuleSqlFileDiscovery(
            directory.FullName,
            effectivePatterns,
            scannedFileCount,
            searchTruncated,
            orderedCandidates.Length,
            orderedCandidates.Length > effectiveMaxCandidates,
            visibleCandidates,
            selectedCandidate,
            ambiguous,
            hint);
    }

    private static string[] NormalizeRepoComparePatterns(string[]? patterns)
    {
        var normalized = patterns?
            .Select(pattern => pattern?.Trim().Replace('\\', '/'))
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized is { Length: > 0 }
            ? normalized
            : DefaultRepoComparePatterns;
    }

    private static IEnumerable<FileInfo> EnumerateSqlFiles(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in SafeEnumerateDirectories(directory))
            {
                if (!RepoCompareSkippedDirectoryNames.Contains(child.Name))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in SafeEnumerateFiles(directory))
            {
                yield return file;
            }
        }
    }

    private static DirectoryInfo[] SafeEnumerateDirectories(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateDirectories().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileInfo[] SafeEnumerateFiles(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*.sql").ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool MatchesRepoComparePatterns(string relativePath, string fileName, string[] patterns)
    {
        return patterns.Any(pattern =>
            GlobMatches(relativePath, pattern)
            || (!pattern.Contains('/', StringComparison.Ordinal) && GlobMatches(fileName, pattern)));
    }

    private static bool GlobMatches(string value, string pattern)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        return Regex.IsMatch(value, GlobToRegex(normalizedPattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               || (normalizedPattern.StartsWith("**/", StringComparison.Ordinal)
                   && Regex.IsMatch(value, GlobToRegex(normalizedPattern[3..]), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static string GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            if (current == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    builder.Append(".*");
                    i++;
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (current == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(current.ToString()));
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static ModuleSqlFileCandidate BuildModuleSqlFileCandidate(
        DirectoryInfo root,
        FileInfo file,
        string relativePath,
        string schema,
        string name)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
        var schemaDotName = $"{schema}.{name}";
        var schemaUnderscoreName = $"{schema}_{name}";
        var reasons = new List<string>();
        var score = 0;

        if (fileNameWithoutExtension.Equals(schemaDotName, StringComparison.OrdinalIgnoreCase))
        {
            score += 160;
            reasons.Add("schema-qualified file name");
        }

        if (fileNameWithoutExtension.Equals(schemaUnderscoreName, StringComparison.OrdinalIgnoreCase))
        {
            score += 150;
            reasons.Add("schema-qualified underscore file name");
        }

        if (fileNameWithoutExtension.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            score += 140;
            reasons.Add("exact object file name");
        }

        if (fileNameWithoutExtension.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExtension.EndsWith($"_{name}", StringComparison.OrdinalIgnoreCase))
        {
            score += 90;
            reasons.Add("file name suffix matches object name");
        }

        if (fileNameWithoutExtension.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
            reasons.Add("file name contains object name");
        }

        if (relativePath.Contains(schemaDotName, StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
            reasons.Add("relative path contains schema.object");
        }

        var pathParts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Any(part => part.Equals(schema, StringComparison.OrdinalIgnoreCase)))
        {
            score += 20;
            reasons.Add("path contains schema folder");
        }

        if (pathParts.Any(part => part.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            score += 20;
            reasons.Add("path contains object folder");
        }

        return new ModuleSqlFileCandidate(
            file.FullName,
            relativePath,
            file.Name,
            file.Length,
            file.LastWriteTime,
            score,
            reasons.ToArray());
    }

    private static string? BuildRepoCompareDiscoveryHint(int candidateCount, bool ambiguous, bool searchTruncated)
    {
        if (candidateCount == 0)
        {
            return searchTruncated
                ? "No matching .sql file was found before the scan cap. Pass a narrower root or patterns."
                : "No matching .sql file was found. Pass filePath to compare_module_to_file, or pass root/patterns to narrow repository discovery.";
        }

        if (ambiguous)
        {
            return "Multiple top-ranked .sql candidates were found. Pass filePath to compare_module_to_file, or narrow root/patterns.";
        }

        return searchTruncated
            ? "A matching file was selected before the scan cap. Narrow root/patterns if this repository is very large."
            : null;
    }

    private object? ReadValue(SqlDataReader reader, int ordinal, QueryValueTruncationInfo truncation)
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
            string text => TruncateText(text, reader.GetName(ordinal), truncation),
            DateTime dateTime => dateTime.ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O"),
            TimeSpan timeSpan => timeSpan.ToString(),
            Guid guid => guid.ToString(),
            _ => value
        };
    }

    private string TruncateText(string text, string columnName, QueryValueTruncationInfo truncation)
    {
        if (text.Length <= _options.Limits.MaxTextLength)
        {
            return text;
        }

        truncation.TextValuesTruncated++;
        truncation.ColumnsWithTruncatedText.Add(columnName);
        return string.Concat(text.AsSpan(0, _options.Limits.MaxTextLength), "...<truncated>");
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

    internal static ModuleDefinitionSlice BuildModuleDefinitionSlice(
        string definition,
        string? keyword,
        int? startLine,
        int? endLine,
        int? contextLines,
        int maxLines)
    {
        var lines = SplitDefinitionLines(definition);
        var totalLines = lines.Length;
        var cleanKeyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var hasLineRange = startLine.HasValue || endLine.HasValue;
        var effectiveMaxLines = Math.Clamp(maxLines, 1, 5000);
        var effectiveContextLines = Math.Clamp(contextLines ?? DefaultModuleContextLines, 0, MaxModuleContextLines);

        if (hasLineRange)
        {
            var start = startLine ?? 1;
            var end = endLine ?? totalLines;
            if (start < 1 || end < 1 || start > end)
            {
                throw new SqlMcpException(
                    ErrorCodes.ConfigInvalid,
                    "Invalid module definition line range.",
                    $"startLine={startLine}, endLine={endLine}",
                    "Use 1-based line numbers with startLine less than or equal to endLine.");
            }

            if (start > totalLines)
            {
                throw new SqlMcpException(
                    ErrorCodes.ConfigInvalid,
                    "Module definition line range starts after the final line.",
                    $"lineCount={totalLines}, startLine={start}",
                    "Use a startLine within the returned lineCount.");
            }

            end = Math.Min(end, totalLines);
            return BuildSlice(
                lines,
                [(start, end)],
                cleanKeyword,
                totalLines,
                "line_range",
                effectiveContextLines,
                effectiveMaxLines);
        }

        if (!string.IsNullOrWhiteSpace(cleanKeyword))
        {
            var ranges = new List<(int Start, int End)>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(cleanKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    var lineNumber = i + 1;
                    ranges.Add((
                        Math.Max(1, lineNumber - effectiveContextLines),
                        Math.Min(totalLines, lineNumber + effectiveContextLines)));
                }
            }

            return BuildSlice(
                lines,
                ranges,
                cleanKeyword,
                totalLines,
                "keyword",
                effectiveContextLines,
                effectiveMaxLines);
        }

        return BuildSlice(
            lines,
            [(1, totalLines)],
            null,
            totalLines,
            "full",
            effectiveContextLines,
            totalLines);
    }

    private static ModuleDefinitionSlice BuildSlice(
        string[] allLines,
        IReadOnlyList<(int Start, int End)> ranges,
        string? keyword,
        int totalLines,
        string reason,
        int contextLines,
        int maxLines)
    {
        var mergedRanges = MergeLineRanges(ranges);
        var selectedLines = new List<ModuleDefinitionLine>();
        var matchedLines = new List<int>();
        var truncated = false;

        foreach (var range in mergedRanges)
        {
            for (var lineNumber = range.Start; lineNumber <= range.End; lineNumber++)
            {
                if (selectedLines.Count >= maxLines)
                {
                    truncated = true;
                    break;
                }

                var text = allLines[lineNumber - 1];
                selectedLines.Add(new ModuleDefinitionLine(lineNumber, text));
                if (!string.IsNullOrWhiteSpace(keyword)
                    && text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matchedLines.Add(lineNumber);
                }
            }

            if (truncated)
            {
                break;
            }
        }

        var isPartial = reason != "full" || truncated;
        return new ModuleDefinitionSlice(
            string.Join(Environment.NewLine, selectedLines.Select(line => line.Text)),
            totalLines,
            selectedLines.Count == 0 ? null : selectedLines[0].LineNumber,
            selectedLines.Count == 0 ? null : selectedLines[^1].LineNumber,
            selectedLines.Count,
            isPartial,
            truncated,
            reason,
            contextLines,
            matchedLines.Distinct().ToArray(),
            selectedLines.ToArray());
    }

    private static IReadOnlyList<(int Start, int End)> MergeLineRanges(IReadOnlyList<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        var ordered = ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
        var merged = new List<(int Start, int End)> { ordered[0] };

        foreach (var range in ordered.Skip(1))
        {
            var previous = merged[^1];
            if (range.Start <= previous.End + 1)
            {
                merged[^1] = (previous.Start, Math.Max(previous.End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static string[] SplitDefinitionLines(string definition)
    {
        var normalized = definition.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0
            ? [string.Empty]
            : normalized.Split('\n');
    }

    internal static ModuleFileDiff BuildLineDiff(string databaseDefinition, string fileText, int contextLines)
    {
        var databaseLines = SplitDefinitionLines(databaseDefinition);
        var fileLines = SplitDefinitionLines(fileText);
        var effectiveContextLines = Math.Clamp(contextLines, 0, MaxModuleContextLines);
        var segments = BuildDiffChangeSegments(databaseLines, fileLines);
        if (segments.Count == 0)
        {
            return new ModuleFileDiff(true, null, 0, 0, false, 0, []);
        }

        var hunks = segments
            .Select(segment => BuildDiffHunk(databaseLines, fileLines, segment, effectiveContextLines))
            .ToArray();
        var visibleHunks = hunks.Take(MaxDiffHunks).ToArray();
        var omittedHunkCount = Math.Max(0, hunks.Length - visibleHunks.Length);
        var linesTruncated = visibleHunks.Any(hunk => hunk.DatabaseLinesTruncated || hunk.FileLinesTruncated);

        return new ModuleFileDiff(
            false,
            segments.Min(segment => Math.Min(segment.DatabaseDisplayLine, segment.FileDisplayLine)),
            segments.Sum(segment => segment.DatabaseChangedLineCount),
            segments.Sum(segment => segment.FileChangedLineCount),
            omittedHunkCount > 0 || linesTruncated,
            omittedHunkCount,
            visibleHunks);
    }

    private static List<DiffChangeSegment> BuildDiffChangeSegments(string[] databaseLines, string[] fileLines)
    {
        var segments = new List<DiffChangeSegment>();
        var databaseIndex = 0;
        var fileIndex = 0;

        while (databaseIndex < databaseLines.Length || fileIndex < fileLines.Length)
        {
            if (databaseIndex < databaseLines.Length
                && fileIndex < fileLines.Length
                && string.Equals(databaseLines[databaseIndex], fileLines[fileIndex], StringComparison.Ordinal))
            {
                databaseIndex++;
                fileIndex++;
                continue;
            }

            var databaseStart = databaseIndex;
            var fileStart = fileIndex;
            var sync = FindNextDiffSync(databaseLines, fileLines, databaseIndex, fileIndex);
            if (sync is null)
            {
                segments.Add(new DiffChangeSegment(databaseStart, databaseLines.Length - 1, fileStart, fileLines.Length - 1));
                break;
            }

            segments.Add(new DiffChangeSegment(databaseStart, sync.Value.DatabaseIndex - 1, fileStart, sync.Value.FileIndex - 1));
            databaseIndex = sync.Value.DatabaseIndex;
            fileIndex = sync.Value.FileIndex;
        }

        return segments
            .Where(segment => segment.DatabaseChangedLineCount > 0 || segment.FileChangedLineCount > 0)
            .ToList();
    }

    private static (int DatabaseIndex, int FileIndex)? FindNextDiffSync(
        string[] databaseLines,
        string[] fileLines,
        int databaseIndex,
        int fileIndex)
    {
        var maxDatabaseIndex = Math.Min(databaseLines.Length - 1, databaseIndex + MaxDiffSyncLookahead);
        var maxFileIndex = Math.Min(fileLines.Length - 1, fileIndex + MaxDiffSyncLookahead);
        for (var offset = 1; offset <= MaxDiffSyncLookahead; offset++)
        {
            var candidateDatabaseIndex = databaseIndex + offset;
            if (candidateDatabaseIndex <= maxDatabaseIndex
                && fileIndex < fileLines.Length
                && IsDiffSyncLine(databaseLines[candidateDatabaseIndex])
                && string.Equals(databaseLines[candidateDatabaseIndex], fileLines[fileIndex], StringComparison.Ordinal))
            {
                return (candidateDatabaseIndex, fileIndex);
            }

            var candidateFileIndex = fileIndex + offset;
            if (candidateFileIndex <= maxFileIndex
                && databaseIndex < databaseLines.Length
                && IsDiffSyncLine(fileLines[candidateFileIndex])
                && string.Equals(databaseLines[databaseIndex], fileLines[candidateFileIndex], StringComparison.Ordinal))
            {
                return (databaseIndex, candidateFileIndex);
            }
        }

        (int DatabaseIndex, int FileIndex)? best = null;
        var bestDistance = int.MaxValue;
        for (var i = databaseIndex + 1; i <= maxDatabaseIndex; i++)
        {
            if (!IsDiffSyncLine(databaseLines[i]))
            {
                continue;
            }

            for (var j = fileIndex + 1; j <= maxFileIndex; j++)
            {
                if (!string.Equals(databaseLines[i], fileLines[j], StringComparison.Ordinal))
                {
                    continue;
                }

                var distance = (i - databaseIndex) + (j - fileIndex);
                if (distance < bestDistance)
                {
                    best = (i, j);
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    private static bool IsDiffSyncLine(string line)
    {
        return line.Trim().Length > 2;
    }

    private static ModuleFileDiffHunk BuildDiffHunk(
        string[] databaseLines,
        string[] fileLines,
        DiffChangeSegment segment,
        int contextLines)
    {
        var databaseRange = BuildHunkRange(databaseLines.Length, segment.DatabaseStartLine, segment.DatabaseEndLine, contextLines);
        var fileRange = BuildHunkRange(fileLines.Length, segment.FileStartLine, segment.FileEndLine, contextLines);

        var databaseDiffLines = BuildDiffLines(
            databaseLines,
            databaseRange.Start,
            databaseRange.End,
            segment.DatabaseStartLine,
            segment.DatabaseEndLine,
            out var databaseLinesTruncated);
        var fileDiffLines = BuildDiffLines(
            fileLines,
            fileRange.Start,
            fileRange.End,
            segment.FileStartLine,
            segment.FileEndLine,
            out var fileLinesTruncated);

        return new ModuleFileDiffHunk(
            databaseRange.Start,
            databaseRange.End,
            fileRange.Start,
            fileRange.End,
            databaseLinesTruncated,
            fileLinesTruncated,
            databaseDiffLines,
            fileDiffLines);
    }

    private static (int Start, int End) BuildHunkRange(int lineCount, int changedStart, int changedEnd, int contextLines)
    {
        if (lineCount == 0)
        {
            return (1, 0);
        }

        if (changedStart <= changedEnd)
        {
            return (
                Math.Max(1, changedStart - contextLines),
                Math.Min(lineCount, changedEnd + contextLines));
        }

        var anchor = Math.Clamp(changedStart, 1, lineCount);
        return (
            Math.Max(1, anchor - contextLines),
            Math.Min(lineCount, anchor + contextLines));
    }

    private static ModuleFileDiffLine[] BuildDiffLines(
        string[] lines,
        int startLine,
        int endLine,
        int changedStart,
        int changedEnd,
        out bool truncated)
    {
        truncated = false;
        if (lines.Length == 0 || startLine > endLine)
        {
            return [];
        }

        var result = new List<ModuleFileDiffLine>();
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            if (result.Count >= MaxDiffLinesPerSide)
            {
                truncated = true;
                break;
            }

            result.Add(new ModuleFileDiffLine(
                lineNumber,
                lines[lineNumber - 1],
                lineNumber >= changedStart && lineNumber <= changedEnd));
        }

        return result.ToArray();
    }

    private static string NormalizeTextForComparison(string text)
    {
        return string.Join(
                "\n",
                SplitDefinitionLines(text.Trim('\uFEFF'))
                    .Select(line => line.TrimEnd()))
            .Trim();
    }

    internal static string NormalizeSqlModuleTextForComparison(string text)
    {
        var lines = SplitDefinitionLines(text.Trim('\uFEFF'))
            .Select(line => line.TrimEnd())
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0
               && (SqlBatchSeparatorRegex.IsMatch(lines[0])
                   || SqlSessionSetRegex.IsMatch(lines[0])
                   || string.IsNullOrWhiteSpace(lines[0])))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0
               && (SqlBatchSeparatorRegex.IsMatch(lines[^1])
                   || string.IsNullOrWhiteSpace(lines[^1])))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            lines[i] = SqlModuleCreateRegex.Replace(lines[i], match =>
            {
                var objectType = Regex.Replace(match.Groups[1].Value.ToUpperInvariant(), @"\s+", " ");
                return $"CREATE {objectType}";
            });
            break;
        }

        return string.Join("\n", lines).Trim();
    }

    internal static TempTableAnalysis AnalyzeTempTables(string definition)
    {
        var lines = SplitDefinitionLines(definition);
        var tables = new Dictionary<string, TempTableBuilder>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i];
            foreach (Match match in TempTableNameRegex.Matches(line))
            {
                var tableName = match.Value;
                var builder = GetTempTableBuilder(tables, tableName);
                builder.SetFirstLine(lineNumber);
                var statement = BuildTempTableStatement(lines, i);
                var operation = ClassifyTempTableOperation(statement.CompactText, tableName);
                var referenceColumns = ExtractTempTableReferenceColumns(statement.CompactText, tableName, operation);
                builder.References.Add(new TempTableReference(
                    lineNumber,
                    operation,
                    BuildStatementPreview(statement.CompactText),
                    statement.EndLine,
                    referenceColumns));
                builder.ColumnFlowEvents.AddRange(referenceColumns
                    .Select(column => new TempTableColumnFlowEvent(column, operation, lineNumber)));

                if (operation is "create_table" or "select_into")
                {
                    builder.Creations.Add(new TempTableCreation(
                        lineNumber,
                        operation,
                        BuildStatementPreview(statement.CompactText),
                        statement.EndLine,
                        referenceColumns));
                }

                if (operation == "create_table")
                {
                    var columns = ExtractTempTableColumns(lines, i);
                    builder.Columns.AddRange(columns);
                    builder.ColumnFlowEvents.AddRange(columns
                        .Select(column => new TempTableColumnFlowEvent(column.Name, "create_table", column.LineNumber)));
                }
            }
        }

        var items = tables.Values
            .OrderBy(table => table.FirstLine)
            .Select(table => new TempTableInfo(
                table.Name,
                table.FirstLine,
                table.Creations.ToArray(),
                table.Columns
                    .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray(),
                table.References
                    .GroupBy(reference => new { reference.LineNumber, reference.Operation, reference.Text })
                    .Select(group => group.First())
                    .OrderBy(reference => reference.LineNumber)
                    .ToArray(),
                table.References
                    .GroupBy(reference => reference.Operation, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                BuildTempTableColumnFlow(table)))
            .ToArray();

        return new TempTableAnalysis(items);
    }

    private static TempTableColumnFlow[] BuildTempTableColumnFlow(TempTableBuilder table)
    {
        return table.ColumnFlowEvents
            .Where(flow => !string.IsNullOrWhiteSpace(flow.Column))
            .GroupBy(flow => flow.Column, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .Select(group => new TempTableColumnFlow(
                group.Key,
                group
                    .GroupBy(flow => flow.Operation, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(operationGroup => operationGroup.Key)
                    .ToDictionary(operationGroup => operationGroup.Key, operationGroup => operationGroup.Count(), StringComparer.OrdinalIgnoreCase),
                group.Select(flow => flow.LineNumber).Distinct().OrderBy(line => line).ToArray()))
            .ToArray();
    }

    private static TempTableBuilder GetTempTableBuilder(
        Dictionary<string, TempTableBuilder> tables,
        string tableName)
    {
        if (!tables.TryGetValue(tableName, out var builder))
        {
            builder = new TempTableBuilder(tableName);
            tables.Add(tableName, builder);
        }

        return builder;
    }

    private static string ClassifyTempTableOperation(string line, string tableName)
    {
        var normalized = NormalizeSqlLine(line);
        var table = Regex.Escape(tableName);

        if (Regex.IsMatch(normalized, $@"\bCREATE\s+TABLE\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "create_table";
        }

        if (Regex.IsMatch(normalized, $@"\bINSERT\s+(?:INTO\s+)?{table}\b", RegexOptions.IgnoreCase))
        {
            return "insert";
        }

        if (Regex.IsMatch(normalized, $@"\bINTO\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "select_into";
        }

        if (Regex.IsMatch(normalized, $@"\bUPDATE\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "update";
        }

        if (Regex.IsMatch(normalized, $@"\bUPDATE\s+\w+\b.*\bFROM\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "update";
        }

        if (Regex.IsMatch(normalized, $@"\bDELETE\s+(?:FROM\s+)?{table}\b", RegexOptions.IgnoreCase))
        {
            return "delete";
        }

        if (Regex.IsMatch(normalized, $@"\bJOIN\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "join";
        }

        if (Regex.IsMatch(normalized, $@"\bFROM\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "read";
        }

        if (Regex.IsMatch(normalized, $@"\bMERGE\s+{table}\b", RegexOptions.IgnoreCase))
        {
            return "merge";
        }

        return "reference";
    }

    private static string NormalizeSqlLine(string line)
    {
        return Regex.Replace(line, @"\s+", " ").Trim();
    }

    private static TempTableStatement BuildTempTableStatement(string[] lines, int lineIndex)
    {
        var start = lineIndex;
        for (var i = lineIndex; i >= Math.Max(0, lineIndex - 8); i--)
        {
            var trimmed = lines[i].Trim();
            if (i < lineIndex && trimmed.Contains(';', StringComparison.Ordinal))
            {
                break;
            }

            start = i;
            if (Regex.IsMatch(trimmed, @"^(CREATE\s+TABLE|SELECT|INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            {
                break;
            }

            if (i < lineIndex && string.IsNullOrWhiteSpace(trimmed))
            {
                start = i + 1;
                break;
            }
        }

        var end = lineIndex;
        for (var i = lineIndex; i < Math.Min(lines.Length, lineIndex + 40); i++)
        {
            var trimmed = lines[i].Trim();
            if (i > lineIndex
                && Regex.IsMatch(trimmed, @"^(CREATE\s+TABLE|SELECT|INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            {
                end = i - 1;
                break;
            }

            end = i;
            if (trimmed.Contains(';', StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = string.Join(Environment.NewLine, lines.Skip(start).Take(end - start + 1));
        return new TempTableStatement(start + 1, end + 1, text, NormalizeSqlLine(text));
    }

    private static string BuildStatementPreview(string compactText)
    {
        return compactText.Length <= 500
            ? compactText
            : string.Concat(compactText.AsSpan(0, 500), "...<truncated>");
    }

    private static string[] ExtractTempTableReferenceColumns(string statement, string tableName, string operation)
    {
        return operation switch
        {
            "insert" => ExtractInsertTargetColumns(statement, tableName),
            "select_into" => ExtractSelectIntoColumns(statement, tableName),
            "update" => ExtractUpdateSetColumns(statement),
            "create_table" => [],
            _ => []
        };
    }

    private static string[] ExtractInsertTargetColumns(string statement, string tableName)
    {
        var match = Regex.Match(
            statement,
            $@"\bINSERT\s+(?:INTO\s+)?{Regex.Escape(tableName)}\s*\((?<columns>[^)]*)\)",
            RegexOptions.IgnoreCase);

        return match.Success
            ? ParseColumnList(match.Groups["columns"].Value)
            : [];
    }

    private static string[] ExtractSelectIntoColumns(string statement, string tableName)
    {
        var match = Regex.Match(
            statement,
            $@"\bSELECT\s+(?<select>.*?)\s+\bINTO\s+{Regex.Escape(tableName)}\b",
            RegexOptions.IgnoreCase);

        return match.Success
            ? SplitTopLevelCommas(match.Groups["select"].Value)
                .Select(ExtractProjectionColumnName)
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .Select(column => column!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private static string[] ExtractUpdateSetColumns(string statement)
    {
        var match = Regex.Match(
            statement,
            @"\bSET\s+(?<set>.*?)(?:\s+FROM\b|\s+WHERE\b|\s+OUTPUT\b|$)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return [];
        }

        return SplitTopLevelCommas(match.Groups["set"].Value)
            .Select(item => item.Split('=', 2)[0])
            .Select(NormalizeColumnToken)
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(column => column!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ParseColumnList(string text)
    {
        return SplitTopLevelCommas(text)
            .Select(NormalizeColumnToken)
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(column => column!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ExtractProjectionColumnName(string expression)
    {
        var item = expression.Trim();
        if (item.Length == 0 || item == "*")
        {
            return null;
        }

        var asMatch = Regex.Match(item, @"\bAS\s+(?<name>\[?[A-Za-z_][A-Za-z0-9_]*\]?)$", RegexOptions.IgnoreCase);
        if (asMatch.Success)
        {
            return NormalizeColumnToken(asMatch.Groups["name"].Value);
        }

        var trailingAlias = Regex.Match(item, @"\s+(?<name>\[?[A-Za-z_][A-Za-z0-9_]*\]?)$");
        if (trailingAlias.Success
            && !item.EndsWith(")", StringComparison.Ordinal)
            && !item.Contains("=", StringComparison.Ordinal))
        {
            return NormalizeColumnToken(trailingAlias.Groups["name"].Value);
        }

        var lastIdentifier = Regex.Match(item, @"(?:\.|\b)(?<name>\[?[A-Za-z_][A-Za-z0-9_]*\]?)$");
        return lastIdentifier.Success
            ? NormalizeColumnToken(lastIdentifier.Groups["name"].Value)
            : null;
    }

    private static string? NormalizeColumnToken(string token)
    {
        var normalized = token.Trim().TrimEnd(',');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            normalized = normalized[(normalized.LastIndexOf('.') + 1)..];
        }

        normalized = normalized.Trim().Trim('[', ']');
        return Regex.IsMatch(normalized, @"^[A-Za-z_][A-Za-z0-9_]*$")
            ? normalized
            : null;
    }

    private static string[] SplitTopLevelCommas(string text)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var bracketDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
            }
            else if (bracketDepth == 0 && ch == '(')
            {
                depth++;
            }
            else if (bracketDepth == 0 && ch == ')' && depth > 0)
            {
                depth--;
            }
            else if (bracketDepth == 0 && depth == 0 && ch == ',')
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }

        result.Add(text[start..]);
        return result
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<TempTableColumn> ExtractTempTableColumns(string[] lines, int createLineIndex)
    {
        var columns = new List<TempTableColumn>();
        var openParenSeen = false;
        for (var i = createLineIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!openParenSeen)
            {
                var openIndex = line.IndexOf('(');
                if (openIndex < 0)
                {
                    continue;
                }

                openParenSeen = true;
                line = line[(openIndex + 1)..];
            }

            var closeIndex = line.IndexOf(')');
            var candidate = closeIndex >= 0 ? line[..closeIndex] : line;
            var column = ParseTempTableColumn(i + 1, candidate);
            if (column is not null)
            {
                columns.Add(column);
            }

            if (closeIndex >= 0)
            {
                break;
            }
        }

        return columns;
    }

    private static TempTableColumn? ParseTempTableColumn(int lineNumber, string line)
    {
        var trimmed = line.Trim().TrimEnd(',');
        if (trimmed.Length == 0
            || trimmed.StartsWith("--", StringComparison.Ordinal)
            || trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("PRIMARY ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("CHECK ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = Regex.Match(trimmed, @"^\[?(?<name>[A-Za-z_][A-Za-z0-9_]*)\]?\s+(?<definition>.+)$");
        return match.Success
            ? new TempTableColumn(lineNumber, match.Groups["name"].Value, match.Groups["definition"].Value.Trim())
            : null;
    }

    internal static SqlTemplateApplication ApplySqlTemplateValues(
        string sql,
        IReadOnlyDictionary<string, object?>? templateValues)
    {
        if (templateValues is null || templateValues.Count == 0)
        {
            return new SqlTemplateApplication(sql, []);
        }

        if (templateValues.Count > 50)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Too many SQL template values.",
                templateValues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Pass at most 50 template placeholder replacements.");
        }

        var result = sql;
        var replacements = new List<SqlTemplateReplacement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (rawKey, rawValue) in templateValues)
        {
            var placeholder = NormalizeSqlTemplatePlaceholder(rawKey);
            if (!seen.Add(placeholder))
            {
                throw new SqlMcpException(
                    ErrorCodes.ConfigInvalid,
                    "Duplicate SQL template placeholder.",
                    placeholder,
                    "Use unique placeholder keys such as 0 or {0}.");
            }

            var value = ConvertSqlTemplateValue(rawValue);
            var occurrences = CountOccurrences(result, placeholder);
            if (occurrences > 0)
            {
                result = result.Replace(placeholder, value, StringComparison.Ordinal);
            }

            replacements.Add(new SqlTemplateReplacement(
                placeholder,
                occurrences > 0,
                occurrences,
                value.Length));
        }

        return new SqlTemplateApplication(result, replacements.ToArray());
    }

    private static string NormalizeSqlTemplatePlaceholder(string rawKey)
    {
        var key = rawKey.Trim();
        var match = Regex.Match(key, @"^(?:\{(?<index>\d{1,3})\}|(?<index>\d{1,3}))$");
        if (!match.Success)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Invalid SQL template placeholder.",
                rawKey,
                "Use numeric UI placeholders such as 0 or {0}.");
        }

        return $"{{{match.Groups["index"].Value}}}";
    }

    private static string ConvertSqlTemplateValue(object? rawValue)
    {
        var value = NormalizeParameterValue(rawValue);
        var text = value switch
        {
            null => "NULL",
            bool boolean => boolean ? "1" : "0",
            int integer => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long integer => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string sqlFragment => sqlFragment,
            _ => throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Unsupported SQL template value type.",
                value.GetType().Name,
                "Use JSON string, number, boolean, or null values.")
        };

        if (text.Length > 8000)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "SQL template value is too long.",
                text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Use a shorter SQL fragment.");
        }

        return text;
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(token, start, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            start = index + token.Length;
        }

        return count;
    }

    internal static IReadOnlyList<UserSqlParameterSpec> BuildUserSqlParameters(
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UserSqlParameterSpec>();
        foreach (var (rawName, rawValue) in parameters)
        {
            var name = NormalizeParameterName(rawName);
            if (!seen.Add(name))
            {
                throw new SqlMcpException(
                    ErrorCodes.ConfigInvalid,
                    "Duplicate SQL parameter name.",
                    name,
                    "Use unique parameter names after removing the optional @ prefix.");
            }

            result.Add(BuildUserSqlParameter(name, rawValue));
        }

        return result;
    }

    private static UserSqlParameterSpec BuildUserSqlParameter(string name, object? rawValue)
    {
        var value = NormalizeParameterValue(rawValue);
        return value switch
        {
            null => new UserSqlParameterSpec(name, null, SqlDbType.NVarChar, 4000, $"{name} nvarchar(4000)"),
            bool boolean => new UserSqlParameterSpec(name, boolean, SqlDbType.Bit, null, $"{name} bit"),
            int integer => new UserSqlParameterSpec(name, integer, SqlDbType.Int, null, $"{name} int"),
            long integer => new UserSqlParameterSpec(name, integer, SqlDbType.BigInt, null, $"{name} bigint"),
            decimal number => new UserSqlParameterSpec(name, number, SqlDbType.Decimal, null, $"{name} decimal(38,10)"),
            double number => new UserSqlParameterSpec(name, number, SqlDbType.Float, null, $"{name} float"),
            DateTime dateTime => new UserSqlParameterSpec(name, dateTime, SqlDbType.DateTime2, null, $"{name} datetime2"),
            DateTimeOffset dateTimeOffset => new UserSqlParameterSpec(name, dateTimeOffset, SqlDbType.DateTimeOffset, null, $"{name} datetimeoffset"),
            Guid guid => new UserSqlParameterSpec(name, guid, SqlDbType.UniqueIdentifier, null, $"{name} uniqueidentifier"),
            string text => new UserSqlParameterSpec(
                name,
                text,
                SqlDbType.NVarChar,
                text.Length > 4000 ? -1 : 4000,
                text.Length > 4000 ? $"{name} nvarchar(max)" : $"{name} nvarchar(4000)"),
            _ => throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Unsupported SQL parameter value type.",
                $"{name}: {value.GetType().Name}",
                "Use JSON string, number, boolean, or null values.")
        };
    }

    private static object? NormalizeParameterValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
                JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
                _ => throw new SqlMcpException(
                    ErrorCodes.ConfigInvalid,
                    "Unsupported SQL parameter JSON value.",
                    element.ValueKind.ToString(),
                    "Use JSON string, number, boolean, or null values.")
            };
        }

        return value;
    }

    private static string NormalizeParameterName(string rawName)
    {
        var name = rawName.Trim();
        if (name.StartsWith('@'))
        {
            name = name[1..];
        }

        if (name.Length is < 1 or > 128
            || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Invalid SQL parameter name.",
                rawName,
                "Use names like id, customer_id, or @customer_id.");
        }

        return $"@{name}";
    }

    private static string BuildParameterDefinitionList(IReadOnlyList<UserSqlParameterSpec> parameters)
    {
        return string.Join(", ", parameters.Select(parameter => parameter.Definition));
    }

    private static object ToParameterSummary(UserSqlParameterSpec parameter)
    {
        return new
        {
            name = parameter.Name,
            sqlType = parameter.Definition[(parameter.Name.Length + 1)..]
        };
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

    private async Task<object> ValidateTextSearchTargetsAsync(CancellationToken cancellationToken)
    {
        var enabledTargets = _options.TextSearch.Targets
            .Where(target => target.Enabled)
            .ToArray();

        if (enabledTargets.Length == 0)
        {
            return new
            {
                ok = true,
                checkedTargetCount = 0,
                invalidTargetCount = 0,
                targets = Array.Empty<object>()
            };
        }

        var validNameTargets = enabledTargets
            .Where(target => IsValidTextSearchTarget(target))
            .ToArray();
        var columnLookup = await LoadTextSearchTargetColumnsAsync(validNameTargets, cancellationToken);

        var targetResults = enabledTargets
            .Select(target => BuildTextSearchTargetValidation(target, columnLookup))
            .ToArray();

        return new
        {
            ok = targetResults.All(target => target.Ok),
            checkedTargetCount = targetResults.Length,
            invalidTargetCount = targetResults.Count(target => !target.Ok),
            targets = targetResults.Select(target => new
            {
                target.Profile,
                target.Schema,
                target.Table,
                target.TextColumn,
                target.Ok,
                target.TableExists,
                target.IdentifierConfigOk,
                target.ConfiguredColumns,
                target.MissingColumns,
                target.Hint
            }).ToArray()
        };
    }

    private async Task<Dictionary<string, HashSet<string>>> LoadTextSearchTargetColumnsAsync(
        IReadOnlyList<TextSearchTargetOptions> targets,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var parameters = new List<SqlParameter>();
        var predicates = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var schemaParameter = $"@schema{i}";
            var tableParameter = $"@table{i}";
            parameters.Add(new SqlParameter(schemaParameter, SqlDbType.NVarChar, 128) { Value = targets[i].Schema });
            parameters.Add(new SqlParameter(tableParameter, SqlDbType.NVarChar, 128) { Value = targets[i].Table });
            predicates.Add($"(S.name={schemaParameter} AND O.name={tableParameter})");
        }

        var sql = $"""
                   SELECT
                       schema_name=S.name,
                       object_name=O.name,
                       column_name=C.name
                   FROM sys.objects O
                   INNER JOIN sys.schemas S ON S.schema_id=O.schema_id
                   INNER JOIN sys.columns C ON C.object_id=O.object_id
                   WHERE O.type IN (N'U', N'V')
                       AND ({string.Join(" OR ", predicates)})
                   ORDER BY S.name, O.name, C.column_id;
                   """;

        var rows = await QueryAsync(
            sql,
            parameters,
            reader => new
            {
                Schema = reader.GetString("schema_name"),
                Name = reader.GetString("object_name"),
                Column = reader.GetString("column_name")
            },
            cancellationToken);

        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.GroupBy(row => BuildTargetKey(row.Schema, row.Name), StringComparer.OrdinalIgnoreCase))
        {
            lookup[group.Key] = group
                .Select(row => row.Column)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return lookup;
    }

    private static TextSearchTargetValidation BuildTextSearchTargetValidation(
        TextSearchTargetOptions target,
        IReadOnlyDictionary<string, HashSet<string>> columnLookup)
    {
        var identifierConfigOk = IsValidTextSearchTarget(target);
        var configuredColumns = GetConfiguredTextSearchColumns(target).ToArray();
        var targetKey = BuildTargetKey(target.Schema, target.Table);
        var tableExists = columnLookup.TryGetValue(targetKey, out var existingColumns);
        var missingColumns = identifierConfigOk && tableExists
            ? configuredColumns
                .Where(column => !existingColumns!.Contains(column))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : configuredColumns;
        var ok = identifierConfigOk && tableExists && missingColumns.Length == 0;

        var hint = ok
            ? null
            : !identifierConfigOk
                ? "Check textSearch target identifiers; only simple schema, table, and column names are allowed."
                : !tableExists
                    ? "Check that the configured table or view exists in the target database."
                    : "Check configured textSearch column names against the target table or view.";

        return new TextSearchTargetValidation(
            target.Profile,
            target.Schema,
            target.Table,
            target.TextColumn,
            ok,
            tableExists,
            identifierConfigOk,
            configuredColumns,
            missingColumns,
            hint);
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

    private static bool IsValidTextSearchTarget(TextSearchTargetOptions target)
    {
        return !string.IsNullOrWhiteSpace(target.Schema)
               && !string.IsNullOrWhiteSpace(target.Table)
               && !string.IsNullOrWhiteSpace(target.TextColumn)
               && IsSafeIdentifier(target.Schema)
               && IsSafeIdentifier(target.Table)
               && IsSafeIdentifier(target.TextColumn)
               && (string.IsNullOrWhiteSpace(target.KeyColumn) || IsSafeIdentifier(target.KeyColumn))
               && (string.IsNullOrWhiteSpace(target.NameColumn) || IsSafeIdentifier(target.NameColumn))
               && target.LabelColumns.All(IsSafeIdentifier)
               && (string.IsNullOrWhiteSpace(target.CreatedAtColumn) || IsSafeIdentifier(target.CreatedAtColumn))
               && (string.IsNullOrWhiteSpace(target.UpdatedAtColumn) || IsSafeIdentifier(target.UpdatedAtColumn))
               && (string.IsNullOrWhiteSpace(target.CreatedByColumn) || IsSafeIdentifier(target.CreatedByColumn))
               && (string.IsNullOrWhiteSpace(target.UpdatedByColumn) || IsSafeIdentifier(target.UpdatedByColumn))
               && (string.IsNullOrWhiteSpace(target.ContentKindColumn) || IsSafeIdentifier(target.ContentKindColumn));
    }

    private static bool IsSafeIdentifier(string value)
    {
        return value.Length is > 0 and <= 128
               && value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '#');
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string BuildNullableTextColumnExpression(string? column)
    {
        return string.IsNullOrWhiteSpace(column)
            ? "CONVERT(NVARCHAR(4000), NULL)"
            : $"CONVERT(NVARCHAR(4000), {QuoteIdentifier(column!)})";
    }

    private static string BuildOptionalSelectList(IReadOnlyList<string> selectItems)
    {
        return selectItems.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                selectItems.Select(item => $"                           {item},"));
    }

    internal static string? FindUsableColumn(
        TextSearchTargetOptions target,
        IReadOnlyDictionary<string, HashSet<string>> columnLookup)
    {
        if (!columnLookup.TryGetValue(BuildTargetKey(target.Schema, target.Table), out var columns))
        {
            return null;
        }

        return UsableColumnCandidates.FirstOrDefault(columns.Contains);
    }

    private static string BuildUsableStateExpression(string? usableColumn)
    {
        if (string.IsNullOrWhiteSpace(usableColumn))
        {
            return "CONVERT(INT, NULL)";
        }

        var value = BuildNullableTextColumnExpression(usableColumn);
        var normalized = $"UPPER(LTRIM(RTRIM({value})))";
        return $"""
               CASE
                   WHEN TRY_CONVERT(INT, {value}) = 1 THEN 1
                   WHEN {normalized} IN (N'TRUE', N'Y', N'YES', N'ON', N'ENABLE', N'ENABLED', N'ACTIVE', N'是', N'启用', N'有效') THEN 1
                   WHEN TRY_CONVERT(INT, {value}) = 0 THEN 0
                   WHEN {normalized} IN (N'FALSE', N'N', N'NO', N'OFF', N'DISABLE', N'DISABLED', N'INACTIVE', N'否', N'停用', N'无效') THEN 0
                   ELSE NULL
               END
               """;
    }

    private static string BuildTextSearchOrderBy(TextSearchTargetOptions target, string? usableColumn)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(usableColumn))
        {
            parts.Add("usable_state DESC");
        }

        if (!string.IsNullOrWhiteSpace(target.NameColumn))
        {
            parts.Add(QuoteIdentifier(target.NameColumn!));
            return string.Join(", ", parts);
        }

        if (!string.IsNullOrWhiteSpace(target.KeyColumn))
        {
            parts.Add(QuoteIdentifier(target.KeyColumn!));
            return string.Join(", ", parts);
        }

        if (!string.IsNullOrWhiteSpace(target.UpdatedAtColumn))
        {
            parts.Add($"{QuoteIdentifier(target.UpdatedAtColumn!)} DESC");
            return string.Join(", ", parts);
        }

        parts.Add("(SELECT NULL)");
        return string.Join(", ", parts);
    }

    private static object BuildTextSearchTargetSummary(TextSearchTargetOptions target, string? usableColumn)
    {
        return new
        {
            target.Profile,
            target.Schema,
            target.Table,
            target.TextColumn,
            target.KeyColumn,
            target.NameColumn,
            target.LabelColumns,
            target.CreatedAtColumn,
            target.UpdatedAtColumn,
            target.CreatedByColumn,
            target.UpdatedByColumn,
            target.ContentKind,
            target.ContentKindColumn,
            usableColumn,
            searchableColumns = BuildTextSearchMatchColumnNames(target)
        };
    }

    private static TextSearchQueryColumn[] BuildTextSearchQueryColumns(
        TextSearchTargetOptions target,
        string textExpression,
        string keyExpression,
        string nameExpression)
    {
        var columns = new List<TextSearchQueryColumn>
        {
            new(target.TextColumn, "text", textExpression)
        };

        if (!string.IsNullOrWhiteSpace(target.NameColumn))
        {
            columns.Add(new(target.NameColumn!, "name", nameExpression));
        }

        foreach (var column in target.LabelColumns)
        {
            columns.Add(new(column, "label", BuildNullableTextColumnExpression(column)));
        }

        if (!string.IsNullOrWhiteSpace(target.KeyColumn))
        {
            columns.Add(new(target.KeyColumn!, "key", keyExpression));
        }

        return columns
            .GroupBy(column => column.Column, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string[] BuildTextSearchMatchColumnNames(TextSearchTargetOptions target)
    {
        return BuildTextSearchQueryColumns(
                target,
                QuoteIdentifier(target.TextColumn),
                string.IsNullOrWhiteSpace(target.KeyColumn) ? string.Empty : QuoteIdentifier(target.KeyColumn!),
                string.IsNullOrWhiteSpace(target.NameColumn) ? string.Empty : QuoteIdentifier(target.NameColumn!))
            .Select(column => column.Column)
            .ToArray();
    }

    private static TextSearchLabelValue[] BuildTextSearchLabelValues(
        SqlDataReader reader,
        IReadOnlyList<string> labelColumns)
    {
        return labelColumns
            .Select((column, index) => new TextSearchLabelValue(column, reader.GetNullableString($"label_{index}")))
            .ToArray();
    }

    private static object[] BuildTextSearchLabels(IReadOnlyList<TextSearchLabelValue> labels)
    {
        return labels
            .Where(label => !string.IsNullOrWhiteSpace(label.Value))
            .Select(label => new
            {
                column = label.Column,
                value = label.Value
            })
            .Cast<object>()
            .ToArray();
    }

    private static TextSearchMatchInput[] BuildTextSearchMatchInputs(
        TextSearchTargetOptions target,
        string? keyValue,
        string? nameValue,
        IReadOnlyList<TextSearchLabelValue> labels,
        string textValue)
    {
        var inputs = new List<TextSearchMatchInput>
        {
            new(target.TextColumn, "text", textValue)
        };

        if (!string.IsNullOrWhiteSpace(target.NameColumn))
        {
            inputs.Add(new(target.NameColumn!, "name", nameValue));
        }

        inputs.AddRange(labels.Select(label => new TextSearchMatchInput(label.Column, "label", label.Value)));

        if (!string.IsNullOrWhiteSpace(target.KeyColumn))
        {
            inputs.Add(new(target.KeyColumn!, "key", keyValue));
        }

        return inputs.ToArray();
    }

    internal static TextSearchMatch? FindTextSearchMatch(
        IReadOnlyList<string> terms,
        IReadOnlyList<TextSearchMatchInput> inputs)
    {
        foreach (var term in terms.Where(term => !string.IsNullOrWhiteSpace(term)))
        {
            foreach (var input in inputs)
            {
                if (string.IsNullOrEmpty(input.Value))
                {
                    continue;
                }

                var index = input.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return new TextSearchMatch(
                        input.Column,
                        input.Kind,
                        term,
                        index + 1,
                        term.Length,
                        input.Value);
                }
            }
        }

        return null;
    }

    internal static string BuildTextSearchSnippet(string text, int matchStart, int matchLength, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalizedMaxLength = Math.Clamp(maxLength, 20, 4000);
        var zeroBasedStart = Math.Clamp(matchStart - 1, 0, text.Length - 1);
        var before = normalizedMaxLength / 3;
        var start = Math.Max(0, zeroBasedStart - before);
        var length = Math.Min(normalizedMaxLength, text.Length - start);
        var snippet = text.Substring(start, length);
        return NormalizeSnippet(snippet);
    }

    internal static object BuildContentKindInfo(
        string? configuredKind,
        string? contentKindColumn,
        string? contentKindValue,
        string textSample)
    {
        var configured = NullIfWhiteSpace(configuredKind);
        var columnValue = NullIfWhiteSpace(contentKindValue);
        var detected = DetectContentKind(textSample);
        return new
        {
            effective = configured ?? columnValue ?? detected,
            configured,
            column = NullIfWhiteSpace(contentKindColumn),
            columnValue,
            detected
        };
    }

    internal static string DetectContentKind(string text)
    {
        var sample = text.Trim();
        if (sample.Length == 0)
        {
            return "text";
        }

        if ((sample.StartsWith('{') && sample.EndsWith('}'))
            || (sample.StartsWith('[') && sample.EndsWith(']')))
        {
            return "json";
        }

        if (sample.StartsWith('<'))
        {
            return sample.Contains("<html", StringComparison.OrdinalIgnoreCase)
                ? "html"
                : "xml";
        }

        if (Regex.IsMatch(sample, @"\b(SELECT|WITH|INSERT|UPDATE|DELETE|MERGE|EXEC|FROM|JOIN|WHERE)\b", RegexOptions.IgnoreCase))
        {
            return "sql";
        }

        if (Regex.IsMatch(sample, @"\b(function|const|let|var|return|async|await)\b|=>|\$\(", RegexOptions.IgnoreCase))
        {
            return "javascript";
        }

        return "text";
    }

    private static string? NullIfWhiteSpace(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IEnumerable<string> GetConfiguredTextSearchColumns(TextSearchTargetOptions target)
    {
        yield return target.TextColumn;
        foreach (var column in new[]
                 {
                     target.KeyColumn,
                     target.NameColumn,
                     target.CreatedAtColumn,
                     target.UpdatedAtColumn,
                     target.CreatedByColumn,
                     target.UpdatedByColumn,
                     target.ContentKindColumn
                 })
        {
            if (!string.IsNullOrWhiteSpace(column))
            {
                yield return column!;
            }
        }

        foreach (var column in target.LabelColumns)
        {
            yield return column;
        }
    }

    private static string BuildTargetKey(string schema, string table)
    {
        return $"{schema}.{table}";
    }

    private static string NormalizeSnippet(string text)
    {
        return string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ComputeSha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
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

    internal static ShowplanSummary SummarizeShowplanXml(IReadOnlyList<string> plans)
    {
        var statementCount = 0;
        var estimatedTotalSubtreeCost = 0m;
        var operators = new List<ShowplanOperatorSummary>();
        var missingIndexes = new List<ShowplanMissingIndex>();
        var warnings = new List<ShowplanWarningSummary>();
        var statements = new List<ShowplanStatementSummary>();
        var memoryGrants = new List<ShowplanMemoryGrantNode>();
        var parseErrors = new List<string>();
        var implicitConversionCount = 0;

        foreach (var plan in plans.Where(plan => !string.IsNullOrWhiteSpace(plan)))
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(plan, LoadOptions.None);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
            {
                parseErrors.Add(ex.Message);
                continue;
            }

            if (document.Root is null)
            {
                parseErrors.Add("Showplan XML document has no root element.");
                continue;
            }

            var statementNodes = DescendantsByLocalName(document.Root, "StmtSimple").ToArray();
            statementCount += statementNodes.Length == 0 ? 1 : statementNodes.Length;
            foreach (var statement in statementNodes)
            {
                estimatedTotalSubtreeCost += ReadDecimalAttribute(statement, "StatementSubTreeCost") ?? 0m;
            }

            statements.AddRange(ReadShowplanStatements(document.Root, statements.Count + 1));
            memoryGrants.AddRange(DescendantsByLocalName(document.Root, "MemoryGrantInfo").Select(ReadShowplanMemoryGrant));
            var rootOperators = DescendantsByLocalName(document.Root, "RelOp")
                .Select(ReadShowplanOperator)
                .ToArray();
            operators.AddRange(rootOperators);
            missingIndexes.AddRange(ReadMissingIndexes(document.Root));
            warnings.AddRange(ReadShowplanWarnings(document.Root));
            implicitConversionCount += Math.Max(
                DescendantsByLocalName(document.Root, "PlanAffectingConvert").Count(),
                DescendantsByLocalName(document.Root, "ScalarOperator")
                    .Count(element => (ReadAttribute(element, "ScalarString") ?? string.Empty)
                        .Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase)));
        }

        if (statementCount == 0 && parseErrors.Count == 0)
        {
            statementCount = plans.Count;
        }

        var counts = new ShowplanOperatorCounts(
            operators.Count(operatorSummary => IsScanOperator(operatorSummary.PhysicalOp)),
            operators.Count(operatorSummary => ContainsOperator(operatorSummary.PhysicalOp, "Key Lookup")),
            operators.Count(operatorSummary => ContainsOperator(operatorSummary.PhysicalOp, "Sort")),
            operators.Count(operatorSummary => ContainsOperator(operatorSummary.PhysicalOp, "Hash Match")),
            operators.Count(operatorSummary => ContainsOperator(operatorSummary.PhysicalOp, "Parallelism")),
            missingIndexes.Count,
            implicitConversionCount,
            warnings.Count);
        var warningCounts = BuildShowplanWarningCounts(warnings);
        var memoryGrantSummary = BuildShowplanMemoryGrantSummary(memoryGrants);

        return new ShowplanSummary(
            statementCount,
            estimatedTotalSubtreeCost,
            counts,
            BuildShowplanRisks(counts, warningCounts, memoryGrantSummary, statements),
            statements
                .OrderByDescending(statement => statement.EstimatedSubtreeCost ?? 0m)
                .ThenBy(statement => statement.Ordinal)
                .Take(10)
                .ToArray(),
            memoryGrantSummary,
            warningCounts,
            operators
                .Where(operatorSummary => operatorSummary.EstimatedSubtreeCost is not null)
                .OrderByDescending(operatorSummary => operatorSummary.EstimatedSubtreeCost)
                .ThenBy(operatorSummary => operatorSummary.NodeId)
                .Take(10)
                .ToArray(),
            missingIndexes
                .OrderByDescending(index => index.Impact)
                .Take(10)
                .ToArray(),
            warnings.Take(20).ToArray(),
            parseErrors.ToArray());
    }

    private static ShowplanRisk[] BuildShowplanRisks(
        ShowplanOperatorCounts counts,
        ShowplanWarningCounts warningCounts,
        ShowplanMemoryGrantSummary memoryGrant,
        IReadOnlyList<ShowplanStatementSummary> statements)
    {
        var risks = new List<ShowplanRisk>();

        if (counts.MissingIndexCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "missing_index",
                "high",
                $"Plan reports {counts.MissingIndexCount} missing index suggestion(s).",
                "Review the suggested keys/includes against workload and write cost before creating indexes."));
        }

        if (counts.ImplicitConversionCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "implicit_conversion",
                "high",
                $"Plan reports {counts.ImplicitConversionCount} plan-affecting implicit conversion(s).",
                "Check mismatched parameter, variable, and column data types; conversions on indexed columns can block seeks."));
        }

        if (counts.ScanCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "scan",
                "medium",
                $"Plan contains {counts.ScanCount} scan operator(s).",
                "Validate expected selectivity; add predicates or indexes only when the scan is not intentional."));
        }

        if (counts.KeyLookupCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "key_lookup",
                "medium",
                $"Plan contains {counts.KeyLookupCount} key lookup operator(s).",
                "If lookups run many times, consider covering needed columns in an existing index."));
        }

        if (counts.SortCount + counts.HashMatchCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "sort_or_hash",
                "medium",
                $"Plan contains {counts.SortCount} sort and {counts.HashMatchCount} hash match operator(s).",
                "Check memory grant, row estimates, and whether join/order keys can be supported by indexes."));
        }

        if (counts.WarningCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "warning",
                "medium",
                $"Plan contains {counts.WarningCount} warning node(s).",
                "Inspect warnings for spills, no-join-predicate, cardinality, or conversion issues."));
        }

        if (warningCounts.SpillToTempDbCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "spill_to_tempdb",
                "high",
                $"Plan reports {warningCounts.SpillToTempDbCount} spill-to-tempdb warning(s).",
                "Check memory grant, row estimates, sort/hash operators, and whether supporting indexes can reduce spill risk."));
        }

        if (warningCounts.NoJoinPredicateCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "no_join_predicate",
                "high",
                $"Plan reports {warningCounts.NoJoinPredicateCount} no-join-predicate warning(s).",
                "Verify join conditions; accidental cross joins can explode row counts."));
        }

        var earlyAbortCount = statements.Count(statement => !string.IsNullOrWhiteSpace(statement.OptimizationEarlyAbortReason));
        if (earlyAbortCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "optimizer_early_abort",
                "medium",
                $"Plan reports {earlyAbortCount} statement(s) with optimizer early-abort reason.",
                "Inspect optimizationEarlyAbortReason; timeout or memory-limit reasons can mean the chosen plan is not fully optimized."));
        }

        if ((memoryGrant.MaxSerialDesiredMemoryKb ?? 0) >= 102_400
            || (memoryGrant.MaxDesiredMemoryKb ?? 0) >= 102_400
            || (memoryGrant.MaxRequestedMemoryKb ?? 0) >= 102_400)
        {
            risks.Add(new ShowplanRisk(
                "large_memory_grant",
                "medium",
                "Plan has a large estimated memory grant.",
                "Review sort/hash operators, estimated rows, and available indexes; large grants can reduce concurrency."));
        }

        if (counts.ParallelismCount > 0)
        {
            risks.Add(new ShowplanRisk(
                "parallelism",
                "info",
                $"Plan contains {counts.ParallelismCount} parallelism operator(s).",
                "Parallelism is not necessarily bad; review only if the query is unexpectedly expensive or blocking."));
        }

        return risks.ToArray();
    }

    private static IEnumerable<ShowplanStatementSummary> ReadShowplanStatements(XElement root, int startingOrdinal)
    {
        var ordinal = startingOrdinal;
        foreach (var statement in DescendantsByLocalName(root, "StmtSimple"))
        {
            yield return new ShowplanStatementSummary(
                ordinal++,
                TruncateShowplanText(ReadAttribute(statement, "StatementText"), 500),
                ReadAttribute(statement, "StatementType"),
                ReadDecimalAttribute(statement, "StatementSubTreeCost"),
                ReadDecimalAttribute(statement, "StatementEstRows"),
                ReadAttribute(statement, "StatementOptmLevel"),
                ReadAttribute(statement, "StatementOptmEarlyAbortReason"),
                ReadAttribute(statement, "NonParallelPlanReason"),
                ReadAttribute(statement, "CardinalityEstimationModelVersion"));
        }
    }

    private static ShowplanMemoryGrantNode ReadShowplanMemoryGrant(XElement element)
    {
        return new ShowplanMemoryGrantNode(
            ReadLongAttribute(element, "SerialRequiredMemory"),
            ReadLongAttribute(element, "SerialDesiredMemory"),
            ReadLongAttribute(element, "RequiredMemory"),
            ReadLongAttribute(element, "DesiredMemory"),
            ReadLongAttribute(element, "RequestedMemory"),
            ReadLongAttribute(element, "GrantedMemory"),
            ReadLongAttribute(element, "MaxUsedMemory"),
            ReadAttribute(element, "IsMemoryGrantFeedbackAdjusted"));
    }

    private static ShowplanMemoryGrantSummary BuildShowplanMemoryGrantSummary(
        IReadOnlyList<ShowplanMemoryGrantNode> grants)
    {
        return new ShowplanMemoryGrantSummary(
            grants.Count,
            MaxOrNull(grants.Select(grant => grant.SerialRequiredMemoryKb)),
            MaxOrNull(grants.Select(grant => grant.SerialDesiredMemoryKb)),
            MaxOrNull(grants.Select(grant => grant.RequiredMemoryKb)),
            MaxOrNull(grants.Select(grant => grant.DesiredMemoryKb)),
            MaxOrNull(grants.Select(grant => grant.RequestedMemoryKb)),
            MaxOrNull(grants.Select(grant => grant.GrantedMemoryKb)),
            MaxOrNull(grants.Select(grant => grant.MaxUsedMemoryKb)),
            grants
                .Select(grant => grant.IsMemoryGrantFeedbackAdjusted)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static ShowplanWarningCounts BuildShowplanWarningCounts(IReadOnlyList<ShowplanWarningSummary> warnings)
    {
        return new ShowplanWarningCounts(
            warnings.Sum(warning => CountWarningDetails(warning, "SpillToTempDb")),
            warnings.Sum(warning => CountWarningDetails(warning, "PlanAffectingConvert")),
            warnings.Sum(warning => CountWarningDetails(warning, "ColumnsWithNoStatistics")),
            warnings.Sum(warning => warning.Details.Count(detail => detail.Contains("NoJoinPredicate", StringComparison.OrdinalIgnoreCase))));
    }

    private static int CountWarningDetails(ShowplanWarningSummary warning, string token)
    {
        return warning.Details.Count(detail => detail.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static long? MaxOrNull(IEnumerable<long?> values)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static string? TruncateShowplanText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return text.Length <= maxLength
            ? text
            : string.Concat(text.AsSpan(0, maxLength), "...<truncated>");
    }

    private static ShowplanOperatorSummary ReadShowplanOperator(XElement element)
    {
        var objectRef = ReadShowplanObject(element);
        return new ShowplanOperatorSummary(
            ReadIntAttribute(element, "NodeId"),
            ReadAttribute(element, "PhysicalOp"),
            ReadAttribute(element, "LogicalOp"),
            ReadDecimalAttribute(element, "EstimatedTotalSubtreeCost"),
            ReadDecimalAttribute(element, "EstimateRows"),
            objectRef);
    }

    private static ShowplanObjectReference? ReadShowplanObject(XElement element)
    {
        var objectElement = DescendantsByLocalName(element, "Object").FirstOrDefault();
        if (objectElement is null)
        {
            return null;
        }

        var database = ReadAttribute(objectElement, "Database");
        var schema = ReadAttribute(objectElement, "Schema");
        var table = ReadAttribute(objectElement, "Table");
        var index = ReadAttribute(objectElement, "Index");
        return database is null && schema is null && table is null && index is null
            ? null
            : new ShowplanObjectReference(database, schema, table, index);
    }

    private static IEnumerable<ShowplanMissingIndex> ReadMissingIndexes(XElement root)
    {
        foreach (var missingIndex in DescendantsByLocalName(root, "MissingIndex"))
        {
            var group = missingIndex.Ancestors()
                .FirstOrDefault(element => element.Name.LocalName.Equals("MissingIndexGroup", StringComparison.Ordinal));
            yield return new ShowplanMissingIndex(
                ReadAttribute(missingIndex, "Database"),
                ReadAttribute(missingIndex, "Schema"),
                ReadAttribute(missingIndex, "Table"),
                group is null ? null : ReadDecimalAttribute(group, "Impact"),
                ReadMissingIndexColumns(missingIndex, "EQUALITY"),
                ReadMissingIndexColumns(missingIndex, "INEQUALITY"),
                ReadMissingIndexColumns(missingIndex, "INCLUDE"));
        }
    }

    private static string[] ReadMissingIndexColumns(XElement missingIndex, string usage)
    {
        return DescendantsByLocalName(missingIndex, "ColumnGroup")
            .Where(group => string.Equals(ReadAttribute(group, "Usage"), usage, StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => DescendantsByLocalName(group, "Column"))
            .Select(column => ReadAttribute(column, "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<ShowplanWarningSummary> ReadShowplanWarnings(XElement root)
    {
        foreach (var warning in DescendantsByLocalName(root, "Warnings"))
        {
            var details = new List<string>();
            details.AddRange(warning.Attributes()
                .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Value))
                .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}"));

            details.AddRange(warning.Descendants()
                .Where(element => element.Name.LocalName is "PlanAffectingConvert" or "SpillToTempDb" or "ColumnsWithNoStatistics")
                .Select(ReadWarningDetail));

            var relOp = warning.Ancestors()
                .FirstOrDefault(element => element.Name.LocalName.Equals("RelOp", StringComparison.Ordinal));
            yield return new ShowplanWarningSummary(
                relOp is null ? null : ReadIntAttribute(relOp, "NodeId"),
                relOp is null ? null : ReadAttribute(relOp, "PhysicalOp"),
                details.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray());
        }
    }

    private static string ReadWarningDetail(XElement element)
    {
        var attributes = string.Join(
            ", ",
            element.Attributes()
                .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Value))
                .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}"));
        return string.IsNullOrWhiteSpace(attributes)
            ? element.Name.LocalName
            : $"{element.Name.LocalName}: {attributes}";
    }

    private static IEnumerable<XElement> DescendantsByLocalName(XContainer root, string localName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName.Equals(localName, StringComparison.Ordinal));
    }

    private static string? ReadAttribute(XElement element, string attributeName)
    {
        return element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(attributeName, StringComparison.Ordinal))
            ?.Value;
    }

    private static int? ReadIntAttribute(XElement element, string attributeName)
    {
        return int.TryParse(
            ReadAttribute(element, attributeName),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static decimal? ReadDecimalAttribute(XElement element, string attributeName)
    {
        return decimal.TryParse(
            ReadAttribute(element, attributeName),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static long? ReadLongAttribute(XElement element, string attributeName)
    {
        return long.TryParse(
            ReadAttribute(element, attributeName),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static bool IsScanOperator(string? physicalOp)
    {
        return ContainsOperator(physicalOp, "Scan")
               && !ContainsOperator(physicalOp, "Constant Scan");
    }

    private static bool ContainsOperator(string? physicalOp, string token)
    {
        return physicalOp?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
    }

    internal sealed record ShowplanSummary(
        int StatementCount,
        decimal EstimatedTotalSubtreeCost,
        ShowplanOperatorCounts OperatorCounts,
        ShowplanRisk[] Risks,
        ShowplanStatementSummary[] Statements,
        ShowplanMemoryGrantSummary MemoryGrant,
        ShowplanWarningCounts WarningCounts,
        ShowplanOperatorSummary[] ExpensiveOperators,
        ShowplanMissingIndex[] MissingIndexes,
        ShowplanWarningSummary[] Warnings,
        string[] ParseErrors);

    internal sealed record ShowplanOperatorCounts(
        int ScanCount,
        int KeyLookupCount,
        int SortCount,
        int HashMatchCount,
        int ParallelismCount,
        int MissingIndexCount,
        int ImplicitConversionCount,
        int WarningCount);

    internal sealed record ShowplanRisk(
        string Code,
        string Severity,
        string Message,
        string Hint);

    internal sealed record ShowplanStatementSummary(
        int Ordinal,
        string? StatementText,
        string? StatementType,
        decimal? EstimatedSubtreeCost,
        decimal? EstimatedRows,
        string? OptimizationLevel,
        string? OptimizationEarlyAbortReason,
        string? NonParallelPlanReason,
        string? CardinalityEstimationModelVersion);

    internal sealed record ShowplanMemoryGrantSummary(
        int GrantNodeCount,
        long? MaxSerialRequiredMemoryKb,
        long? MaxSerialDesiredMemoryKb,
        long? MaxRequiredMemoryKb,
        long? MaxDesiredMemoryKb,
        long? MaxRequestedMemoryKb,
        long? MaxGrantedMemoryKb,
        long? MaxUsedMemoryKb,
        string[] FeedbackAdjustments);

    private sealed record ShowplanMemoryGrantNode(
        long? SerialRequiredMemoryKb,
        long? SerialDesiredMemoryKb,
        long? RequiredMemoryKb,
        long? DesiredMemoryKb,
        long? RequestedMemoryKb,
        long? GrantedMemoryKb,
        long? MaxUsedMemoryKb,
        string? IsMemoryGrantFeedbackAdjusted);

    internal sealed record ShowplanWarningCounts(
        int SpillToTempDbCount,
        int PlanAffectingConvertCount,
        int ColumnsWithNoStatisticsCount,
        int NoJoinPredicateCount);

    internal sealed record ShowplanOperatorSummary(
        int? NodeId,
        string? PhysicalOp,
        string? LogicalOp,
        decimal? EstimatedSubtreeCost,
        decimal? EstimatedRows,
        ShowplanObjectReference? Object);

    internal sealed record ShowplanObjectReference(
        string? Database,
        string? Schema,
        string? Table,
        string? Index);

    internal sealed record ShowplanMissingIndex(
        string? Database,
        string? Schema,
        string? Table,
        decimal? Impact,
        string[] EqualityColumns,
        string[] InequalityColumns,
        string[] IncludeColumns);

    internal sealed record ShowplanWarningSummary(
        int? NodeId,
        string? PhysicalOp,
        string[] Details);

    private sealed record TextSearchTargetValidation(
        string Profile,
        string Schema,
        string Table,
        string TextColumn,
        bool Ok,
        bool TableExists,
        bool IdentifierConfigOk,
        string[] ConfiguredColumns,
        string[] MissingColumns,
        string? Hint);

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

    internal sealed record ModuleDefinitionSlice(
        string Definition,
        int TotalLines,
        int? StartLine,
        int? EndLine,
        int SelectedLineCount,
        bool IsPartial,
        bool Truncated,
        string Reason,
        int ContextLines,
        int[] MatchedLines,
        ModuleDefinitionLine[] Lines);

    internal sealed record ModuleDefinitionLine(int LineNumber, string Text);

    private sealed record ModuleDefinitionInfo(
        string Schema,
        string Name,
        string Type,
        string TypeDesc,
        DateTime CreateDate,
        DateTime ModifyDate,
        string Definition);

    private sealed record ModuleCompareDatabaseInfo(
        DateTime CreateDate,
        DateTime ModifyDate,
        int DefinitionLength,
        int LineCount,
        string Sha256,
        string NormalizedSha256,
        string SqlNormalizedSha256);

    private sealed record ModuleCompareFileInfo(
        string Path,
        long LengthBytes,
        DateTime LastWriteTime,
        int TextLength,
        int LineCount,
        string Sha256,
        string NormalizedSha256,
        string SqlNormalizedSha256);

    private sealed record ModuleFileComparisonResult(
        string Schema,
        string Name,
        string Type,
        string TypeDesc,
        ModuleCompareDatabaseInfo Database,
        ModuleCompareFileInfo File,
        bool ExactMatch,
        bool NormalizedMatch,
        bool SqlNormalizedMatch,
        ModuleFileDiff Diff,
        string[] NextActions);

    internal sealed record ModuleSqlFileDiscovery(
        string Root,
        string[] Patterns,
        int ScannedFileCount,
        bool SearchTruncated,
        int CandidateCount,
        bool CandidateListTruncated,
        ModuleSqlFileCandidate[] Candidates,
        ModuleSqlFileCandidate? SelectedCandidate,
        bool Ambiguous,
        string? Hint);

    internal sealed record ModuleSqlFileCandidate(
        string Path,
        string RelativePath,
        string FileName,
        long LengthBytes,
        DateTime LastWriteTime,
        int Score,
        string[] Reasons);

    internal sealed record ModuleFileDiff(
        bool Equal,
        int? FirstDifferentLine,
        int DatabaseChangedLineCount,
        int FileChangedLineCount,
        bool Truncated,
        int OmittedHunkCount,
        ModuleFileDiffHunk[] Hunks);

    internal sealed record ModuleFileDiffHunk(
        int DatabaseStartLine,
        int DatabaseEndLine,
        int FileStartLine,
        int FileEndLine,
        bool DatabaseLinesTruncated,
        bool FileLinesTruncated,
        ModuleFileDiffLine[] DatabaseLines,
        ModuleFileDiffLine[] FileLines);

    internal sealed record ModuleFileDiffLine(int LineNumber, string Text, bool Changed);

    private sealed record DiffChangeSegment(
        int DatabaseStartIndex,
        int DatabaseEndIndex,
        int FileStartIndex,
        int FileEndIndex)
    {
        public int DatabaseStartLine => DatabaseStartIndex + 1;
        public int DatabaseEndLine => DatabaseEndIndex + 1;
        public int FileStartLine => FileStartIndex + 1;
        public int FileEndLine => FileEndIndex + 1;
        public int DatabaseChangedLineCount => Math.Max(0, DatabaseEndIndex - DatabaseStartIndex + 1);
        public int FileChangedLineCount => Math.Max(0, FileEndIndex - FileStartIndex + 1);
        public int DatabaseDisplayLine => DatabaseChangedLineCount > 0 ? DatabaseStartLine : Math.Max(1, DatabaseStartLine - 1);
        public int FileDisplayLine => FileChangedLineCount > 0 ? FileStartLine : Math.Max(1, FileStartLine - 1);
    }

    internal sealed record TempTableAnalysis(TempTableInfo[] TempTables);

    internal sealed record TempTableInfo(
        string Name,
        int FirstLine,
        TempTableCreation[] Creations,
        TempTableColumn[] Columns,
        TempTableReference[] References,
        IReadOnlyDictionary<string, int> OperationCounts,
        TempTableColumnFlow[] ColumnFlow);

    internal sealed record TempTableCreation(
        int LineNumber,
        string Operation,
        string Text,
        int EndLine,
        string[] Columns);

    internal sealed record TempTableColumn(int LineNumber, string Name, string Definition);

    internal sealed record TempTableReference(
        int LineNumber,
        string Operation,
        string Text,
        int EndLine,
        string[] Columns);

    internal sealed record TempTableColumnFlow(
        string Column,
        IReadOnlyDictionary<string, int> OperationCounts,
        int[] Lines);

    private sealed record TempTableColumnFlowEvent(string Column, string Operation, int LineNumber);

    private sealed record TempTableStatement(
        int StartLine,
        int EndLine,
        string Text,
        string CompactText);

    private sealed class TempTableBuilder
    {
        public TempTableBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int FirstLine { get; private set; }

        public List<TempTableCreation> Creations { get; } = [];

        public List<TempTableColumn> Columns { get; } = [];

        public List<TempTableReference> References { get; } = [];

        public List<TempTableColumnFlowEvent> ColumnFlowEvents { get; } = [];

        public void SetFirstLine(int lineNumber)
        {
            if (FirstLine == 0 || lineNumber < FirstLine)
            {
                FirstLine = lineNumber;
            }
        }
    }

    internal sealed record UserSqlParameterSpec(
        string Name,
        object? Value,
        SqlDbType DbType,
        int? Size,
        string Definition);

    internal sealed record SqlTemplateApplication(
        string Sql,
        SqlTemplateReplacement[] Replacements);

    internal sealed record SqlTemplateReplacement(
        string Placeholder,
        bool Applied,
        int Occurrences,
        int ValueLength);

    private sealed class QueryValueTruncationInfo
    {
        public int TextValuesTruncated { get; set; }

        public HashSet<string> ColumnsWithTruncatedText { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record QueryResultColumnMetadata(
        int? ColumnOrdinal,
        string? Name,
        int? IsNullable,
        string? SystemTypeName,
        int? SystemTypeId,
        int? MaxLength,
        int? Precision,
        int? Scale,
        string? CollationName,
        int? ErrorNumber,
        int? ErrorSeverity,
        int? ErrorState,
        string? ErrorMessage);

    private sealed record TextSearchQueryColumn(string Column, string Kind, string Expression);

    private sealed record TextSearchLabelValue(string Column, string? Value);

    internal sealed record TextSearchMatchInput(string Column, string Kind, string? Value);

    internal sealed record TextSearchMatch(
        string Column,
        string Kind,
        string Term,
        int Start,
        int Length,
        string Value);

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
