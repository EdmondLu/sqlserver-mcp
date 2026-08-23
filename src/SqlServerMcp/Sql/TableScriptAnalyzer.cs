using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Sql;

internal static class TableScriptAnalyzer
{
    public static TableScriptAnalysis Analyze(string script, string schema, string name)
    {
        using var reader = new StringReader(script);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(reader, out var parseErrors);
        var diagnostics = parseErrors
            .Select(error => new TableScriptDiagnostic(
                "syntax_error",
                "error",
                error.Message,
                error.Line,
                error.Column))
            .ToList();
        if (parseErrors.Count > 0)
        {
            return new TableScriptAnalysis(false, null, diagnostics.ToArray());
        }

        var collector = new TableStatementCollector();
        fragment.Accept(collector);
        var matchingCreates = collector.CreateTables
            .Where(statement => IsTarget(statement.SchemaObjectName, schema, name))
            .OrderBy(statement => statement.StartOffset)
            .ToArray();
        if (matchingCreates.Length == 0)
        {
            diagnostics.Add(new TableScriptDiagnostic(
                "create_table_missing",
                "error",
                $"No CREATE TABLE statement for '{schema}.{name}' was found.",
                null,
                null));
            return new TableScriptAnalysis(true, null, diagnostics.ToArray());
        }

        if (matchingCreates.Length > 1)
        {
            diagnostics.Add(new TableScriptDiagnostic(
                "multiple_create_table_statements",
                "error",
                $"Multiple CREATE TABLE statements for '{schema}.{name}' were found.",
                matchingCreates[1].StartLine,
                matchingCreates[1].StartColumn));
            return new TableScriptAnalysis(true, null, diagnostics.ToArray());
        }

        var create = matchingCreates[0];
        var builder = new TableSchemaBuilder(schema, name);
        builder.ApplyDefinition(create.Definition, script, diagnostics);
        var laterStatements = collector.Operations
            .Where(operation => operation.Fragment.StartOffset > create.StartOffset)
            .Where(operation => operation.Target is not null && IsTarget(operation.Target, schema, name))
            .OrderBy(operation => operation.Fragment.StartOffset)
            .ToArray();
        foreach (var operation in laterStatements)
        {
            switch (operation.Fragment)
            {
                case AlterTableAddTableElementStatement add:
                    builder.ApplyDefinition(add.Definition, script, diagnostics);
                    break;
                case AlterTableAlterColumnStatement alter:
                    builder.AlterColumn(alter, script, diagnostics);
                    break;
                case AlterTableDropTableElementStatement drop:
                    builder.DropElements(drop, diagnostics);
                    break;
                case CreateIndexStatement createIndex:
                    builder.AddIndex(createIndex, script);
                    break;
                case DropIndexClause dropIndex:
                    builder.DropIndex(dropIndex.Index.Value);
                    break;
            }
        }

        foreach (var execute in collector.ExecuteStatements.OrderBy(statement => statement.StartOffset))
        {
            builder.ApplyDescription(execute, diagnostics);
        }

        return new TableScriptAnalysis(
            true,
            builder.Build(),
            diagnostics
                .OrderBy(diagnostic => diagnostic.Line ?? int.MaxValue)
                .ThenBy(diagnostic => diagnostic.Column ?? int.MaxValue)
                .ToArray());
    }

    public static TableStructureComparison Compare(
        TableSchemaModel expected,
        TableSchemaModel actual,
        bool includeDescriptions = false)
    {
        var differences = new List<TableStructureDifference>();
        CompareColumns(expected.Columns, actual.Columns, differences);
        CompareNamedSet("index", expected.Indexes, actual.Indexes, IndexKey, differences);
        CompareNamedSet("key_constraint", expected.KeyConstraints, actual.KeyConstraints, KeyConstraintKey, differences);
        CompareNamedSet("default_constraint", expected.DefaultConstraints, actual.DefaultConstraints, DefaultConstraintKey, differences);
        CompareNamedSet("check_constraint", expected.CheckConstraints, actual.CheckConstraints, CheckConstraintKey, differences);
        CompareNamedSet("foreign_key", expected.ForeignKeys, actual.ForeignKeys, ForeignKeyKey, differences);
        if (includeDescriptions)
        {
            CompareDescriptions(expected, actual, differences);
        }

        return new TableStructureComparison(differences.Count == 0, differences.ToArray());
    }

