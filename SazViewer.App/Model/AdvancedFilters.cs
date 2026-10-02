using System.Globalization;
using System.Text.RegularExpressions;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Model;

internal enum AdvancedFilterFieldKind
{
    Column,
    RequestBody,
    ResponseBody
}

internal enum AdvancedFilterOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    IsIn,
    IsNotIn,
    IsOneOf,
    IsNotOneOf,
    StartsWith,
    DoesNotStartWith,
    EndsWith,
    DoesNotEndWith,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Between,
    IsEmpty,
    IsNotEmpty,
    MatchesRegex
}

internal enum AdvancedFilterJoin
{
    And,
    Or
}

internal sealed record AdvancedFilterField(
    AdvancedFilterFieldKind Kind,
    SessionColumnSetting? Column = null)
{
    public static AdvancedFilterField ForColumn(SessionColumnSetting setting) =>
        new(AdvancedFilterFieldKind.Column, setting);

    public static AdvancedFilterField RequestBody { get; } = new(AdvancedFilterFieldKind.RequestBody);

    public static AdvancedFilterField ResponseBody { get; } = new(AdvancedFilterFieldKind.ResponseBody);
}

internal sealed record AdvancedFilterRule(
    bool Enabled,
    int OpenParentheses,
    AdvancedFilterField Field,
    AdvancedFilterOperator Operator,
    string Value,
    int CloseParentheses,
    AdvancedFilterJoin Join,
    bool CaseSensitive);

internal sealed record AdvancedFilterDefinition(IReadOnlyList<AdvancedFilterRule> Rules)
{
    public static AdvancedFilterDefinition Empty { get; } = new([]);
}

internal sealed record AdvancedFilterValidationError(int RuleIndex, string Message);

internal sealed record AdvancedFilterCompilation(
    AdvancedFilterCompiler.CompiledAdvancedFilter? Filter,
    IReadOnlyList<AdvancedFilterValidationError> Errors);

internal sealed class AdvancedFilterRuntimeException(int ruleIndex, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int RuleIndex { get; } = ruleIndex;
}

internal static class AdvancedFilterCompiler
{
    public const int MaximumRules = 128;
    public const int MaximumValueLength = 16_384;
    public const int MaximumRegexPatternLength = 1_024;
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly AdvancedFilterOperator[] TextOperators =
    [
        AdvancedFilterOperator.Equals,
        AdvancedFilterOperator.NotEquals,
        AdvancedFilterOperator.Contains,
        AdvancedFilterOperator.NotContains,
        AdvancedFilterOperator.IsIn,
        AdvancedFilterOperator.IsNotIn,
        AdvancedFilterOperator.IsOneOf,
        AdvancedFilterOperator.IsNotOneOf,
        AdvancedFilterOperator.StartsWith,
        AdvancedFilterOperator.DoesNotStartWith,
        AdvancedFilterOperator.EndsWith,
        AdvancedFilterOperator.DoesNotEndWith,
        AdvancedFilterOperator.IsEmpty,
        AdvancedFilterOperator.IsNotEmpty,
        AdvancedFilterOperator.MatchesRegex
    ];

    private static readonly AdvancedFilterOperator[] TypedOperators =
    [
        AdvancedFilterOperator.Equals,
        AdvancedFilterOperator.NotEquals,
        AdvancedFilterOperator.IsOneOf,
        AdvancedFilterOperator.IsNotOneOf,
        AdvancedFilterOperator.LessThan,
        AdvancedFilterOperator.LessThanOrEqual,
        AdvancedFilterOperator.GreaterThan,
        AdvancedFilterOperator.GreaterThanOrEqual,
        AdvancedFilterOperator.Between,
        AdvancedFilterOperator.IsEmpty,
        AdvancedFilterOperator.IsNotEmpty
    ];

    public static IReadOnlyList<AdvancedFilterOperator> OperatorsFor(SessionColumnType type) =>
        type == SessionColumnType.Text || type == SessionColumnType.Method
            ? TextOperators
            : TypedOperators;

