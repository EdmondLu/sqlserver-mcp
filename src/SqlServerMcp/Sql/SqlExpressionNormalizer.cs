using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Sql;

internal static class SqlExpressionNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var expression = value.Trim().TrimEnd(';').Trim();
        if (TryParseBoolean(expression, out var booleanExpression))
        {
            return $"BOOL:{CanonicalizeBoolean(booleanExpression!)}";
        }

        if (TryParseScalar(expression, out var scalarExpression))
        {
            return $"SCALAR:{CanonicalizeScalar(scalarExpression!)}";
        }

        return $"RAW:{CanonicalizeRaw(expression)}";
    }

    private static bool TryParseBoolean(string expression, out BooleanExpression? result)
    {
        result = null;
        var fragment = Parse($"SELECT 1 WHERE {expression};", out var errors);
        if (errors.Count > 0
            || fragment is not TSqlScript script
            || script.Batches.FirstOrDefault()?.Statements.FirstOrDefault() is not SelectStatement select
            || select.QueryExpression is not QuerySpecification query)
        {
            return false;
        }

        result = query.WhereClause?.SearchCondition;
        return result is not null;
    }

    private static bool TryParseScalar(string expression, out ScalarExpression? result)
    {
        result = null;
        var fragment = Parse($"SELECT {expression};", out var errors);
        if (errors.Count > 0
            || fragment is not TSqlScript script
            || script.Batches.FirstOrDefault()?.Statements.FirstOrDefault() is not SelectStatement select
            || select.QueryExpression is not QuerySpecification query
            || query.SelectElements.FirstOrDefault() is not SelectScalarExpression scalar)
        {
            return false;
        }

        result = scalar.Expression;
        return result is not null;
    }

    private static TSqlFragment Parse(string sql, out IList<ParseError> errors)
    {
        using var reader = new StringReader(sql);
        return new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out errors);
    }

    private static string CanonicalizeBoolean(BooleanExpression expression)
    {
        return expression switch
        {
            BooleanParenthesisExpression parenthesis => CanonicalizeBoolean(parenthesis.Expression),
            BooleanBinaryExpression binary => CanonicalizeBooleanBinary(binary),
            BooleanComparisonExpression comparison => CanonicalizeComparison(comparison),
            InPredicate predicate => CanonicalizeInPredicate(predicate),
            BooleanIsNullExpression isNull => $"IS{(isNull.IsNot ? "NOT" : string.Empty)}NULL({CanonicalizeScalar(isNull.Expression)})",
            BooleanNotExpression not => $"NOT({CanonicalizeBoolean(not.Expression)})",
            LikePredicate like => $"{(like.NotDefined ? "NOT" : string.Empty)}LIKE({CanonicalizeScalar(like.FirstExpression)},{CanonicalizeScalar(like.SecondExpression)},{CanonicalizeNullableScalar(like.EscapeExpression)})",
            BooleanTernaryExpression ternary => $"{ternary.TernaryExpressionType.ToString().ToUpperInvariant()}({CanonicalizeScalar(ternary.FirstExpression)},{CanonicalizeScalar(ternary.SecondExpression)},{CanonicalizeScalar(ternary.ThirdExpression)})",
            _ => CanonicalizeFragment(expression)
        };
    }

    private static string CanonicalizeBooleanBinary(BooleanBinaryExpression binary)
    {
        var values = new List<string>();
        CollectBooleanOperands(binary, binary.BinaryExpressionType, values);
        values.Sort(StringComparer.Ordinal);
        return $"{binary.BinaryExpressionType.ToString().ToUpperInvariant()}({string.Join(',', values)})";
    }

    private static void CollectBooleanOperands(
        BooleanExpression expression,
        BooleanBinaryExpressionType type,
        ICollection<string> values)
    {
        if (expression is BooleanParenthesisExpression parenthesis)
        {
            CollectBooleanOperands(parenthesis.Expression, type, values);
            return;
        }

        if (expression is BooleanBinaryExpression binary && binary.BinaryExpressionType == type)
        {
            CollectBooleanOperands(binary.FirstExpression, type, values);
            CollectBooleanOperands(binary.SecondExpression, type, values);
            return;
        }

        values.Add(CanonicalizeBoolean(expression));
    }

    private static string CanonicalizeComparison(BooleanComparisonExpression comparison)
    {
        var operation = comparison.ComparisonType switch
        {
            BooleanComparisonType.Equals => "EQ",
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "NE",
            _ => comparison.ComparisonType.ToString().ToUpperInvariant()
        };
        var first = CanonicalizeScalar(comparison.FirstExpression);
        var second = CanonicalizeScalar(comparison.SecondExpression);
        if (operation is "EQ" or "NE" && string.CompareOrdinal(first, second) > 0)
        {
            (first, second) = (second, first);
        }

        return $"{operation}({first},{second})";
    }

    private static string CanonicalizeInPredicate(InPredicate predicate)
    {
        if (predicate.Subquery is not null)
        {
            return CanonicalizeFragment(predicate);
        }

        var left = CanonicalizeScalar(predicate.Expression);
        var operation = predicate.NotDefined ? "NE" : "EQ";
        var comparisons = predicate.Values
            .Select(value =>
            {
                var right = CanonicalizeScalar(value);
                if (string.CompareOrdinal(left, right) > 0)
                {
                    return $"{operation}({right},{left})";
                }

                return $"{operation}({left},{right})";
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var conjunction = predicate.NotDefined ? "AND" : "OR";
        return comparisons.Length == 1 ? comparisons[0] : $"{conjunction}({string.Join(',', comparisons)})";
    }

    private static string CanonicalizeScalar(ScalarExpression expression)
    {
        return expression switch
        {
            ParenthesisExpression parenthesis => CanonicalizeScalar(parenthesis.Expression),
            ColumnReferenceExpression column => $"COLUMN({CanonicalizeIdentifierParts(column.MultiPartIdentifier?.Identifiers)})",
            FunctionCall function => CanonicalizeFunction(function),
            IntegerLiteral integer => $"NUMBER({CanonicalizeNumber(integer.Value)})",
            NumericLiteral numeric => $"NUMBER({CanonicalizeNumber(numeric.Value)})",
            MoneyLiteral money => $"NUMBER({CanonicalizeNumber(money.Value)})",
            RealLiteral real => $"NUMBER({CanonicalizeNumber(real.Value)})",
            StringLiteral text => $"STRING({(text.IsNational ? "N" : string.Empty)}:{text.Value.Length}:{text.Value})",
            BinaryLiteral binary => $"BINARY({binary.Value.ToUpperInvariant()})",
            NullLiteral => "NULL",
            DefaultLiteral => "DEFAULT",
            MaxLiteral => "MAX",
            VariableReference variable => $"VARIABLE({variable.Name.ToUpperInvariant()})",
            IdentifierLiteral identifier => $"IDENTIFIER({identifier.Value.ToUpperInvariant()})",
            UnaryExpression unary when unary.UnaryExpressionType == UnaryExpressionType.Positive => CanonicalizeScalar(unary.Expression),
            UnaryExpression unary => $"{unary.UnaryExpressionType.ToString().ToUpperInvariant()}({CanonicalizeScalar(unary.Expression)})",
            BinaryExpression binaryExpression => $"{binaryExpression.BinaryExpressionType.ToString().ToUpperInvariant()}({CanonicalizeScalar(binaryExpression.FirstExpression)},{CanonicalizeScalar(binaryExpression.SecondExpression)})",
            _ => CanonicalizeFragment(expression)
        };
    }

    private static string CanonicalizeFunction(FunctionCall function)
    {
        var target = function.CallTarget is null ? string.Empty : $"{CanonicalizeFragment(function.CallTarget)}.";
        var parameters = function.Parameters.Select(CanonicalizeScalar);
        return $"FUNCTION({target}{function.FunctionName.Value.ToUpperInvariant()}({string.Join(',', parameters)}))";
    }

    private static string CanonicalizeNullableScalar(ScalarExpression? expression) =>
        expression is null ? string.Empty : CanonicalizeScalar(expression);

    private static string CanonicalizeIdentifierParts(IEnumerable<Identifier>? identifiers) =>
        string.Join('.', identifiers?.Select(identifier => identifier.Value.ToUpperInvariant()) ?? []);

    private static string CanonicalizeNumber(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue.ToString("G29", CultureInfo.InvariantCulture);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        }

        return value.ToUpperInvariant();
    }

    private static string CanonicalizeRaw(string expression)
    {
        var fragment = Parse($"SELECT {expression};", out var errors);
        return errors.Count == 0 ? CanonicalizeFragment(fragment) : expression.ToUpperInvariant();
    }

    private static string CanonicalizeFragment(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null || fragment.FirstTokenIndex < 0 || fragment.LastTokenIndex < 0)
        {
            return fragment.GetType().Name.ToUpperInvariant();
        }

        var tokens = new List<string>();
        for (var index = fragment.FirstTokenIndex; index <= fragment.LastTokenIndex; index++)
        {
            var token = fragment.ScriptTokenStream[index];
            if (token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
            {
                continue;
            }

            var text = token.Text;
            if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
            {
                text = text[1..^1].Replace("]]", "]", StringComparison.Ordinal);
            }
            else if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                text = text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            }
            else if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                text = CanonicalizeNumber(text);
            }

            tokens.Add(text.ToUpperInvariant());
        }

        return string.Join(' ', tokens);
    }
}