    public static string BuildDatabaseTypeSignature(
        string typeSchema,
        string typeName,
        bool isUserDefined,
        int maxLengthBytes,
        byte precision,
        byte scale)
    {
        if (isUserDefined)
        {
            return $"{typeSchema.ToLowerInvariant()}.{typeName.ToLowerInvariant()}";
        }

        var normalized = NormalizeTypeName(typeName);
        return normalized switch
        {
            "nvarchar" or "nchar" => $"{normalized}({(maxLengthBytes < 0 ? "max" : (maxLengthBytes / 2).ToString())})",
            "varchar" or "char" or "varbinary" or "binary" => $"{normalized}({(maxLengthBytes < 0 ? "max" : maxLengthBytes.ToString())})",
            "decimal" => $"decimal({precision},{scale})",
            "float" => precision == 53 ? "float" : $"float({precision})",
            "datetime2" or "datetimeoffset" or "time" => $"{normalized}({scale})",
            _ => normalized
        };
    }

    public static string NormalizeExpression(string? value)
        => SqlExpressionNormalizer.Normalize(value);

    private static void CompareColumns(
        TableColumnSpec[] expected,
        TableColumnSpec[] actual,
        ICollection<TableStructureDifference> differences)
    {
        var actualByName = actual.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var expectedColumn in expected)
        {
            if (!actualByName.Remove(expectedColumn.Name, out var actualColumn))
            {
                differences.Add(new("column", expectedColumn.Name, "missing", expectedColumn, null));
                continue;
            }

            var expectedComparable = expectedColumn with
            {
                DefaultConstraintName = NormalizeOptionalName(expectedColumn.DefaultConstraintName)
            };
            var actualComparable = actualColumn with
            {
                DefaultConstraintName = expectedColumn.DefaultConstraintName is null
                    ? null
                    : NormalizeOptionalName(actualColumn.DefaultConstraintName)
            };
            if (expectedComparable.Ordinal != actualComparable.Ordinal
                || (!expectedComparable.Computed
                    && !expectedComparable.TypeSignature.Equals(actualComparable.TypeSignature, StringComparison.OrdinalIgnoreCase))
                || (expectedComparable.Nullable is not null && expectedComparable.Nullable != actualComparable.Nullable)
                || expectedComparable.Identity != actualComparable.Identity
                || expectedComparable.Computed != actualComparable.Computed
                || NormalizeExpression(expectedComparable.ComputedDefinition) != NormalizeExpression(actualComparable.ComputedDefinition)
                || NormalizeExpression(expectedComparable.DefaultDefinition) != NormalizeExpression(actualComparable.DefaultDefinition)
                || !string.Equals(expectedComparable.DefaultConstraintName, actualComparable.DefaultConstraintName, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(new("column", expectedColumn.Name, "changed", expectedColumn, actualColumn));
            }
        }

        foreach (var unexpected in actualByName.Values.OrderBy(column => column.Ordinal))
        {
            differences.Add(new("column", unexpected.Name, "unexpected", null, unexpected));
        }
    }

