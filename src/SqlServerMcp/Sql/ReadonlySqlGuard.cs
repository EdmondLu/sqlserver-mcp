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
        return $"Line {error.Line}, Column {error.Column}: {error.Message}";
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

        private void ValidateSchemaObject(SchemaObjectName name)
        {
            var parts = name.Identifiers.Select(identifier => identifier.Value).ToList();
            if (parts.Count == 0)
            {
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
}