    public static AdvancedFilterCompilation Compile(AdvancedFilterDefinition definition)
    {
        var errors = ValidateShape(definition);
        if (errors.Count > 0)
        {
            return new(null, errors);
        }

        var active = definition.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Enabled)
            .ToArray();
        if (active.Length == 0)
        {
            return new(null, [new(-1, "Enable at least one rule.")]);
        }

        var predicates = new Dictionary<int, CompiledPredicate>();
        foreach (var (rule, index) in active)
        {
            try
            {
                predicates[index] = CompilePredicate(rule, index);
            }
            catch (FormatException exception)
            {
                errors.Add(new(index, exception.Message));
            }
        }
        if (errors.Count > 0)
        {
            return new(null, errors);
        }

        var output = new List<CompiledToken>();
        var operators = new Stack<ExpressionOperator>();
        foreach (var (item, activeIndex) in active.Select((item, index) => (item, index)))
        {
            for (var count = 0; count < item.Rule.OpenParentheses; count++)
            {
                operators.Push(ExpressionOperator.OpenParenthesis);
            }
            output.Add(new PredicateToken(predicates[item.Index]));
            for (var count = 0; count < item.Rule.CloseParentheses; count++)
            {
                while (operators.TryPeek(out var pending) && pending != ExpressionOperator.OpenParenthesis)
                {
                    output.Add(new LogicToken(operators.Pop()));
                }
                if (!operators.TryPop(out var open) || open != ExpressionOperator.OpenParenthesis)
                {
                    errors.Add(new(item.Index, "Closing parenthesis has no matching opening parenthesis."));
                    break;
                }
            }
            if (errors.Count > 0)
            {
                break;
            }
            if (activeIndex < active.Length - 1)
            {
                var next = item.Rule.Join == AdvancedFilterJoin.And ? ExpressionOperator.And : ExpressionOperator.Or;
                while (operators.TryPeek(out var pending)
                       && pending != ExpressionOperator.OpenParenthesis
                       && Precedence(pending) >= Precedence(next))
                {
                    output.Add(new LogicToken(operators.Pop()));
                }
                operators.Push(next);
            }
        }
        while (errors.Count == 0 && operators.TryPop(out var pending))
        {
            if (pending == ExpressionOperator.OpenParenthesis)
            {
                errors.Add(new(active[^1].Index, "Opening parenthesis has no matching closing parenthesis."));
                break;
            }
            output.Add(new LogicToken(pending));
        }
        return errors.Count == 0
            ? new(new CompiledAdvancedFilter(output), [])
            : new(null, errors);
    }

    private static List<AdvancedFilterValidationError> ValidateShape(AdvancedFilterDefinition? definition)
    {
        var errors = new List<AdvancedFilterValidationError>();
        if (definition?.Rules is null)
        {
            errors.Add(new(-1, "The filter has no rule list."));
            return errors;
        }
        if (definition.Rules.Count > MaximumRules)
        {
            errors.Add(new(-1, $"A filter may contain at most {MaximumRules} rules."));
            return errors;
        }
        for (var index = 0; index < definition.Rules.Count; index++)
        {
            var rule = definition.Rules[index];
            if (rule is null)
            {
                errors.Add(new(index, "Rule is missing."));
                continue;
            }
            if (!Enum.IsDefined(rule.Operator) || !Enum.IsDefined(rule.Join)
                || rule.Field is null || !Enum.IsDefined(rule.Field.Kind))
            {
                errors.Add(new(index, "Rule contains an unknown operator, join, or field kind."));
            }
            if (rule.Enabled
                && (rule.OpenParentheses is < 0 or > 3 || rule.CloseParentheses is < 0 or > 3))
            {
                errors.Add(new(index, "Parenthesis counts must be between zero and three."));
            }
            if (rule.Value?.Length > MaximumValueLength)
            {
                errors.Add(new(index, $"Rule value may contain at most {MaximumValueLength:N0} characters."));
            }
            if (rule.Field is null)
            {
                errors.Add(new(index, "Choose a field."));
            }
        }
        return errors;
    }

    private static CompiledPredicate CompilePredicate(AdvancedFilterRule rule, int ruleIndex)
    {
        SessionColumnDefinition? column = null;
        var type = SessionColumnType.Text;
        if (rule.Field.Kind == AdvancedFilterFieldKind.Column)
        {
            column = rule.Field.Column is null ? null : SessionColumnCatalog.Resolve(rule.Field.Column);
            if (column is null)
            {
                throw new FormatException("The selected column is unavailable.");
            }
            type = column.Type;
        }
        if (!(type is SessionColumnType.Text or SessionColumnType.Method
              ? TextOperators
              : TypedOperators).Contains(rule.Operator))
        {
            throw new FormatException($"{Display(rule.Operator)} is not valid for {Display(type)} fields.");
        }

        var value = rule.Value ?? "";
        if (rule.Operator is not (AdvancedFilterOperator.IsEmpty or AdvancedFilterOperator.IsNotEmpty)
            && string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Enter a comparison value.");
        }

        Regex? regex = null;
        double[]? typedValues = null;
        string[]? textValues = null;
        if (rule.Operator == AdvancedFilterOperator.MatchesRegex)
        {
            if (value.Length > MaximumRegexPatternLength)
            {
                throw new FormatException($"A regex may contain at most {MaximumRegexPatternLength:N0} characters.");
            }
            try
            {
                regex = new Regex(
                    value,
                    RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                    RegexTimeout);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException($"Invalid regular expression: {exception.Message}", exception);
            }
        }
        else if (type is not (SessionColumnType.Text or SessionColumnType.Method)
                 && rule.Operator is not (AdvancedFilterOperator.IsEmpty or AdvancedFilterOperator.IsNotEmpty))
        {
            typedValues = ParseTypedValues(value, type, rule.Operator);
        }
        else if (rule.Operator is AdvancedFilterOperator.IsOneOf or AdvancedFilterOperator.IsNotOneOf)
        {
            textValues = SplitList(value);
            if (textValues.Length == 0)
            {
                throw new FormatException("Enter at least one list value.");
            }
        }

        return new CompiledPredicate(
            ruleIndex,
            rule.Field,
            column,
            type,
            rule.Operator,
            value,
            typedValues,
            textValues,
            regex,
            rule.CaseSensitive);
    }

    private static double[] ParseTypedValues(string value, SessionColumnType type, AdvancedFilterOperator filterOperator)
    {
        var parts = filterOperator switch
        {
            AdvancedFilterOperator.Between => SplitRange(value, type),
            AdvancedFilterOperator.IsOneOf or AdvancedFilterOperator.IsNotOneOf => SplitList(value),
            _ => [value.Trim()]
        };
        var expected = filterOperator == AdvancedFilterOperator.Between ? 2 : 1;
        if (parts.Length < expected)
        {
            throw new FormatException(filterOperator == AdvancedFilterOperator.Between
                ? "Enter a range such as 200-299, 1 KB..2 MB, or two comma-separated values."
                : "Enter a valid comparison value.");
        }
        var parsed = new double[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            parsed[index] = ParseTyped(parts[index], type);
        }
        if (filterOperator == AdvancedFilterOperator.Between && parsed[0] > parsed[1])
        {
            throw new FormatException("The start of a range must not exceed its end.");
        }
        return parsed;
    }

    private static double ParseTyped(string value, SessionColumnType type)
    {
        var trimmed = value.Trim();
        if (type == SessionColumnType.Timestamp)
        {
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var timestamp)
                || DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out timestamp))
            {
                return timestamp.UtcTicks;
            }
            throw new FormatException("Enter a date/time such as 2026-10-02 13:45:00.");
        }
        if (type == SessionColumnType.Size)
        {
            return ParseWithSuffix(trimmed, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [""] = 1, ["B"] = 1, ["KB"] = 1024, ["KIB"] = 1024,
                ["MB"] = 1024 * 1024, ["MIB"] = 1024 * 1024,
                ["GB"] = 1024d * 1024 * 1024, ["GIB"] = 1024d * 1024 * 1024
            }, "Enter a size such as 512, 10 KB, or 2 MB.");
        }
        if (type == SessionColumnType.Duration)
        {
            return ParseWithSuffix(trimmed, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [""] = 1, ["MS"] = 1, ["S"] = 1000, ["M"] = 60_000, ["MIN"] = 60_000, ["H"] = 3_600_000
            }, "Enter a duration such as 250 ms, 2 s, or 1 min.");
        }
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }
        throw new FormatException(type == SessionColumnType.Status
            ? "Enter a status code, or use Between with a range such as 200-299."
            : "Enter a number.");
    }

    private static double ParseWithSuffix(string value, IReadOnlyDictionary<string, double> suffixes, string error)
    {
        var split = 0;
        while (split < value.Length && (char.IsDigit(value[split]) || value[split] is '+' or '-' or '.' or ','))
        {
            split++;
        }
        var numberText = value[..split].Trim();
        var suffix = value[split..].Trim();
        if ((double.TryParse(numberText, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
             || double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            && suffixes.TryGetValue(suffix, out var multiplier))
        {
            return number * multiplier;
        }
        throw new FormatException(error);
    }

    private static string[] SplitRange(string value, SessionColumnType type)
    {
        foreach (var separator in new[] { "..", "," })
        {
            var index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index >= 0)
            {
                return [value[..index].Trim(), value[(index + separator.Length)..].Trim()];
            }
        }
        if (type != SessionColumnType.Timestamp)
        {
            var index = value.IndexOf('-', 1);
            if (index >= 0)
            {
                return [value[..index].Trim(), value[(index + 1)..].Trim()];
            }
        }
        return [];
    }

    private static string[] SplitList(string value) =>
        value.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int Precedence(ExpressionOperator value) => value == ExpressionOperator.And ? 2 : 1;

    internal static string Display(AdvancedFilterOperator value) => value switch
    {
        AdvancedFilterOperator.NotEquals => "Not equals",
        AdvancedFilterOperator.NotContains => "Does not contain",
        AdvancedFilterOperator.IsIn => "Is in",
        AdvancedFilterOperator.IsNotIn => "Is not in",
        AdvancedFilterOperator.IsOneOf => "Is one of",
        AdvancedFilterOperator.IsNotOneOf => "Is not one of",
        AdvancedFilterOperator.StartsWith => "Starts with",
        AdvancedFilterOperator.DoesNotStartWith => "Does not start with",
        AdvancedFilterOperator.EndsWith => "Ends with",
        AdvancedFilterOperator.DoesNotEndWith => "Does not end with",
        AdvancedFilterOperator.LessThan => "Less than",
        AdvancedFilterOperator.LessThanOrEqual => "Less than or equal",
        AdvancedFilterOperator.GreaterThan => "Greater than",
        AdvancedFilterOperator.GreaterThanOrEqual => "Greater than or equal",
        AdvancedFilterOperator.IsEmpty => "Is empty",
        AdvancedFilterOperator.IsNotEmpty => "Is not empty",
        AdvancedFilterOperator.MatchesRegex => "Matches regex",
        _ => value.ToString()
    };

    private static string Display(SessionColumnType value) => value switch
    {
        SessionColumnType.Timestamp => "time",
        SessionColumnType.Duration => "duration",
        SessionColumnType.Size => "size",
        SessionColumnType.Status => "status",
        _ => value.ToString().ToLowerInvariant()
    };

    private enum ExpressionOperator
    {
        OpenParenthesis,
        And,
        Or
    }

    internal abstract record CompiledToken;

    private sealed record PredicateToken(CompiledPredicate Predicate) : CompiledToken;

    private sealed record LogicToken(ExpressionOperator Operator) : CompiledToken;

    internal sealed record CompiledPredicate(
        int RuleIndex,
        AdvancedFilterField Field,
        SessionColumnDefinition? Column,
        SessionColumnType Type,
        AdvancedFilterOperator Operator,
        string Value,
        double[]? TypedValues,
        string[]? TextValues,
        Regex? Regex,
        bool CaseSensitive);

    internal sealed class CompiledAdvancedFilter
    {
        private readonly ExpressionNode root;

        public CompiledAdvancedFilter(IReadOnlyList<CompiledToken> tokens)
        {
            var stack = new Stack<ExpressionNode>();
            foreach (var token in tokens)
            {
                if (token is PredicateToken predicate)
                {
                    stack.Push(new PredicateExpression(predicate.Predicate));
                }
                else if (token is LogicToken logic && stack.Count >= 2)
                {
                    var right = stack.Pop();
                    var left = stack.Pop();
                    stack.Push(new LogicExpression(logic.Operator, left, right));
                }
                else
                {
                    throw new InvalidOperationException("Compiled filter expression is invalid.");
                }
            }
            root = stack.Count == 1
                ? stack.Pop()
                : throw new InvalidOperationException("Compiled filter expression is invalid.");
        }

        public bool RequiresPayload => root.RequiresPayload;

        public bool Matches(SessionRow row, PayloadSearchCache payloadCache, CancellationToken cancellationToken) =>
            root.Evaluate(row, payloadCache, cancellationToken);

        private static bool Evaluate(
            CompiledPredicate predicate,
            SessionRow row,
            PayloadSearchCache payloadCache,
            CancellationToken cancellationToken)
        {
            SessionColumnValue cell;
            if (predicate.Field.Kind == AdvancedFilterFieldKind.Column)
            {
                cell = row.ColumnValue(predicate.Column!);
            }
            else
            {
                var text = payloadCache.GetBodyText(
                    row,
                    predicate.Field.Kind == AdvancedFilterFieldKind.RequestBody,
                    cancellationToken);
                cell = string.IsNullOrEmpty(text) ? new("") : new(text, text);
            }

            if (predicate.Operator == AdvancedFilterOperator.IsEmpty)
            {
                return cell.IsBlank || string.IsNullOrEmpty(cell.Display);
            }
            if (predicate.Operator == AdvancedFilterOperator.IsNotEmpty)
            {
                return !cell.IsBlank && !string.IsNullOrEmpty(cell.Display);
            }
            if (cell.IsBlank)
            {
                return false;
            }

            if (predicate.Type is not (SessionColumnType.Text or SessionColumnType.Method))
            {
                return EvaluateTyped(predicate, cell.SortValue);
            }
            return EvaluateText(predicate, cell.Display);
        }

        private static bool EvaluateTyped(CompiledPredicate predicate, IComparable? source)
        {
            var number = source switch
            {
                DateTimeOffset timestamp => timestamp.UtcTicks,
                byte value => value,
                short value => value,
                int value => value,
                long value => value,
                float value => value,
                double value => value,
                decimal value => (double)value,
                _ => double.NaN
            };
            if (double.IsNaN(number))
            {
                return false;
            }
            var values = predicate.TypedValues!;
            return predicate.Operator switch
            {
                AdvancedFilterOperator.Equals => number.Equals(values[0]),
                AdvancedFilterOperator.NotEquals => !number.Equals(values[0]),
                AdvancedFilterOperator.IsOneOf => values.Contains(number),
                AdvancedFilterOperator.IsNotOneOf => !values.Contains(number),
                AdvancedFilterOperator.LessThan => number < values[0],
                AdvancedFilterOperator.LessThanOrEqual => number <= values[0],
                AdvancedFilterOperator.GreaterThan => number > values[0],
                AdvancedFilterOperator.GreaterThanOrEqual => number >= values[0],
                AdvancedFilterOperator.Between => number >= values[0] && number <= values[1],
                _ => false
            };
        }

        private static bool EvaluateText(CompiledPredicate predicate, string source)
        {
            var comparison = predicate.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var value = predicate.Value;
            try
            {
                return predicate.Operator switch
                {
                    AdvancedFilterOperator.Equals => source.Equals(value, comparison),
                    AdvancedFilterOperator.NotEquals => !source.Equals(value, comparison),
                    AdvancedFilterOperator.Contains => source.Contains(value, comparison),
                    AdvancedFilterOperator.NotContains => !source.Contains(value, comparison),
                    AdvancedFilterOperator.IsIn => value.Contains(source, comparison),
                    AdvancedFilterOperator.IsNotIn => !value.Contains(source, comparison),
                    AdvancedFilterOperator.IsOneOf => predicate.TextValues!.Any(item => source.Equals(item, comparison)),
                    AdvancedFilterOperator.IsNotOneOf => predicate.TextValues!.All(item => !source.Equals(item, comparison)),
                    AdvancedFilterOperator.StartsWith => source.StartsWith(value, comparison),
                    AdvancedFilterOperator.DoesNotStartWith => !source.StartsWith(value, comparison),
                    AdvancedFilterOperator.EndsWith => source.EndsWith(value, comparison),
                    AdvancedFilterOperator.DoesNotEndWith => !source.EndsWith(value, comparison),
                    AdvancedFilterOperator.MatchesRegex => predicate.Regex!.IsMatch(
                        source.Length <= 1_048_576 ? source : source[..1_048_576]),
                    _ => false
                };
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new AdvancedFilterRuntimeException(
                    predicate.RuleIndex,
                    $"Regex evaluation exceeded {RegexTimeout.TotalMilliseconds:N0} ms.",
                    exception);
            }
        }

        private abstract class ExpressionNode
        {
            public abstract bool RequiresPayload { get; }

            public abstract bool Evaluate(
                SessionRow row,
                PayloadSearchCache payloadSearchCache,
                CancellationToken cancellationToken);
        }

        private sealed class PredicateExpression(CompiledPredicate predicate) : ExpressionNode
        {
            public override bool RequiresPayload => predicate.Field.Kind != AdvancedFilterFieldKind.Column;

            public override bool Evaluate(
                SessionRow row,
                PayloadSearchCache payloadSearchCache,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CompiledAdvancedFilter.Evaluate(predicate, row, payloadSearchCache, cancellationToken);
            }
        }

        private sealed class LogicExpression(
            ExpressionOperator expressionOperator,
            ExpressionNode left,
            ExpressionNode right) : ExpressionNode
        {
            public override bool RequiresPayload => left.RequiresPayload || right.RequiresPayload;

            public override bool Evaluate(
                SessionRow row,
                PayloadSearchCache payloadSearchCache,
                CancellationToken cancellationToken) =>
                expressionOperator == ExpressionOperator.And
                    ? left.Evaluate(row, payloadSearchCache, cancellationToken)
                      && right.Evaluate(row, payloadSearchCache, cancellationToken)
                    : left.Evaluate(row, payloadSearchCache, cancellationToken)
                      || right.Evaluate(row, payloadSearchCache, cancellationToken);
        }
    }
}

internal sealed record AdvancedFilterEvaluationResult(
    IReadOnlySet<SessionRow> Matches,
    int Completed);

internal static class AdvancedFilterEvaluator
{
    public static async Task<AdvancedFilterEvaluationResult> EvaluateAsync(
        AdvancedFilterCompiler.CompiledAdvancedFilter filter,
        IReadOnlyList<SessionRow> rows,
        PayloadSearchCache payloadCache,
        IProgress<PayloadSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var matches = new System.Collections.Concurrent.ConcurrentBag<SessionRow>();
        var completed = 0;
        await Parallel.ForEachAsync(rows, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
        }, (row, token) =>
        {
            if (filter.Matches(row, payloadCache, token))
            {
                matches.Add(row);
            }
            var done = Interlocked.Increment(ref completed);
            if (done == rows.Count || (done & 15) == 0)
            {
                progress?.Report(new PayloadSearchProgress(done, rows.Count));
            }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return new(matches.ToHashSet(), completed);
    }
}