    private static void CompareDescriptions(
        TableSchemaModel expected,
        TableSchemaModel actual,
        ICollection<TableStructureDifference> differences)
    {
        if (!string.Equals(
                NormalizeDescription(expected.Description),
                NormalizeDescription(actual.Description),
                StringComparison.Ordinal))
        {
            differences.Add(new(
                "table_description",
                $"{expected.Schema}.{expected.Name}",
                "changed",
                expected.Description,
                actual.Description));
        }

        var actualColumns = actual.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var expectedColumn in expected.Columns)
        {
            if (!actualColumns.TryGetValue(expectedColumn.Name, out var actualColumn))
            {
                continue;
            }

            if (!string.Equals(
                    NormalizeDescription(expectedColumn.Description),
                    NormalizeDescription(actualColumn.Description),
                    StringComparison.Ordinal))
            {
                differences.Add(new(
                    "column_description",
                    expectedColumn.Name,
                    "changed",
                    expectedColumn.Description,
                    actualColumn.Description));
            }
        }
    }

    private static string NormalizeDescription(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static void CompareNamedSet<T>(
        string section,
        T[] expected,
        T[] actual,
        Func<T, string> semanticKey,
        ICollection<TableStructureDifference> differences)
        where T : INamedTableElement
    {
        var remaining = actual.ToList();
        foreach (var expectedItem in expected)
        {
            var expectedKey = semanticKey(expectedItem);
            var matchIndex = remaining.FindIndex(item => semanticKey(item).Equals(expectedKey, StringComparison.OrdinalIgnoreCase));
            if (matchIndex < 0)
            {
                differences.Add(new(section, expectedItem.Name ?? expectedKey, "missing_or_changed", expectedItem, null));
                continue;
            }

            var actualItem = remaining[matchIndex];
            remaining.RemoveAt(matchIndex);
            if (expectedItem.Name is not null
                && !expectedItem.Name.Equals(actualItem.Name, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(new(section, expectedItem.Name, "name_changed", expectedItem, actualItem));
            }
        }

        foreach (var unexpected in remaining)
        {
            differences.Add(new(section, unexpected.Name ?? semanticKey(unexpected), "unexpected", null, unexpected));
        }
    }

    private static string IndexKey(TableIndexSpec value) => string.Join(
        "|",
        value.Type.ToUpperInvariant(),
        value.Unique,
        value.PrimaryKey,
        string.Join(",", value.KeyColumns.Select(column => $"{column.Name.ToUpperInvariant()}:{column.Descending}")),
        string.Join(",", value.IncludedColumns.Select(column => column.ToUpperInvariant())),
        NormalizeExpression(value.FilterDefinition));

    private static string KeyConstraintKey(TableKeyConstraintSpec value) => string.Join(
        "|",
        value.Type.ToUpperInvariant(),
        string.Join(",", value.Columns.Select(column => $"{column.Name.ToUpperInvariant()}:{column.Descending}")));

    private static string DefaultConstraintKey(TableDefaultConstraintSpec value) =>
        $"{value.Column.ToUpperInvariant()}|{NormalizeExpression(value.Definition)}";

    private static string CheckConstraintKey(TableCheckConstraintSpec value) => NormalizeExpression(value.Definition);

    private static string ForeignKeyKey(TableForeignKeySpec value) => string.Join(
        "|",
        string.Join(",", value.ParentColumns.Select(column => column.ToUpperInvariant())),
        value.ReferencedSchema.ToUpperInvariant(),
        value.ReferencedTable.ToUpperInvariant(),
        string.Join(",", value.ReferencedColumns.Select(column => column.ToUpperInvariant())),
        value.DeleteAction.ToUpperInvariant(),
        value.UpdateAction.ToUpperInvariant());

    private static string? NormalizeOptionalName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTarget(SchemaObjectName? objectName, string schema, string name)
    {
        return objectName is not null
               && (objectName.SchemaIdentifier?.Value ?? "dbo").Equals(schema, StringComparison.OrdinalIgnoreCase)
               && (objectName.BaseIdentifier?.Value ?? string.Empty).Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static string FragmentText(TSqlFragment? fragment, string script)
    {
        if (fragment is null || fragment.StartOffset < 0 || fragment.FragmentLength <= 0)
        {
            return string.Empty;
        }

        var length = Math.Min(fragment.FragmentLength, script.Length - fragment.StartOffset);
        return length <= 0 ? string.Empty : script.Substring(fragment.StartOffset, length);
    }

    private static string ReadColumnName(ColumnReferenceExpression expression) =>
        expression.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value ?? string.Empty;

    private static string BuildTypeSignature(DataTypeReference? dataType, string script)
    {
        if (dataType is null)
        {
            return string.Empty;
        }

        if (dataType is UserDataTypeReference userType)
        {
            return string.Join('.', userType.Name.Identifiers.Select(identifier => identifier.Value.ToLowerInvariant()));
        }

        if (dataType is not SqlDataTypeReference sqlType)
        {
            return Regex.Replace(FragmentText(dataType, script), @"\s+", string.Empty).ToLowerInvariant();
        }

        var typeName = NormalizeTypeName(sqlType.SqlDataTypeOption.ToString());
        var parameters = sqlType.Parameters
            .Select(parameter => FragmentText(parameter, script).Trim().ToLowerInvariant())
            .ToArray();
        if (typeName == "decimal" && parameters.Length == 0)
        {
            parameters = ["18", "0"];
        }

        if (typeName is "varchar" or "char" or "nvarchar" or "nchar" or "varbinary" or "binary"
            && parameters.Length == 0)
        {
            parameters = ["1"];
        }

        if (typeName == "float" && parameters is ["53"])
        {
            parameters = [];
        }

        if (typeName is "datetime2" or "datetimeoffset" or "time" && parameters.Length == 0)
        {
            parameters = ["7"];
        }

        return parameters.Length == 0 ? typeName : $"{typeName}({string.Join(',', parameters)})";
    }

    private static string NormalizeTypeName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "numeric" => "decimal",
            "timestamp" => "rowversion",
            "sqlvariant" => "sql_variant",
            _ => normalized
        };
    }

    private sealed class TableStatementCollector : TSqlFragmentVisitor
    {
        public List<CreateTableStatement> CreateTables { get; } = [];

        public List<TableOperation> Operations { get; } = [];

        public List<ExecuteStatement> ExecuteStatements { get; } = [];

        public override void ExplicitVisit(CreateTableStatement node)
        {
            CreateTables.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTableAddTableElementStatement node)
        {
            Operations.Add(new(node, node.SchemaObjectName));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTableAlterColumnStatement node)
        {
            Operations.Add(new(node, node.SchemaObjectName));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTableDropTableElementStatement node)
        {
            Operations.Add(new(node, node.SchemaObjectName));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            Operations.Add(new(node, node.OnName));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropIndexStatement node)
        {
            foreach (var clause in node.DropIndexClauses)
            {
                if (clause is DropIndexClause dropIndex)
                {
                    Operations.Add(new(dropIndex, dropIndex.Object));
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            ExecuteStatements.Add(node);
            base.ExplicitVisit(node);
        }
    }

    private sealed record TableOperation(TSqlFragment Fragment, SchemaObjectName? Target);

    private sealed class TableSchemaBuilder
    {
        private readonly string _schema;
        private readonly string _name;
        private readonly List<TableColumnSpec> _columns = [];
        private readonly List<TableIndexSpec> _indexes = [];
        private readonly List<TableKeyConstraintSpec> _keyConstraints = [];
        private readonly List<TableDefaultConstraintSpec> _defaultConstraints = [];
        private readonly List<TableCheckConstraintSpec> _checkConstraints = [];
        private readonly List<TableForeignKeySpec> _foreignKeys = [];
        private readonly Dictionary<string, string?> _columnDescriptions = new(StringComparer.OrdinalIgnoreCase);
        private string? _description;

        public TableSchemaBuilder(string schema, string name)
        {
            _schema = schema;
            _name = name;
        }

        public void ApplyDefinition(
            TableDefinition definition,
            string script,
            ICollection<TableScriptDiagnostic> diagnostics)
        {
            foreach (var column in definition.ColumnDefinitions)
            {
                AddColumn(column, script, diagnostics);
            }

            foreach (var constraint in definition.TableConstraints)
            {
                AddConstraint(constraint, null, script);
            }

            foreach (var index in definition.Indexes)
            {
                AddIndex(index, script);
            }
        }

        public void AlterColumn(
            AlterTableAlterColumnStatement statement,
            string script,
            ICollection<TableScriptDiagnostic> diagnostics)
        {
            var index = _columns.FindIndex(column => column.Name.Equals(statement.ColumnIdentifier.Value, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                diagnostics.Add(new TableScriptDiagnostic(
                    "alter_column_missing",
                    "warning",
                    $"ALTER COLUMN references unknown column '{statement.ColumnIdentifier.Value}'.",
                    statement.StartLine,
                    statement.StartColumn));
                return;
            }

            var existing = _columns[index];
            var nullable = statement.AlterTableAlterColumnOption switch
            {
                AlterTableAlterColumnOption.Null => true,
                AlterTableAlterColumnOption.NotNull => false,
                _ => existing.Nullable
            };
            _columns[index] = existing with
            {
                TypeSignature = BuildTypeSignature(statement.DataType, script),
                Nullable = nullable
            };
        }

        public void DropElements(
            AlterTableDropTableElementStatement statement,
            ICollection<TableScriptDiagnostic> diagnostics)
        {
            foreach (var element in statement.AlterTableDropTableElements)
            {
                var removed = element.TableElementType switch
                {
                    TableElementType.Column => _columns.RemoveAll(column => column.Name.Equals(element.Name.Value, StringComparison.OrdinalIgnoreCase)),
                    TableElementType.Index => _indexes.RemoveAll(index => index.Name?.Equals(element.Name.Value, StringComparison.OrdinalIgnoreCase) == true),
                    TableElementType.Constraint => RemoveConstraint(element.Name.Value),
                    _ => 0
                };
                if (removed == 0)
                {
                    diagnostics.Add(new TableScriptDiagnostic(
                        "drop_element_unresolved",
                        "warning",
                        $"DROP {element.TableElementType} '{element.Name.Value}' could not be resolved in the accumulated table model.",
                        element.StartLine,
                        element.StartColumn));
                }
            }
        }

        public void AddIndex(CreateIndexStatement statement, string script)
        {
            _indexes.Add(new TableIndexSpec(
                statement.Name?.Value,
                statement.Clustered == true ? "CLUSTERED" : "NONCLUSTERED",
                statement.Unique,
                false,
                statement.Columns.Select(ReadKeyColumn).ToArray(),
                statement.IncludeColumns.Select(ReadColumnName).ToArray(),
                NormalizeExpression(FragmentText(statement.FilterPredicate, script))));
        }

        public void DropIndex(string name)
        {
            _indexes.RemoveAll(index => index.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }

        public void ApplyDescription(
            ExecuteStatement statement,
            ICollection<TableScriptDiagnostic> diagnostics)
        {
            if (statement.ExecuteSpecification.ExecutableEntity is not ExecutableProcedureReference executable
                || executable.ProcedureReference.ProcedureReference?.Name.BaseIdentifier?.Value is not { } procedureName
                || (!procedureName.Equals("sp_addextendedproperty", StringComparison.OrdinalIgnoreCase)
                    && !procedureName.Equals("sp_updateextendedproperty", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var parameters = executable.Parameters
                .Where(parameter => parameter.Variable is not null)
                .ToDictionary(
                    parameter => parameter.Variable.Name.TrimStart('@'),
                    parameter => parameter.ParameterValue,
                    StringComparer.OrdinalIgnoreCase);
            if (!TryReadLiteralValue(parameters, "name", out var propertyName)
                || !string.Equals(propertyName, "MS_Description", StringComparison.OrdinalIgnoreCase)
                || !TryReadLiteralValue(parameters, "level0type", out var level0Type)
                || !string.Equals(level0Type, "SCHEMA", StringComparison.OrdinalIgnoreCase)
                || !TryReadLiteralValue(parameters, "level0name", out var schema)
                || !string.Equals(schema, _schema, StringComparison.OrdinalIgnoreCase)
                || !TryReadLiteralValue(parameters, "level1type", out var level1Type)
                || !string.Equals(level1Type, "TABLE", StringComparison.OrdinalIgnoreCase)
                || !TryReadLiteralValue(parameters, "level1name", out var table)
                || !string.Equals(table, _name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryReadLiteralValue(parameters, "value", out var value))
            {
                diagnostics.Add(new TableScriptDiagnostic(
                    "description_value_unresolved",
                    "warning",
                    "MS_Description uses a non-literal value and could not be compared.",
                    statement.StartLine,
                    statement.StartColumn));
                return;
            }

            if (TryReadLiteralValue(parameters, "level2type", out var level2Type)
                && string.Equals(level2Type, "COLUMN", StringComparison.OrdinalIgnoreCase)
                && TryReadLiteralValue(parameters, "level2name", out var column)
                && !string.IsNullOrWhiteSpace(column))
            {
                _columnDescriptions[column] = value;
                return;
            }

            _description = value;
        }

        public TableSchemaModel Build() => new(
            _schema,
            _name,
            _columns
                .OrderBy(column => column.Ordinal)
                .Select(column => column with
                {
                    Description = _columnDescriptions.GetValueOrDefault(column.Name)
                })
                .ToArray(),
            _indexes.ToArray(),
            _keyConstraints.ToArray(),
            _defaultConstraints.ToArray(),
            _checkConstraints.ToArray(),
            _foreignKeys.ToArray(),
            _description);

        private static bool TryReadLiteralValue(
            IReadOnlyDictionary<string, ScalarExpression> parameters,
            string name,
            out string? value)
        {
            value = null;
            if (!parameters.TryGetValue(name, out var expression))
            {
                return false;
            }

            value = expression switch
            {
                StringLiteral text => text.Value,
                NullLiteral => null,
                IntegerLiteral integer => integer.Value,
                NumericLiteral numeric => numeric.Value,
                _ => null
            };
            return expression is StringLiteral or NullLiteral or IntegerLiteral or NumericLiteral;
        }

        private void AddColumn(
            ColumnDefinition column,
            string script,
            ICollection<TableScriptDiagnostic> diagnostics)
        {
            var nullableConstraint = column.Constraints.OfType<NullableConstraintDefinition>().LastOrDefault();
            var nullable = column.ComputedColumnExpression is null
                ? nullableConstraint?.Nullable ?? true
                : nullableConstraint?.Nullable;
            var defaultConstraint = column.DefaultConstraint
                ?? column.Constraints.OfType<DefaultConstraintDefinition>().LastOrDefault();
            var spec = new TableColumnSpec(
                _columns.Count + 1,
                column.ColumnIdentifier.Value,
                BuildTypeSignature(column.DataType, script),
                nullable,
                column.IdentityOptions is not null,
                column.ComputedColumnExpression is not null,
                NormalizeExpression(FragmentText(column.ComputedColumnExpression, script)),
                defaultConstraint?.ConstraintIdentifier?.Value,
                NormalizeExpression(FragmentText(defaultConstraint?.Expression, script)));
            var existing = _columns.FindIndex(item => item.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                diagnostics.Add(new TableScriptDiagnostic(
                    "duplicate_column",
                    "error",
                    $"Column '{spec.Name}' is declared more than once.",
                    column.StartLine,
                    column.StartColumn));
                _columns[existing] = spec with { Ordinal = _columns[existing].Ordinal };
            }
            else
            {
                _columns.Add(spec);
            }

            if (defaultConstraint is not null)
            {
                _defaultConstraints.Add(new TableDefaultConstraintSpec(
                    defaultConstraint.ConstraintIdentifier?.Value,
                    spec.Name,
                    spec.DefaultDefinition ?? string.Empty));
            }

            foreach (var constraint in column.Constraints.Where(value => value is not NullableConstraintDefinition and not DefaultConstraintDefinition))
            {
                AddConstraint(constraint, spec.Name, script);
            }

            if (column.Index is not null)
            {
                AddIndex(column.Index, script);
            }
        }

        private void AddConstraint(ConstraintDefinition constraint, string? columnName, string script)
        {
            switch (constraint)
            {
                case UniqueConstraintDefinition unique:
                    {
                        var columns = unique.Columns.Count > 0
                            ? unique.Columns.Select(ReadKeyColumn).ToArray()
                            : columnName is null ? [] : [new TableKeyColumnSpec(columnName, false)];
                        var type = unique.IsPrimaryKey ? "PRIMARY_KEY" : "UNIQUE";
                        var name = unique.ConstraintIdentifier?.Value;
                        if (unique.IsPrimaryKey)
                        {
                            foreach (var keyColumn in columns)
                            {
                                var columnIndex = _columns.FindIndex(column => column.Name.Equals(keyColumn.Name, StringComparison.OrdinalIgnoreCase));
                                if (columnIndex >= 0)
                                {
                                    _columns[columnIndex] = _columns[columnIndex] with { Nullable = false };
                                }
                            }
                        }

                        _keyConstraints.Add(new TableKeyConstraintSpec(name, type, columns));
                        _indexes.Add(new TableIndexSpec(
                            name,
                            unique.Clustered ?? unique.IsPrimaryKey ? "CLUSTERED" : "NONCLUSTERED",
                            true,
                            unique.IsPrimaryKey,
                            columns,
                            [],
                            string.Empty));
                        break;
                    }
                case DefaultConstraintDefinition defaultConstraint when columnName is not null || defaultConstraint.Column is not null:
                    {
                        var targetColumn = columnName ?? defaultConstraint.Column.Value;
                        var definition = NormalizeExpression(FragmentText(defaultConstraint.Expression, script));
                        _defaultConstraints.Add(new TableDefaultConstraintSpec(
                            defaultConstraint.ConstraintIdentifier?.Value,
                            targetColumn,
                            definition));
                        var targetIndex = _columns.FindIndex(column => column.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase));
                        if (targetIndex >= 0)
                        {
                            _columns[targetIndex] = _columns[targetIndex] with
                            {
                                DefaultConstraintName = defaultConstraint.ConstraintIdentifier?.Value,
                                DefaultDefinition = definition
                            };
                        }

                        break;
                    }
                case CheckConstraintDefinition check:
                    _checkConstraints.Add(new TableCheckConstraintSpec(
                        check.ConstraintIdentifier?.Value,
                        NormalizeExpression(FragmentText(check.CheckCondition, script))));
                    break;
                case ForeignKeyConstraintDefinition foreignKey:
                    _foreignKeys.Add(new TableForeignKeySpec(
                        foreignKey.ConstraintIdentifier?.Value,
                        foreignKey.Columns.Count > 0
                            ? foreignKey.Columns.Select(identifier => identifier.Value).ToArray()
                            : columnName is null ? [] : [columnName],
                        foreignKey.ReferenceTableName.SchemaIdentifier?.Value ?? "dbo",
                        foreignKey.ReferenceTableName.BaseIdentifier?.Value ?? string.Empty,
                        foreignKey.ReferencedTableColumns.Select(identifier => identifier.Value).ToArray(),
                        NormalizeAction(foreignKey.DeleteAction),
                        NormalizeAction(foreignKey.UpdateAction)));
                    break;
            }
        }

        private void AddIndex(IndexDefinition index, string script)
        {
            var kind = index.IndexType?.IndexTypeKind?.ToString() ?? "NonClustered";
            _indexes.Add(new TableIndexSpec(
                index.Name?.Value,
                kind.StartsWith("Clustered", StringComparison.OrdinalIgnoreCase) ? "CLUSTERED" : "NONCLUSTERED",
                index.Unique,
                false,
                index.Columns.Select(ReadKeyColumn).ToArray(),
                index.IncludeColumns.Select(ReadColumnName).ToArray(),
                NormalizeExpression(FragmentText(index.FilterPredicate, script))));
        }

        private int RemoveConstraint(string name)
        {
            var removedDefaults = _defaultConstraints
                .Where(value => value.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            foreach (var value in removedDefaults)
            {
                var columnIndex = _columns.FindIndex(column => column.Name.Equals(value.Column, StringComparison.OrdinalIgnoreCase));
                if (columnIndex >= 0)
                {
                    _columns[columnIndex] = _columns[columnIndex] with
                    {
                        DefaultConstraintName = null,
                        DefaultDefinition = null
                    };
                }
            }

            return _keyConstraints.RemoveAll(value => value.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                   + _defaultConstraints.RemoveAll(value => value.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                   + _checkConstraints.RemoveAll(value => value.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                   + _foreignKeys.RemoveAll(value => value.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                   + _indexes.RemoveAll(value => value.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }

        private static TableKeyColumnSpec ReadKeyColumn(ColumnWithSortOrder column) => new(
            ReadColumnName(column.Column),
            column.SortOrder == SortOrder.Descending);

        private static string NormalizeAction(DeleteUpdateAction action) => action switch
        {
            DeleteUpdateAction.Cascade => "CASCADE",
            DeleteUpdateAction.SetNull => "SET_NULL",
            DeleteUpdateAction.SetDefault => "SET_DEFAULT",
            _ => "NO_ACTION"
        };
    }
}

internal sealed record TableScriptAnalysis(
    bool SyntaxValid,
    TableSchemaModel? Table,
    TableScriptDiagnostic[] Diagnostics);

internal sealed record TableScriptDiagnostic(
    string Category,
    string Severity,
    string Message,
    int? Line,
    int? Column);

internal sealed record TableSchemaModel(
    string Schema,
    string Name,
    TableColumnSpec[] Columns,
    TableIndexSpec[] Indexes,
    TableKeyConstraintSpec[] KeyConstraints,
    TableDefaultConstraintSpec[] DefaultConstraints,
    TableCheckConstraintSpec[] CheckConstraints,
    TableForeignKeySpec[] ForeignKeys,
    string? Description = null);

internal sealed record TableColumnSpec(
    int Ordinal,
    string Name,
    string TypeSignature,
    bool? Nullable,
    bool Identity,
    bool Computed,
    string? ComputedDefinition,
    string? DefaultConstraintName,
    string? DefaultDefinition,
    string? Description = null);

internal interface INamedTableElement
{
    string? Name { get; }
}

internal sealed record TableIndexSpec(
    string? Name,
    string Type,
    bool Unique,
    bool PrimaryKey,
    TableKeyColumnSpec[] KeyColumns,
    string[] IncludedColumns,
    string? FilterDefinition) : INamedTableElement;

internal sealed record TableKeyColumnSpec(string Name, bool Descending);

internal sealed record TableKeyConstraintSpec(
    string? Name,
    string Type,
    TableKeyColumnSpec[] Columns) : INamedTableElement;

internal sealed record TableDefaultConstraintSpec(
    string? Name,
    string Column,
    string Definition) : INamedTableElement;

internal sealed record TableCheckConstraintSpec(
    string? Name,
    string Definition) : INamedTableElement;

internal sealed record TableForeignKeySpec(
    string? Name,
    string[] ParentColumns,
    string ReferencedSchema,
    string ReferencedTable,
    string[] ReferencedColumns,
    string DeleteAction,
    string UpdateAction) : INamedTableElement;

internal sealed record TableStructureComparison(
    bool Equivalent,
    TableStructureDifference[] Differences);

internal sealed record TableStructureDifference(
    string Section,
    string Name,
    string Kind,
    object? Expected,
    object? Actual);
