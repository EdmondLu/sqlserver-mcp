using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Sql;

public sealed class ReadonlySqlGuard
{
    private static readonly HashSet<string> AllowedDatabaseScopedDmvs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys.dm_db_partition_stats"
    };

    private static readonly HashSet<string> AllowedReadOnlyMetadataFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys.dm_exec_describe_first_result_set"
    };

    private readonly SqlServerMcpOptions _options;

    public ReadonlySqlGuard(SqlServerMcpOptions options)
    {
        _options = options;
    }

    public void ValidateReadonlyQuery(string sql)
    {
        ValidateReadonlySelect(sql, "Only one read-only SELECT or WITH CTE query is supported.");
    }

    public void ValidateShowplanQuery(string sql)
    {
        ValidateReadonlySelect(sql, "Only one read-only SELECT or WITH CTE query can be explained.");
    }

    public void ValidateReadonlyBatch(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SqlMcpException(ErrorCodes.SqlParseFailed, "SQL batch is required.");
        }

        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
        {
            var parseErrors = errors.Select(ToParseErrorDetail).ToArray();
            throw new SqlMcpException(
                ErrorCodes.SqlParseFailed,
                "SQL batch parse failed.",
                string.Join("; ", parseErrors.Select(error => error.Summary)),
                "Use one batch containing only local variables, local #temp state, and read-only SELECT statements.",
                errorDetails: new
                {
                    stage = "parse",
                    reasons = parseErrors.Select(error => new
                    {
                        error.Line,
                        error.Column,
                        error.Message
                    }).ToArray()
                });
        }

        if (fragment is not TSqlScript script)
        {
            throw new SqlMcpException(ErrorCodes.SqlParseFailed, "SQL did not parse as a T-SQL script.");
        }

        if (script.Batches.Count != 1)
        {
            throw GuardRejected(
                [$"Batch separator count produced {script.Batches.Count} batches; exactly one executable batch is required."],
                script.Batches.Sum(batch => batch.Statements.Count));
        }

        var statements = script.Batches[0].Statements.ToArray();
        if (statements.Length == 0 || statements.Length > 50)
        {
            throw GuardRejected(
                [$"Statement count {statements.Length} is outside the allowed range 1..50."],
                statements.Length);
        }

        if (!statements.Any(statement => statement is SelectStatement))
        {
            throw GuardRejected(
                ["At least one SELECT result set is required."],
                statements.Length);
        }

        var batchVisitor = new ReadonlyBatchVisitor();
        fragment.Accept(batchVisitor);
        var objectVisitor = new GuardVisitor(_options.Security);
        fragment.Accept(objectVisitor);
        var allErrors = batchVisitor.Errors.Concat(objectVisitor.Errors).Distinct().ToArray();
        if (allErrors.Length > 0)
        {
            throw GuardRejected(allErrors, statements.Length);
        }
    }

    private static SqlMcpException GuardRejected(IReadOnlyList<string> reasons, int statementCount)
    {
        return new SqlMcpException(
            ErrorCodes.SqlGuardRejected,
            "SQL batch was rejected by read-only guard.",
            string.Join("; ", reasons),
            "Allowed state is limited to DECLARE, SET @local, CREATE/SELECT INTO local #temp, INSERT/UPDATE/DELETE local #temp, and SELECT result sets.",
            errorDetails: new
            {
                stage = "ast_validation",
                statementCount,
                reasons,
                permanentWritesAllowed = false,
                dynamicSqlAllowed = false,
                explicitTransactionsAllowed = false
            });
    }

    private void ValidateReadonlySelect(string sql, string parseHint)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SqlMcpException(ErrorCodes.SqlParseFailed, "SQL is required.");
        }

        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            throw new SqlMcpException(
                ErrorCodes.SqlParseFailed,
                "SQL parse failed.",
                string.Join("; ", errors.Select(AggregateParseError)),
                parseHint);
        }

        if (fragment is not TSqlScript script)
        {
            throw new SqlMcpException(ErrorCodes.SqlParseFailed, "SQL did not parse as a T-SQL script.");
        }

        var statements = script.Batches.SelectMany(batch => batch.Statements).ToList();
        if (statements.Count != 1)
        {
            throw new SqlMcpException(
                ErrorCodes.SqlGuardRejected,
                "SQL was rejected by read-only guard.",
                "Only a single SELECT statement is allowed.",
                "Submit one SELECT query at a time.");
        }

        if (statements[0] is not SelectStatement selectStatement)
        {
            throw new SqlMcpException(
                ErrorCodes.SqlGuardRejected,
                "SQL was rejected by read-only guard.",
                $"{statements[0].GetType().Name} statements are not allowed.",
                "Use SELECT queries, or use metadata tools for definitions and schema.");
        }

        if (selectStatement.Into is not null)
        {
            throw new SqlMcpException(
                ErrorCodes.SqlGuardRejected,
                "SQL was rejected by read-only guard.",
                "SELECT INTO is not allowed.",
                "Remove INTO and return rows directly.");
        }

        var visitor = new GuardVisitor(_options.Security);
        selectStatement.Accept(visitor);

        if (visitor.Errors.Count > 0)
        {
            throw new SqlMcpException(
                ErrorCodes.SqlGuardRejected,
                "SQL was rejected by read-only guard.",
                string.Join("; ", visitor.Errors),
                "Use only objects inside the configured database and avoid write or server-level operations.");
        }
    }

    private static string AggregateParseError(ParseError error)
    {
        return ToParseErrorDetail(error).Summary;
    }

    private static ParseErrorDetail ToParseErrorDetail(ParseError error)
    {
        var message = SensitiveDataRedactor.Redact(error.Message);
        return new ParseErrorDetail(
            error.Line,
            error.Column,
            message,
            $"Line {error.Line}, Column {error.Column}: {message}");
    }

    private sealed class GuardVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> ServerLevelDmvPrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "sys.dm_exec_",
            "sys.dm_os_",
            "sys.dm_server_",
            "sys.dm_io_",
            "sys.dm_tran_"
        };

        private readonly SecurityOptions _security;

        public GuardVisitor(SecurityOptions security)
        {
            _security = security;
        }

        public List<string> Errors { get; } = [];

        public override void ExplicitVisit(NamedTableReference node)
        {
            ValidateSchemaObject(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            ValidateSchemaObject(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AdHocTableReference node)
        {
            RejectExternalDataSource("OPENDATASOURCE");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenQueryTableReference node)
        {
            RejectExternalDataSource("OPENQUERY");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenRowsetTableReference node)
        {
            RejectExternalDataSource("OPENROWSET");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BulkOpenRowset node)
        {
            RejectExternalDataSource("OPENROWSET(BULK)");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NextValueForExpression node)
        {
            Errors.Add("NEXT VALUE FOR is not allowed because advancing a sequence has persistent side effects.");
            base.ExplicitVisit(node);
        }

        private void ValidateSchemaObject(SchemaObjectName name)
        {
            var parts = name.Identifiers.Select(identifier => identifier.Value).ToList();
            if (parts.Count == 0)
            {
                return;
            }

            if (parts[^1].StartsWith("##", StringComparison.Ordinal))
            {
                Errors.Add($"Global temporary object '{parts[^1]}' is not allowed; only local #temp state is permitted in guarded SQL.");
                return;
            }

            if (!_security.AllowCrossDatabase && parts.Count >= 3)
            {
                Errors.Add($"Cross-database object reference '{string.Join(".", parts)}' is not allowed.");
                return;
            }

            if (!_security.AllowSystemDatabases && parts.Count >= 3 && SecurityOptions.SystemDatabases.Contains(parts[0]))
            {
                Errors.Add($"System database reference '{parts[0]}' is not allowed.");
                return;
            }

            var twoPartName = parts.Count >= 2
                ? $"{parts[^2]}.{parts[^1]}"
                : parts[^1];

            if (twoPartName.StartsWith("sys.dm_", StringComparison.OrdinalIgnoreCase))
            {
                ValidateDmv(twoPartName);
            }
        }

        private void ValidateDmv(string twoPartName)
        {
            if (!_security.AllowDmvQueries)
            {
                Errors.Add($"DMV query '{twoPartName}' is not allowed by config.");
                return;
            }

            if (AllowedDatabaseScopedDmvs.Contains(twoPartName))
            {
                return;
            }

            if (AllowedReadOnlyMetadataFunctions.Contains(twoPartName))
            {
                return;
            }

            if (!_security.AllowServerLevelDmv && ServerLevelDmvPrefixes.Any(twoPartName.StartsWith))
            {
                Errors.Add($"Server-level DMV query '{twoPartName}' is not allowed by config.");
            }
        }

        private void RejectExternalDataSource(string feature)
        {
            Errors.Add($"External data source feature '{feature}' is not allowed.");
        }
    }

    private sealed class ReadonlyBatchVisitor : TSqlFragmentVisitor
    {
        public List<string> Errors { get; } = [];

        public override void Visit(TSqlStatement node)
        {
            if (node is not (DeclareVariableStatement
                or SetVariableStatement
                or CreateTableStatement
                or InsertStatement
                or UpdateStatement
                or DeleteStatement
                or SelectStatement))
            {
                Errors.Add($"{node.GetType().Name} statements are not allowed in a read-only batch.");
            }

            base.Visit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (!IsTempObject(node.SchemaObjectName))
            {
                Errors.Add("CREATE TABLE is allowed only for local #temp tables.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            if (node.Target is not NamedTableReference namedTarget || !IsTempObject(namedTarget.SchemaObject))
            {
                Errors.Add("INSERT is allowed only when the target is a local #temp table.");
            }

            if (node.InsertSource is not (SelectInsertSource or ValuesInsertSource))
            {
                Errors.Add($"{node.InsertSource.GetType().Name} is not an allowed INSERT source; INSERT EXEC and uncertain sources are rejected.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            if (!IsTempWriteTarget(node.Target, node.FromClause))
            {
                Errors.Add("UPDATE is allowed only when the target is a local #temp table, directly or through one uniquely resolved top-level #temp alias.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            if (!IsTempWriteTarget(node.Target, node.FromClause))
            {
                Errors.Add("DELETE is allowed only when the target is a local #temp table, directly or through one uniquely resolved top-level #temp alias.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OutputIntoClause node)
        {
            if (node.IntoTable is not NamedTableReference namedTarget || !IsTempObject(namedTarget.SchemaObject))
            {
                Errors.Add("OUTPUT INTO is allowed only when the target is a local #temp table.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is not null && !IsTempObject(node.Into))
            {
                Errors.Add("SELECT INTO is allowed only for a local #temp table.");
            }

            base.ExplicitVisit(node);
        }

        private static bool IsTempObject(SchemaObjectName? name)
        {
            var identifier = name?.BaseIdentifier?.Value;
            return identifier is { Length: > 1 }
                   && identifier[0] == '#'
                   && identifier[1] != '#'
                   && name!.ServerIdentifier is null
                   && name.DatabaseIdentifier is null;
        }

        private static bool IsTempWriteTarget(TableReference? target, FromClause? fromClause)
        {
            if (target is not NamedTableReference namedTarget)
            {
                return false;
            }

            if (IsTempObject(namedTarget.SchemaObject))
            {
                return true;
            }

            if (fromClause is null || namedTarget.SchemaObject.Identifiers.Count != 1)
            {
                return false;
            }

            var targetAlias = namedTarget.SchemaObject.BaseIdentifier?.Value;
            if (string.IsNullOrWhiteSpace(targetAlias))
            {
                return false;
            }

            var aliasMatches = fromClause.TableReferences
                .SelectMany(EnumerateTopLevelNamedTables)
                .Where(table => table.Alias?.Value.Equals(targetAlias, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            return aliasMatches.Length == 1 && IsTempObject(aliasMatches[0].SchemaObject);
        }

        private static IEnumerable<NamedTableReference> EnumerateTopLevelNamedTables(TableReference reference)
        {
            switch (reference)
            {
                case NamedTableReference named:
                    yield return named;
                    break;
                case JoinTableReference join:
                    foreach (var table in EnumerateTopLevelNamedTables(join.FirstTableReference))
                    {
                        yield return table;
                    }

                    foreach (var table in EnumerateTopLevelNamedTables(join.SecondTableReference))
                    {
                        yield return table;
                    }

                    break;
                case JoinParenthesisTableReference parenthesized:
                    foreach (var table in EnumerateTopLevelNamedTables(parenthesized.Join))
                    {
                        yield return table;
                    }

                    break;
            }
        }
    }

    private sealed record ParseErrorDetail(int Line, int Column, string Message, string Summary);
}
