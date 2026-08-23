using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Sql;

internal static class ConfigPatchAnalyzer
{
    public static ConfigPatchAnalysis Analyze(string script)
    {
        using var reader = new StringReader(script);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(reader, out var parseErrors);
        var diagnostics = parseErrors
            .Select(error => new ConfigPatchDiagnostic(
                "syntax_error",
                "error",
                error.Message,
                error.Line,
                error.Column))
            .ToList();
        if (parseErrors.Count > 0)
        {
            return new ConfigPatchAnalysis(false, [], diagnostics.ToArray());
        }

        var visitor = new PatchVisitor(diagnostics);
        fragment.Accept(visitor);
        if (visitor.Patches.Count == 0)
        {
            diagnostics.Add(new ConfigPatchDiagnostic(
                "supported_update_missing",
                "warning",
                "No statically verifiable UPDATE assignment was found.",
                null,
                null));
        }

        return new ConfigPatchAnalysis(
            true,
            visitor.Patches.OrderBy(patch => patch.Line).ThenBy(patch => patch.Column).ToArray(),
            diagnostics
                .OrderBy(diagnostic => diagnostic.Line ?? int.MaxValue)
                .ThenBy(diagnostic => diagnostic.Column ?? int.MaxValue)
                .ToArray());
    }

    public static string EvaluateState(ConfigPatchExpectation patch, string? currentValue)
    {
        return patch.Operation switch
        {
            "set_literal" => string.Equals(currentValue, patch.ExpectedValue, StringComparison.Ordinal)
                ? "deployed"
                : "not_deployed",
            "replace" => EvaluateReplaceState(currentValue, patch.OldValue, patch.NewValue),
            _ => "inconclusive"
        };
    }

    private static string EvaluateReplaceState(string? currentValue, string? oldValue, string? newValue)
    {
        if (currentValue is null || oldValue is null || newValue is null)
        {
            return "inconclusive";
        }

        if (oldValue == newValue)
        {
            return "deployed";
        }

        var hasOld = currentValue.Contains(oldValue, StringComparison.Ordinal);
        var hasNew = currentValue.Contains(newValue, StringComparison.Ordinal);
        return (hasOld, hasNew) switch
        {
            (false, true) => "deployed",
            (true, false) => "not_deployed",
            (true, true) => "partially_deployed",
            _ => "inconclusive"
        };
    }

    private sealed class PatchVisitor : TSqlFragmentVisitor
    {
        private readonly ICollection<ConfigPatchDiagnostic> _diagnostics;
        private readonly Dictionary<string, ConfigScalarValue> _variables = new(StringComparer.OrdinalIgnoreCase);

