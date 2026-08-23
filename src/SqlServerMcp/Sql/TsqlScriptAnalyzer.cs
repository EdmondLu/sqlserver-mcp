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

    private static readonly Regex DoomedTransactionPredicateRegex = new(
        @"(?:\bXACT_STATE\s*\(\s*\)\s*=\s*-\s*1\b|\b-\s*1\s*=\s*XACT_STATE\s*\(\s*\))",
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
        diagnostics.AddRange(FindDoomedTransactionCatchWrites(fragment));

        var externalTempTables = visitor.TempTableReferences
            .Where(reference => !visitor.TempTables.ContainsKey(reference.Name))
            .GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(reference => reference.Line).ThenBy(reference => reference.Column).First())
            .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        diagnostics.AddRange(externalTempTables.Select(reference => new TsqlValidationDiagnostic(
            "external_temp_table",
            "warning",
            $"Temporary table '{reference.Name}' is referenced but not created by this script; it is treated as a caller-provided contract.",
            reference.Line,
            reference.Column,
            reference.Name,
            "Verify that every caller creates the temporary table with the required columns before invoking this module.")));

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
            externalTempTables,
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
                    Kind: "temporary_table" or "table_variable" or "cte" or "derived_table",
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
                    $"{binding.Kind switch
                    {
                        "temporary_table" => "Temporary table",
                        "table_variable" => "Table variable",
                        "cte" => "CTE",
                        _ => "Derived table"
                    }} '{binding.Name}' has no column '{column}'.",
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
        var references = columnReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference.InconclusiveReason))
            .ToArray();
        if (references.Length == 0)
        {
            return [];
        }

        var first = references.OrderBy(reference => reference.Line).ThenBy(reference => reference.Column).First();
        var aliases = references
            .Where(reference => reference.Identifiers.Length >= 2)
            .Select(reference => reference.Identifiers[^2])
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scopeCount = references
            .Select(reference => reference.ScopeStartOffset)
            .Where(offset => offset is not null)
            .Distinct()
            .Count();
        var referenceText = aliases.Length == 0
            ? null
            : string.Join(", ", aliases.Take(10)) + (aliases.Length > 10 ? ", ..." : string.Empty);
        return
        [
            new TsqlValidationDiagnostic(
                "analysis_inconclusive",
                "warning",
                $"Static analysis could not conclusively bind {references.Length} column reference{(references.Length == 1 ? string.Empty : "s")} across {aliases.Length} alias{(aliases.Length == 1 ? string.Empty : "es")} and {scopeCount} query scope{(scopeCount == 1 ? string.Empty : "s")}.",
                first.Line,
                first.Column,
                referenceText,
                "These references were not treated as unresolved; inspect the first location when derived, CTE, or APPLY projections must be verified manually.")
        ];
    }

    private static IEnumerable<TsqlValidationDiagnostic> FindDoomedTransactionCatchWrites(TSqlFragment? fragment)
    {
        if (fragment is null)
        {
            return [];
        }

        var visitor = new CatchTransactionSafetyVisitor();
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private static string ReadFragmentText(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null
            || fragment.FirstTokenIndex < 0
            || fragment.LastTokenIndex < fragment.FirstTokenIndex)
        {
            return string.Empty;
        }

        return string.Concat(
            fragment.ScriptTokenStream
                .Skip(fragment.FirstTokenIndex)
                .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                .Select(token => token.Text));
    }

    private sealed class CatchTransactionSafetyVisitor : TSqlFragmentVisitor
    {
        public List<TsqlValidationDiagnostic> Diagnostics { get; } = [];

        public override void ExplicitVisit(TryCatchStatement node)
        {
            var bodyVisitor = new CatchBodySafetyVisitor();
            node.CatchStatements.Accept(bodyVisitor);
            var firstGuardOffset = bodyVisitor.DoomedTransactionGuardOffsets
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            var operationsBeforeGuard = bodyVisitor.DangerousOperations
                .Where(operation => operation.StartOffset < firstGuardOffset)
                .OrderBy(operation => operation.StartOffset)
                .ToArray();
            if (firstGuardOffset == int.MaxValue && bodyVisitor.DangerousOperations.Count > 0)
            {
                var operationsWithoutGuard = bodyVisitor.DangerousOperations
                    .OrderBy(operation => operation.StartOffset)
                    .ToArray();
                var first = operationsWithoutGuard[0];
                var operationKinds = operationsWithoutGuard
                    .Select(operation => operation.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Diagnostics.Add(new TsqlValidationDiagnostic(
                    "doomed_transaction_write_without_guard",
                    "warning",
                    $"CATCH executes {operationsWithoutGuard.Length} potential write operation{(operationsWithoutGuard.Length == 1 ? string.Empty : "s")} without first checking XACT_STATE() = -1 and rethrowing. An uncommittable transaction can raise SQL error 3930 here and hide the original exception.",
                    first.Line,
                    first.Column,
                    string.Join(", ", operationKinds),
                    "Add `IF XACT_STATE() = -1 THROW;` before DROP, DML, SELECT INTO, CREATE TABLE, or logging procedure calls; roll back before any required logging write."));
            }
            else if (operationsBeforeGuard.Length > 0)
            {
                var first = operationsBeforeGuard[0];
                var operationKinds = operationsBeforeGuard
                    .Select(operation => operation.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Diagnostics.Add(new TsqlValidationDiagnostic(
                    "doomed_transaction_write_before_guard",
                    "warning",
                    $"CATCH executes {operationsBeforeGuard.Length} potential write operation{(operationsBeforeGuard.Length == 1 ? string.Empty : "s")} before checking XACT_STATE() = -1 and rethrowing. An uncommittable transaction can raise SQL error 3930 here and hide the original exception.",
                    first.Line,
                    first.Column,
                    string.Join(", ", operationKinds),
                    "Move `IF XACT_STATE() = -1 THROW;` before DROP, DML, SELECT INTO, CREATE TABLE, or logging procedure calls; roll back before any required logging write."));
            }

            base.ExplicitVisit(node);
        }
    }

    private sealed class CatchBodySafetyVisitor : TSqlFragmentVisitor
    {
        public List<int> DoomedTransactionGuardOffsets { get; } = [];

        public List<TsqlCatchWriteOperation> DangerousOperations { get; } = [];

        public override void ExplicitVisit(TryCatchStatement node)
        {
            // A nested TRY/CATCH is analyzed independently by CatchTransactionSafetyVisitor.
        }

        public override void ExplicitVisit(IfStatement node)
        {
            if (DoomedTransactionPredicateRegex.IsMatch(ReadFragmentText(node.Predicate)))
            {
                var terminalVisitor = new CatchGuardTerminalVisitor();
                node.ThenStatement.Accept(terminalVisitor);
                if (terminalVisitor.FirstThrowOffset is int throwOffset)
                {
                    DoomedTransactionGuardOffsets.Add(throwOffset);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropTableStatement node)
        {
            AddOperation("DROP TABLE", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            AddOperation("CREATE TABLE", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is not null)
            {
                AddOperation("SELECT INTO", node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            AddOperation("INSERT", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            AddOperation("UPDATE", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            AddOperation("DELETE", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            AddOperation("MERGE", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            AddOperation("TRUNCATE TABLE", node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            AddOperation("EXECUTE", node);
            base.ExplicitVisit(node);
        }

        private void AddOperation(string kind, TSqlFragment node)
        {
            DangerousOperations.Add(new TsqlCatchWriteOperation(
                kind,
                node.StartOffset,
                node.StartLine,
                node.StartColumn));
        }
    }

    private sealed class CatchGuardTerminalVisitor : TSqlFragmentVisitor
    {
        public int? FirstThrowOffset { get; private set; }

        public override void ExplicitVisit(ThrowStatement node)
        {
            FirstThrowOffset = FirstThrowOffset is null
                ? node.StartOffset
                : Math.Min(FirstThrowOffset.Value, node.StartOffset);
            base.ExplicitVisit(node);
        }
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

        public List<TsqlTempTableReference> TempTableReferences { get; } = [];

        public Dictionary<string, TsqlTableVariable> TableVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        private List<TsqlCteProjection> CteProjections { get; } = [];

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
                else if (objectName.Name.StartsWith('#'))
                {
                    TempTableReferences.Add(new TsqlTempTableReference(
                        objectName.Name,
                        node.StartLine,
                        node.StartColumn));
                }

                TableSources.Add(new TsqlTableSource(
                    node.Alias?.Value ?? objectName.Name,
                    objectName.Name,
                    reference,
                    node.StartOffset,
                    "database_object",
                    null,
                    false));
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
                node.StartOffset,
                "table_variable",
                null,
                false));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            AddDerivedTableSource(
                node.Alias,
                node.Columns,
                node.QueryExpression,
                node.StartOffset);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InlineDerivedTable node)
        {
            var explicitColumns = node.Columns.Select(column => column.Value).ToArray();
            var projection = explicitColumns.Length > 0
                ? new TsqlProjection(explicitColumns.ToHashSet(StringComparer.OrdinalIgnoreCase), true)
                : new TsqlProjection(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
            AddProjectedTableSource(node.Alias, node.StartOffset, "derived_table", projection);
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

        public override void ExplicitVisit(DeleteSpecification node)
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
            var explicitColumns = node.Columns.Select(column => column.Value).ToArray();
            CteProjections.Add(new TsqlCteProjection(
                node.ExpressionName.Value,
                node.StartOffset,
                explicitColumns.Length > 0
                    ? new TsqlProjection(explicitColumns.ToHashSet(StringComparer.OrdinalIgnoreCase), true)
                    : ReadQueryProjection(node.QueryExpression)));
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
            var cteProjection = CteProjections
                .Where(candidate => candidate.Name.Equals(source.ObjectName, StringComparison.OrdinalIgnoreCase)
                                    && candidate.StartOffset <= source.StartOffset)
                .OrderByDescending(candidate => candidate.StartOffset)
                .Select(candidate => candidate.Projection)
                .FirstOrDefault();
            if (cteProjection is not null)
            {
                return cteProjection.Complete
                    ? new TsqlColumnBinding(
                        "cte",
                        null,
                        source.ObjectName,
                        cteProjection.Columns)
                    : null;
            }

            if (source.ObjectName.StartsWith('#')
                && TempTables.TryGetValue(source.ObjectName, out var tempTable))
            {
                return new TsqlColumnBinding(
                    "temporary_table",
                    null,
                    tempTable.Name,
                    tempTable.Columns);
            }

            if (source.ObjectName.StartsWith('#'))
            {
                return new TsqlColumnBinding(
                    "external_temp_table_contract",
                    null,
                    source.ObjectName,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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

            if (source.SourceKind is "cte" or "derived_table")
            {
                return source.ProjectionComplete
                    ? new TsqlColumnBinding(
                        source.SourceKind,
                        null,
                        source.ObjectName,
                        source.Columns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    : null;
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

        private void AddDerivedTableSource(
            Identifier? alias,
            IList<Identifier> explicitColumns,
            QueryExpression queryExpression,
            int startOffset)
        {
            var projection = explicitColumns.Count > 0
                ? new TsqlProjection(
                    explicitColumns.Select(column => column.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    true)
                : ReadQueryProjection(queryExpression);
            AddProjectedTableSource(alias, startOffset, "derived_table", projection);
        }

        private void AddProjectedTableSource(
            Identifier? alias,
            int startOffset,
            string sourceKind,
            TsqlProjection projection)
        {
            if (alias is null || string.IsNullOrWhiteSpace(alias.Value))
            {
                return;
            }

            TableSources.Add(new TsqlTableSource(
                alias.Value,
                alias.Value,
                null,
                startOffset,
                sourceKind,
                projection.Columns,
                projection.Complete));
        }

        private static TsqlProjection ReadQueryProjection(QueryExpression queryExpression)
        {
            return queryExpression switch
            {
                QuerySpecification specification => ReadSelectProjection(specification),
                BinaryQueryExpression binary => ReadQueryProjection(binary.FirstQueryExpression),
                QueryParenthesisExpression parenthesis => ReadQueryProjection(parenthesis.QueryExpression),
                _ => new TsqlProjection(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false)
            };
        }

        private static TsqlProjection ReadSelectProjection(QuerySpecification specification)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var complete = true;
            foreach (var element in specification.SelectElements)
            {
                if (element is not SelectScalarExpression scalar)
                {
                    complete = false;
                    continue;
                }

                var name = scalar.ColumnName?.Value;
                if (string.IsNullOrWhiteSpace(name)
                    && scalar.Expression is ColumnReferenceExpression columnReference)
                {
                    name = columnReference.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    complete = false;
                    continue;
                }

                columns.Add(name);
            }

            return new TsqlProjection(columns, complete);
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
    TsqlTempTableReference[] ExternalTempTables,
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

internal sealed record TsqlCatchWriteOperation(
    string Kind,
    int StartOffset,
    int Line,
    int Column);

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

internal sealed record TsqlTempTableReference(string Name, int Line, int Column);

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
    int StartOffset,
    string SourceKind,
    IReadOnlySet<string>? Columns,
    bool ProjectionComplete);

internal sealed record TsqlProjection(
    IReadOnlySet<string> Columns,
    bool Complete);

internal sealed record TsqlCteProjection(
    string Name,
    int StartOffset,
    TsqlProjection Projection);
