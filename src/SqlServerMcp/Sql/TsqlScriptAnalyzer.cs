using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Sql;

internal static class TsqlScriptAnalyzer
{
    private static readonly Regex CreateOrAlterModuleRegex = new(
        @"\bCREATE\s+OR\s+ALTER\s+(?:PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreateModuleRegex = new(
        @"\bCREATE\s+(?:PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TsqlScriptAnalysis Analyze(string script)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(script), out var parseErrors);
        var visitor = new ValidationVisitor();
        fragment?.Accept(visitor);

        var diagnostics = parseErrors
            .Select(error => new TsqlValidationDiagnostic(
                "syntax_error",
                "error",
                error.Message,
                error.Line,
                error.Column,
                null,
                null))
            .ToList();

        diagnostics.AddRange(FindUndeclaredVariables(visitor));
        diagnostics.AddRange(FindInsertShapeMismatches(visitor));
        diagnostics.AddRange(FindTempTableColumnErrors(visitor));

        var hasCreateOrAlter = CreateOrAlterModuleRegex.IsMatch(script);
        var hasCreateModule = CreateModuleRegex.IsMatch(script);
        if (hasCreateModule && !hasCreateOrAlter)
        {
            diagnostics.Add(new TsqlValidationDiagnostic(
                "create_or_alter_recommended",
                "warning",
                "Module script uses CREATE without OR ALTER.",
                1,
                1,
                null,
                "Use CREATE OR ALTER for idempotent deployment."));
        }

        if (visitor.HasDynamicSql)
        {
            diagnostics.Add(new TsqlValidationDiagnostic(
                "dynamic_sql_unverified",
                "warning",
                "Dynamic SQL was detected and cannot be fully resolved by static validation.",
                visitor.DynamicSqlLine,
                visitor.DynamicSqlColumn,
                null,
                "Review generated SQL separately or validate a concrete expanded statement."));
        }

        return new TsqlScriptAnalysis(
            parseErrors.Count == 0,
            hasCreateOrAlter,
            visitor.Module,
            visitor.ObjectReferences
                .GroupBy(reference => new
                {
                    reference.Schema,
                    reference.Name,
                    reference.Kind,
                    reference.Line,
                    reference.Column
                })
                .Select(group => group.First())
                .ToArray(),
            visitor.ColumnReferences.ToArray(),
            visitor.UserTypes
                .GroupBy(type => new { type.Schema, type.Name, type.Line, type.Column })
                .Select(group => group.First())
                .ToArray(),
            visitor.Parameters.ToArray(),
            visitor.TempTables.Values
                .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            visitor.TableBindings,
            visitor.CteNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            diagnostics
                .OrderBy(diagnostic => diagnostic.Line ?? int.MaxValue)
                .ThenBy(diagnostic => diagnostic.Column ?? int.MaxValue)
                .ThenBy(diagnostic => diagnostic.Category, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static IEnumerable<TsqlValidationDiagnostic> FindUndeclaredVariables(ValidationVisitor visitor)
    {
        return visitor.VariableReferences
            .Where(reference => !reference.Name.StartsWith("@@", StringComparison.Ordinal))
            .Where(reference => !visitor.DeclaredVariables.Contains(reference.Name))
            .GroupBy(reference => new { reference.Name, reference.Line, reference.Column })
            .Select(group => new TsqlValidationDiagnostic(
                "undeclared_variable",
                "error",
                $"Variable '{group.Key.Name}' is referenced but not declared.",
                group.Key.Line,
                group.Key.Column,
                group.Key.Name,
                "Declare the variable or add it to the module parameter list."));
    }

    private static IEnumerable<TsqlValidationDiagnostic> FindInsertShapeMismatches(ValidationVisitor visitor)
    {
        foreach (var insert in visitor.Inserts)
        {
            if (insert.TargetColumnCount <= 0 || insert.SourceColumnCounts.Length == 0)
            {
                continue;
            }

            foreach (var sourceCount in insert.SourceColumnCounts.Distinct())
            {
                if (sourceCount < 0)
                {
                    continue;
                }

                if (sourceCount == insert.TargetColumnCount)
                {
                    continue;
                }

                yield return new TsqlValidationDiagnostic(
                    "insert_column_count_mismatch",
                    "error",
                    $"INSERT target has {insert.TargetColumnCount} columns but source has {sourceCount}.",
                    insert.Line,
                    insert.Column,
                    null,
                    "Make the INSERT target column list and SELECT/VALUES projection counts equal.");
            }
        }
    }

    private static IEnumerable<TsqlValidationDiagnostic> FindTempTableColumnErrors(ValidationVisitor visitor)
    {
        foreach (var reference in visitor.ColumnReferences.Where(reference => reference.Identifiers.Length >= 2))
        {
            var qualifier = reference.Identifiers[^2];
            if (visitor.TableAliases.TryGetValue(qualifier, out var tableName))
            {
                qualifier = tableName;
            }

            if (!qualifier.StartsWith('#')
                || !visitor.TempTables.TryGetValue(qualifier, out var tempTable)
                || tempTable.Columns.Count == 0)
            {
                continue;
            }

            var column = reference.Identifiers[^1];
            if (!tempTable.Columns.Contains(column))
            {
                yield return new TsqlValidationDiagnostic(
                    "unresolved_column",
                    "error",
                    $"Temporary table '{qualifier}' has no column '{column}'.",
                    reference.Line,
                    reference.Column,
                    column,
                    $"Available columns: {string.Join(", ", tempTable.Columns.OrderBy(value => value))}");
            }
        }
    }

    private sealed class ValidationVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> DeclaredVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<TsqlVariableReference> VariableReferences { get; } = [];

        public List<TsqlObjectReference> ObjectReferences { get; } = [];

        public List<TsqlColumnReference> ColumnReferences { get; } = [];

        public List<TsqlUserTypeReference> UserTypes { get; } = [];

        public List<TsqlParameterDefinition> Parameters { get; } = [];

        public Dictionary<string, TsqlTempTable> TempTables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> TableAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TsqlObjectReference> TableBindings { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<TsqlInsertShape> Inserts { get; } = [];

        public TsqlModuleTarget? Module { get; private set; }

        public bool HasDynamicSql { get; private set; }

        public int? DynamicSqlLine { get; private set; }

        public int? DynamicSqlColumn { get; private set; }

        public override void ExplicitVisit(ProcedureParameter node)
        {
            DeclaredVariables.Add(node.VariableName.Value);
            AddUserType(node.DataType);
            Parameters.Add(new TsqlParameterDefinition(
                node.VariableName.Value,
                ReadDataTypeName(node.DataType),
                node.StartLine,
                node.StartColumn));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareVariableElement node)
        {
            DeclaredVariables.Add(node.VariableName.Value);
            AddUserType(node.DataType);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(VariableReference node)
        {
            VariableReferences.Add(new TsqlVariableReference(node.Name, node.StartLine, node.StartColumn));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UserDataTypeReference node)
        {
            AddUserType(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var objectName = ReadObjectName(node.SchemaObject);
            if (objectName.Name.Length > 0)
            {
                var reference = new TsqlObjectReference(
                    objectName.Schema,
                    objectName.Name,
                    "table_or_view",
                    node.StartLine,
                    node.StartColumn,
                    objectName.Database,
                    objectName.Server);
                ObjectReferences.Add(reference);
                TableBindings[objectName.Name] = reference;
                if (node.Alias is not null)
                {
                    TableAliases[node.Alias.Value] = objectName.Name;
                    TableBindings[node.Alias.Value] = reference;
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            var identifiers = node.MultiPartIdentifier?.Identifiers
                .Select(identifier => identifier.Value)
                .ToArray() ?? [];
            if (identifiers.Length > 0 && identifiers[^1] != "*")
            {
                ColumnReferences.Add(new TsqlColumnReference(
                    identifiers,
                    node.StartLine,
                    node.StartColumn));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommonTableExpression node)
        {
            CteNames.Add(node.ExpressionName.Value);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            var objectName = ReadObjectName(node.SchemaObjectName);
            if (objectName.Name.StartsWith('#'))
            {
                var columns = node.Definition.ColumnDefinitions
                    .Select(column => column.ColumnIdentifier.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                TempTables[objectName.Name] = new TsqlTempTable(
                    objectName.Name,
                    node.StartLine,
                    node.StartColumn,
                    columns);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            var sourceCounts = node.InsertSource switch
            {
                SelectInsertSource selectSource => ReadSelectColumnCounts(selectSource.Select).ToArray(),
                ValuesInsertSource valuesSource => valuesSource.RowValues
                    .Select(row => row.ColumnValues.Count)
                    .ToArray(),
                _ => []
            };
            Inserts.Add(new TsqlInsertShape(
                node.Columns.Count,
                sourceCounts,
                node.StartLine,
                node.StartColumn));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableStringList node)
        {
            HasDynamicSql = true;
            DynamicSqlLine ??= node.StartLine;
            DynamicSqlColumn ??= node.StartColumn;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (name is not null)
            {
                var objectName = ReadObjectName(name);
                ObjectReferences.Add(new TsqlObjectReference(
                    objectName.Schema,
                    objectName.Name,
                    "procedure_call",
                    node.StartLine,
                    node.StartColumn,
                    objectName.Database,
                    objectName.Server));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            Module = ReadModuleTarget(node.ProcedureReference?.Name, "procedure", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            Module = ReadModuleTarget(node.ProcedureReference?.Name, "procedure", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node)
        {
            Module = ReadModuleTarget(node.Name, "function", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            Module = ReadModuleTarget(node.Name, "function", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterViewStatement node)
        {
            Module = ReadModuleTarget(node.SchemaObjectName, "view", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            Module = ReadModuleTarget(node.SchemaObjectName, "view", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
        {
            Module = ReadModuleTarget(node.Name, "trigger", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            Module = ReadModuleTarget(node.Name, "trigger", node);
            base.ExplicitVisit(node);
        }

        private void AddUserType(DataTypeReference? dataType)
        {
            if (dataType is not UserDataTypeReference userType)
            {
                return;
            }

            var name = ReadObjectName(userType.Name);
            UserTypes.Add(new TsqlUserTypeReference(
                name.Schema,
                name.Name,
                userType.StartLine,
                userType.StartColumn));
        }

        private static TsqlModuleTarget? ReadModuleTarget(
            SchemaObjectName? name,
            string type,
            TSqlFragment node)
        {
            if (name is null)
            {
                return null;
            }

            var objectName = ReadObjectName(name);
            return new TsqlModuleTarget(
                objectName.Schema,
                objectName.Name,
                type,
                node.StartLine,
                node.StartColumn);
        }

        private static IEnumerable<int> ReadSelectColumnCounts(QueryExpression query)
        {
            switch (query)
            {
                case QuerySpecification specification:
                    yield return specification.SelectElements.Any(element => element is SelectStarExpression)
                        ? -1
                        : specification.SelectElements.Count;
                    break;
                case QueryParenthesisExpression parenthesis:
                    foreach (var count in ReadSelectColumnCounts(parenthesis.QueryExpression))
                    {
                        yield return count;
                    }

                    break;
                case BinaryQueryExpression binary:
                    foreach (var count in ReadSelectColumnCounts(binary.FirstQueryExpression))
                    {
                        yield return count;
                    }

                    foreach (var count in ReadSelectColumnCounts(binary.SecondQueryExpression))
                    {
                        yield return count;
                    }

                    break;
            }
        }

        private static (string? Server, string? Database, string Schema, string Name) ReadObjectName(
            SchemaObjectName name)
        {
            return (
                name.ServerIdentifier?.Value,
                name.DatabaseIdentifier?.Value,
                name.SchemaIdentifier?.Value ?? "dbo",
                name.BaseIdentifier?.Value ?? string.Empty);
        }

        private static string ReadDataTypeName(DataTypeReference dataType)
        {
            return dataType switch
            {
                SqlDataTypeReference sqlType => sqlType.SqlDataTypeOption.ToString(),
                UserDataTypeReference userType => string.Join(
                    ".",
                    userType.Name.Identifiers.Select(identifier => identifier.Value)),
                _ => dataType.GetType().Name
            };
        }
    }
}

internal sealed record TsqlScriptAnalysis(
    bool SyntaxValid,
    bool HasCreateOrAlter,
    TsqlModuleTarget? Module,
    TsqlObjectReference[] ObjectReferences,
    TsqlColumnReference[] ColumnReferences,
    TsqlUserTypeReference[] UserTypes,
    TsqlParameterDefinition[] Parameters,
    TsqlTempTable[] TempTables,
    IReadOnlyDictionary<string, TsqlObjectReference> TableBindings,
    string[] CteNames,
    TsqlValidationDiagnostic[] Diagnostics);

internal sealed record TsqlValidationDiagnostic(
    string Category,
    string Severity,
    string Message,
    int? Line,
    int? Column,
    string? Reference,
    string? Suggestion);

internal sealed record TsqlModuleTarget(
    string Schema,
    string Name,
    string Type,
    int Line,
    int Column);

internal sealed record TsqlObjectReference(
    string Schema,
    string Name,
    string Kind,
    int Line,
    int Column,
    string? Database,
    string? Server);

internal sealed record TsqlColumnReference(string[] Identifiers, int Line, int Column);

internal sealed record TsqlUserTypeReference(string Schema, string Name, int Line, int Column);

internal sealed record TsqlParameterDefinition(string Name, string DataType, int Line, int Column);

internal sealed record TsqlVariableReference(string Name, int Line, int Column);

internal sealed record TsqlTempTable(
    string Name,
    int Line,
    int Column,
    IReadOnlySet<string> Columns);

internal sealed record TsqlInsertShape(
    int TargetColumnCount,
    int[] SourceColumnCounts,
    int Line,
    int Column);
