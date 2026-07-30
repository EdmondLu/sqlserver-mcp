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
        var columnReferences = visitor.ResolveColumnReferences();

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
        diagnostics.AddRange(FindLocalTableColumnErrors(columnReferences));
        diagnostics.AddRange(FindInconclusiveColumnWarnings(columnReferences));

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
            columnReferences,
            visitor.UserTypes
                .GroupBy(type => new { type.Schema, type.Name, type.Line, type.Column })
                .Select(group => group.First())
                .ToArray(),
            visitor.Parameters.ToArray(),
            visitor.TempTables.Values
                .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            visitor.TableVariables.Values
                .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
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
            .Where(reference => !visitor.ExecuteParameterLabels.Contains(
                new TsqlVariableIdentity(reference.Name, reference.StartOffset)))
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

    private static IEnumerable<TsqlValidationDiagnostic> FindLocalTableColumnErrors(
        IEnumerable<TsqlColumnReference> columnReferences)
    {
        foreach (var reference in columnReferences.Where(reference => reference.Identifiers.Length >= 2))
        {
            if (reference.Binding is not
                {
                    Kind: "temporary_table" or "table_variable",
                    Columns.Count: > 0
                } binding)
            {
                continue;
            }

            var column = reference.Identifiers[^1];
            if (!binding.Columns.Contains(column))
            {
                yield return new TsqlValidationDiagnostic(
                    "unresolved_column",
                    "error",
                    $"{(binding.Kind == "temporary_table" ? "Temporary table" : "Table variable")} '{binding.Name}' has no column '{column}'.",
                    reference.Line,
                    reference.Column,
                    column,
                    $"Available columns: {string.Join(", ", binding.Columns.OrderBy(value => value))}");
            }
        }
    }

    private static IEnumerable<TsqlValidationDiagnostic> FindInconclusiveColumnWarnings(
        IEnumerable<TsqlColumnReference> columnReferences)
    {
        return columnReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference.InconclusiveReason))
            .GroupBy(reference => new
            {
                Qualifier = reference.Identifiers.Length >= 2 ? reference.Identifiers[^2] : string.Empty,
                reference.ScopeStartOffset,
                reference.InconclusiveReason
            })
            .Select(group =>
            {
                var first = group.OrderBy(reference => reference.Line).ThenBy(reference => reference.Column).First();
                return new TsqlValidationDiagnostic(
                    "analysis_inconclusive",
                    "warning",
                    first.InconclusiveReason!,
                    first.Line,
                    first.Column,
                    group.Key.Qualifier,
                    "The column was not treated as unresolved because its source could not be determined uniquely.");
            });
    }

    private sealed class ValidationVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> DeclaredVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<TsqlVariableReference> VariableReferences { get; } = [];

        public HashSet<TsqlVariableIdentity> ExecuteParameterLabels { get; } = [];

        public List<TsqlObjectReference> ObjectReferences { get; } = [];

        private List<TsqlRawColumnReference> ColumnReferences { get; } = [];

        public List<TsqlUserTypeReference> UserTypes { get; } = [];

        public List<TsqlParameterDefinition> Parameters { get; } = [];

        public Dictionary<string, TsqlTempTable> TempTables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TsqlTableVariable> TableVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<TsqlInsertShape> Inserts { get; } = [];

        private List<TsqlQueryScope> QueryScopes { get; } = [];

        private List<TsqlTableSource> TableSources { get; } = [];

        private HashSet<int> AliasOnlyModificationTargetOffsets { get; } = [];

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

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            var name = node.Body.VariableName.Value;
            DeclaredVariables.Add(name);
            var columns = node.Body.Definition.ColumnDefinitions
                .Select(column => column.ColumnIdentifier.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            TableVariables[name] = new TsqlTableVariable(
                name,
                node.StartLine,
                node.StartColumn,
                columns);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(VariableReference node)
        {
            VariableReferences.Add(new TsqlVariableReference(
                node.Name,
                node.StartLine,
                node.StartColumn,
                node.StartOffset));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteParameter node)
        {
            if (node.Variable is not null)
            {
                ExecuteParameterLabels.Add(new TsqlVariableIdentity(
                    node.Variable.Name,
                    node.Variable.StartOffset));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UserDataTypeReference node)
        {
            AddUserType(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (AliasOnlyModificationTargetOffsets.Contains(node.StartOffset))
            {
                base.ExplicitVisit(node);
                return;
            }

            var objectName = ReadObjectName(node.SchemaObject);
            if (objectName.Name.Length > 0)
            {
                TsqlObjectReference? reference = null;
                if (!objectName.Name.StartsWith('@') && !objectName.Name.StartsWith('#'))
                {
                    reference = new TsqlObjectReference(
                        objectName.Schema,
                        objectName.Name,
                        "table_or_view",
                        node.StartLine,
                        node.StartColumn,
                        objectName.Database,
                        objectName.Server);
                    ObjectReferences.Add(reference);
                }

                TableSources.Add(new TsqlTableSource(
                    node.Alias?.Value ?? objectName.Name,
                    objectName.Name,
                    reference,
                    node.StartOffset));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(VariableTableReference node)
        {
            var name = node.Variable.Name;
            TableSources.Add(new TsqlTableSource(
                node.Alias?.Value ?? name,
                name,
                null,
                node.StartOffset));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            AddInconclusiveTableSource(node.Alias, node.StartOffset);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InlineDerivedTable node)
        {
            AddInconclusiveTableSource(node.Alias, node.StartOffset);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            QueryScopes.Add(new TsqlQueryScope(
                node.StartOffset,
                node.StartOffset + node.FragmentLength));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            QueryScopes.Add(new TsqlQueryScope(
                node.StartOffset,
                node.StartOffset + node.FragmentLength));
            if (node.FromClause is not null
                && node.Target is NamedTableReference target
                && target.SchemaObject.Identifiers.Count == 1)
            {
                AliasOnlyModificationTargetOffsets.Add(target.StartOffset);
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
                ColumnReferences.Add(new TsqlRawColumnReference(
                    identifiers,
                    node.StartLine,
                    node.StartColumn,
                    node.StartOffset));
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

        public TsqlColumnReference[] ResolveColumnReferences()
        {
            var sourcesByScope = TableSources
                .Select(source => new
                {
                    Source = source,
                    Scope = FindInnermostScope(source.StartOffset)
                })
                .Where(item => item.Scope is not null)
                .GroupBy(item => item.Scope!.StartOffset)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Source).ToArray());

            return ColumnReferences
                .Select(reference => ResolveColumnReference(reference, sourcesByScope))
                .ToArray();
        }

        private TsqlColumnReference ResolveColumnReference(
            TsqlRawColumnReference reference,
            IReadOnlyDictionary<int, TsqlTableSource[]> sourcesByScope)
        {
            if (reference.Identifiers.Length < 2)
            {
                return new TsqlColumnReference(
                    reference.Identifiers,
                    reference.Line,
                    reference.Column,
                    null,
                    null,
                    null);
            }

            var qualifier = reference.Identifiers[^2];
            var containingScopes = QueryScopes
                .Where(scope => scope.Contains(reference.StartOffset))
                .OrderBy(scope => scope.Length)
                .ToArray();
            foreach (var scope in containingScopes)
            {
                if (!sourcesByScope.TryGetValue(scope.StartOffset, out var sources))
                {
                    continue;
                }

                var matches = sources
                    .Where(source => source.Qualifier.Equals(qualifier, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0)
                {
                    continue;
                }

                if (matches.Length > 1)
                {
                    return new TsqlColumnReference(
                        reference.Identifiers,
                        reference.Line,
                        reference.Column,
                        null,
                        $"Alias '{qualifier}' has multiple possible sources in the same query scope.",
                        scope.StartOffset);
                }

                var source = matches[0];
                var binding = BuildColumnBinding(source);
                return binding is not null
                    ? new TsqlColumnReference(
                        reference.Identifiers,
                        reference.Line,
                        reference.Column,
                        binding,
                        null,
                        scope.StartOffset)
                    : new TsqlColumnReference(
                        reference.Identifiers,
                        reference.Line,
                        reference.Column,
                        null,
                        $"Alias '{qualifier}' refers to a derived, CTE, or otherwise statically unresolved row source.",
                        scope.StartOffset);
            }

            return new TsqlColumnReference(
                reference.Identifiers,
                reference.Line,
                reference.Column,
                null,
                null,
                containingScopes.FirstOrDefault()?.StartOffset);
        }

        private TsqlColumnBinding? BuildColumnBinding(TsqlTableSource source)
        {
            if (source.ObjectName.StartsWith('#')
                && TempTables.TryGetValue(source.ObjectName, out var tempTable))
            {
                return new TsqlColumnBinding(
                    "temporary_table",
                    null,
                    tempTable.Name,
                    tempTable.Columns);
            }

            if (source.ObjectName.StartsWith('@')
                && TableVariables.TryGetValue(source.ObjectName, out var tableVariable))
            {
                return new TsqlColumnBinding(
                    "table_variable",
                    null,
                    tableVariable.Name,
                    tableVariable.Columns);
            }

            if (CteNames.Contains(source.ObjectName) || source.Reference is null)
            {
                return null;
            }

            return new TsqlColumnBinding(
                "database_object",
                source.Reference.Schema,
                source.Reference.Name,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private TsqlQueryScope? FindInnermostScope(int offset)
        {
            return QueryScopes
                .Where(scope => scope.Contains(offset))
                .OrderBy(scope => scope.Length)
                .FirstOrDefault();
        }

        private void AddInconclusiveTableSource(Identifier? alias, int startOffset)
        {
            if (alias is null || string.IsNullOrWhiteSpace(alias.Value))
            {
                return;
            }

            TableSources.Add(new TsqlTableSource(
                alias.Value,
                alias.Value,
                null,
                startOffset));
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
    TsqlTableVariable[] TableVariables,
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

internal sealed record TsqlColumnReference(
    string[] Identifiers,
    int Line,
    int Column,
    TsqlColumnBinding? Binding,
    string? InconclusiveReason,
    int? ScopeStartOffset);

internal sealed record TsqlColumnBinding(
    string Kind,
    string? Schema,
    string Name,
    IReadOnlySet<string> Columns);

internal sealed record TsqlUserTypeReference(string Schema, string Name, int Line, int Column);

internal sealed record TsqlParameterDefinition(string Name, string DataType, int Line, int Column);

internal sealed record TsqlVariableReference(
    string Name,
    int Line,
    int Column,
    int StartOffset);

internal sealed record TsqlVariableIdentity(string Name, int StartOffset);

internal sealed record TsqlTempTable(
    string Name,
    int Line,
    int Column,
    IReadOnlySet<string> Columns);

internal sealed record TsqlTableVariable(
    string Name,
    int Line,
    int Column,
    IReadOnlySet<string> Columns);

internal sealed record TsqlInsertShape(
    int TargetColumnCount,
    int[] SourceColumnCounts,
    int Line,
    int Column);

internal sealed record TsqlRawColumnReference(
    string[] Identifiers,
    int Line,
    int Column,
    int StartOffset);

internal sealed record TsqlQueryScope(int StartOffset, int EndOffset)
{
    public int Length => EndOffset - StartOffset;

    public bool Contains(int offset)
    {
        return offset >= StartOffset && offset < EndOffset;
    }
}

internal sealed record TsqlTableSource(
    string Qualifier,
    string ObjectName,
    TsqlObjectReference? Reference,
    int StartOffset);