        public PatchVisitor(ICollection<ConfigPatchDiagnostic> diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public List<ConfigPatchExpectation> Patches { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                if (TryReadConstant(declaration.Value, out var value))
                {
                    _variables[declaration.VariableName.Value] = value;
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.Variable is not null && TryReadConstant(node.Expression, out var value))
            {
                _variables[node.Variable.Name] = value;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var specification = node.UpdateSpecification;
            var target = ResolveTarget(specification);
            if (target is null)
            {
                _diagnostics.Add(new ConfigPatchDiagnostic(
                    "update_target_unresolved",
                    "warning",
                    "UPDATE target could not be resolved to one table.",
                    node.StartLine,
                    node.StartColumn));
                base.ExplicitVisit(node);
                return;
            }

            var predicates = ReadPredicates(specification.WhereClause?.SearchCondition).ToArray();
            foreach (var assignment in specification.SetClauses.OfType<AssignmentSetClause>())
            {
                var column = ReadColumnName(assignment.Column);
                if (string.IsNullOrWhiteSpace(column))
                {
                    continue;
                }

                if (TryReadConstant(assignment.NewValue, out var literal))
                {
                    Patches.Add(new ConfigPatchExpectation(
                        target.Value.Schema,
                        target.Value.Table,
                        column,
                        predicates,
                        "set_literal",
                        literal.Value,
                        null,
                        null,
                        assignment.StartLine,
                        assignment.StartColumn));
                    continue;
                }

                if (TryReadReplace(assignment.NewValue, column, out var oldValue, out var newValue))
                {
                    Patches.Add(new ConfigPatchExpectation(
                        target.Value.Schema,
                        target.Value.Table,
                        column,
                        predicates,
                        "replace",
                        null,
                        oldValue,
                        newValue,
                        assignment.StartLine,
                        assignment.StartColumn));
                    continue;
                }

                _diagnostics.Add(new ConfigPatchDiagnostic(
                    "assignment_expression_unsupported",
                    "warning",
                    $"Assignment to '{column}' is not a literal or REPLACE(column, old, new) expression.",
                    assignment.StartLine,
                    assignment.StartColumn));
            }

            base.ExplicitVisit(node);
        }

        private (string Schema, string Table)? ResolveTarget(UpdateSpecification specification)
        {
            if (specification.Target is not NamedTableReference namedTarget)
            {
                return null;
            }

            var targetName = namedTarget.SchemaObject.BaseIdentifier?.Value;
            var targetSchema = namedTarget.SchemaObject.SchemaIdentifier?.Value;
            var fromCollector = new NamedTableCollector();
            specification.FromClause?.Accept(fromCollector);
            if (targetSchema is null && targetName is not null)
            {
                var aliasMatches = fromCollector.Tables
                    .Where(table => table.Alias?.Value.Equals(targetName, StringComparison.OrdinalIgnoreCase) == true)
                    .ToArray();
                if (aliasMatches.Length == 1)
                {
                    return ReadTarget(aliasMatches[0].SchemaObject);
                }
            }

            return string.IsNullOrWhiteSpace(targetName)
                ? null
                : (targetSchema ?? "dbo", targetName);
        }

        private IEnumerable<ConfigPatchPredicate> ReadPredicates(BooleanExpression? expression)
        {
            if (expression is null)
            {
                yield break;
            }

            if (expression is BooleanParenthesisExpression parenthesis)
            {
                foreach (var predicate in ReadPredicates(parenthesis.Expression))
                {
                    yield return predicate;
                }

                yield break;
            }

            if (expression is BooleanBinaryExpression
                {
                    BinaryExpressionType: BooleanBinaryExpressionType.And
                } binary)
            {
                foreach (var predicate in ReadPredicates(binary.FirstExpression))
                {
                    yield return predicate;
                }

                foreach (var predicate in ReadPredicates(binary.SecondExpression))
                {
                    yield return predicate;
                }

                yield break;
            }

            if (expression is BooleanComparisonExpression
                {
                    ComparisonType: BooleanComparisonType.Equals
                } comparison)
            {
                if (TryReadColumnAndConstant(comparison.FirstExpression, comparison.SecondExpression, out var predicate)
                    || TryReadColumnAndConstant(comparison.SecondExpression, comparison.FirstExpression, out predicate))
                {
                    yield return predicate;
                }
            }
        }

        private bool TryReadColumnAndConstant(
            ScalarExpression columnExpression,
            ScalarExpression valueExpression,
            out ConfigPatchPredicate predicate)
        {
            predicate = default!;
            if (columnExpression is not ColumnReferenceExpression column
                || !TryReadConstant(valueExpression, out var value))
            {
                return false;
            }

            var name = ReadColumnName(column);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            predicate = new ConfigPatchPredicate(name, value.Value);
            return true;
        }

        private bool TryReadReplace(
            ScalarExpression expression,
            string targetColumn,
            out string? oldValue,
            out string? newValue)
        {
            oldValue = null;
            newValue = null;
            if (expression is not FunctionCall function
                || !function.FunctionName.Value.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
                || function.Parameters.Count != 3
                || function.Parameters[0] is not ColumnReferenceExpression sourceColumn
                || !ReadColumnName(sourceColumn).Equals(targetColumn, StringComparison.OrdinalIgnoreCase)
                || !TryReadConstant(function.Parameters[1], out var oldScalar)
                || !TryReadConstant(function.Parameters[2], out var newScalar))
            {
                return false;
            }

            oldValue = oldScalar.Value;
            newValue = newScalar.Value;
            return true;
        }

        private bool TryReadConstant(ScalarExpression? expression, out ConfigScalarValue value)
        {
            switch (expression)
            {
                case NullLiteral:
                    value = new ConfigScalarValue(null);
                    return true;
                case Literal literal:
                    value = new ConfigScalarValue(literal.Value);
                    return true;
                case VariableReference variable:
                    if (_variables.TryGetValue(variable.Name, out var variableValue))
                    {
                        value = variableValue;
                        return true;
                    }

                    break;
                case ParenthesisExpression parenthesis:
                    return TryReadConstant(parenthesis.Expression, out value);
                default:
                    break;
            }

            value = default!;
            return false;
        }

        private static (string Schema, string Table) ReadTarget(SchemaObjectName objectName) => (
            objectName.SchemaIdentifier?.Value ?? "dbo",
            objectName.BaseIdentifier?.Value ?? string.Empty);

        private static string ReadColumnName(ColumnReferenceExpression expression) =>
            expression.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value ?? string.Empty;
    }

    private sealed class NamedTableCollector : TSqlFragmentVisitor
    {
        public List<NamedTableReference> Tables { get; } = [];

        public override void ExplicitVisit(NamedTableReference node)
        {
            Tables.Add(node);
            base.ExplicitVisit(node);
        }
    }

    private sealed record ConfigScalarValue(string? Value);
}

internal sealed record ConfigPatchAnalysis(
    bool SyntaxValid,
    ConfigPatchExpectation[] Patches,
    ConfigPatchDiagnostic[] Diagnostics);

internal sealed record ConfigPatchExpectation(
    string Schema,
    string Table,
    string Column,
    ConfigPatchPredicate[] Predicates,
    string Operation,
    string? ExpectedValue,
    string? OldValue,
    string? NewValue,
    int Line,
    int ColumnPosition);

internal sealed record ConfigPatchPredicate(string Column, string? Value);

internal sealed record ConfigPatchDiagnostic(
    string Category,
    string Severity,
    string Message,
    int? Line,
    int? Column);
