using System.Globalization;
using System.Text;

namespace Insys.BuildingBlocks.Domain.Expressions;

/// <summary>
/// The governed, typed condition language for configuration (ADR-008; SRS §7.8 asks for one, but does not define it).
/// Conditions only: no functions, no assignment, no loops, nothing that reaches outside the given variables.
/// <code>
///   expression  := or
///   or          := and ("OR" and)*
///   and         := not ("AND" not)*
///   not         := "NOT" not | comparison
///   comparison  := operand (op operand | "IN" "(" list ")" | "NOT" "IN" "(" list ")" | "BETWEEN" operand "AND" operand)?
///   op          := = | != | ≠ | &lt; | &lt;= | ≤ | &gt; | &gt;= | ≥
///   operand     := number | 'text' | true | false | identifier | "(" expression ")"
///   list        := item ("," item)*      items are numbers, 'text' or bare words such as ZA
/// </code>
/// Keywords are case-insensitive. Identifiers name variables (a-z, 0-9, _ and dots). Numbers are decimal; a missing
/// variable is an error, never a silent false.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(string text, Node root)
    {
        Text = text;
        _root = root;
        Variables = Collect(root).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    public string Text { get; }

    /// <summary>The variables the expression reads.</summary>
    public IReadOnlyList<string> Variables { get; }

    public static Result<Expression> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ExpressionErrors.Invalid("An expression is required.");
        }

        try
        {
            var parser = new Parser(Tokenizer.Tokenize(text));
            var root = parser.ParseExpression();
            parser.ExpectEnd();
            return new Expression(text, root);
        }
        catch (ExpressionException ex)
        {
            return ExpressionErrors.Invalid(ex.Message);
        }
    }

    /// <summary>Evaluates to true or false. Values may be decimal (or any number), string, bool or null.</summary>
    public Result<bool> Evaluate(IReadOnlyDictionary<string, object?> variables)
    {
        try
        {
            return Eval(_root, variables) is bool b ? b : throw new ExpressionException("The expression does not evaluate to true or false.");
        }
        catch (ExpressionException ex)
        {
            return ExpressionErrors.Evaluation(ex.Message);
        }
    }

    private static object? Eval(Node node, IReadOnlyDictionary<string, object?> variables) => node switch
    {
        Literal l => l.Value,
        Variable v => variables.TryGetValue(v.Name, out var value)
            ? Normalise(value)
            : throw new ExpressionException($"No value for '{v.Name}'."),
        Not n => !AsBool(Eval(n.Operand, variables)),
        And a => AsBool(Eval(a.Left, variables)) && AsBool(Eval(a.Right, variables)),
        Or o => AsBool(Eval(o.Left, variables)) || AsBool(Eval(o.Right, variables)),
        Compare c => Comparison(c.Op, Eval(c.Left, variables), Eval(c.Right, variables)),
        In i => i.Items.Any(item => Equal(Eval(i.Operand, variables), item)) != i.Negated,
        Between b => Comparison(">=", Eval(b.Operand, variables), Eval(b.Low, variables))
                     && Comparison("<=", Eval(b.Operand, variables), Eval(b.High, variables)),
        _ => throw new ExpressionException("Unknown expression."),
    };

    private static bool AsBool(object? value) => value as bool? ?? throw new ExpressionException("AND, OR and NOT need true or false operands.");

    private static object? Normalise(object? value) => value switch
    {
        null => null,
        bool or string or decimal => value,
        int i => (decimal)i,
        long l => (decimal)l,
        short s => (decimal)s,
        double d => (decimal)d,
        float f => (decimal)f,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        System.Text.Json.JsonElement json => json.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => json.GetDecimal(),
            System.Text.Json.JsonValueKind.String => json.GetString(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => throw new ExpressionException("Only numbers, text, true, false and null can be compared."),
        },
        _ => value.ToString(),
    };

    private static bool Equal(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (decimal a, decimal b) => a == b,
        (decimal a, string b) => decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && a == n,
        (string a, decimal b) => decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) && m == b,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (bool a, bool b) => a == b,
        _ => throw new ExpressionException($"Cannot compare {Describe(left)} with {Describe(right)}."),
    };

    private static bool Comparison(string op, object? left, object? right)
    {
        if (op is "=" or "!=")
        {
            return Equal(left, right) == (op == "=");
        }

        var order = (left, right) switch
        {
            (decimal a, decimal b) => a.CompareTo(b),
            (string a, string b) => string.CompareOrdinal(a, b),
            _ => throw new ExpressionException($"Cannot order {Describe(left)} and {Describe(right)} with {op}."),
        };
        return op switch
        {
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new ExpressionException($"Unknown operator {op}."),
        };
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        decimal => "a number",
        string => "text",
        bool => "true/false",
        _ => value.GetType().Name,
    };

    private static IEnumerable<string> Collect(Node node) => node switch
    {
        Variable v => [v.Name],
        Not n => Collect(n.Operand),
        And a => Collect(a.Left).Concat(Collect(a.Right)),
        Or o => Collect(o.Left).Concat(Collect(o.Right)),
        Compare c => Collect(c.Left).Concat(Collect(c.Right)),
        In i => Collect(i.Operand),
        Between b => Collect(b.Operand).Concat(Collect(b.Low)).Concat(Collect(b.High)),
        _ => [],
    };

    private abstract record Node;

    private sealed record Literal(object? Value) : Node;

    private sealed record Variable(string Name) : Node;

    private sealed record Not(Node Operand) : Node;

    private sealed record And(Node Left, Node Right) : Node;

    private sealed record Or(Node Left, Node Right) : Node;

    private sealed record Compare(string Op, Node Left, Node Right) : Node;

    private sealed record In(Node Operand, IReadOnlyList<object?> Items, bool Negated) : Node;

    private sealed record Between(Node Operand, Node Low, Node High) : Node;

    private enum Kind
    {
        Number,
        Text,
        Word,
        Op,
        Open,
        Close,
        Comma,
        End,
    }

    private sealed record Token(Kind Kind, string Value, int Position);

    private static class Tokenizer
    {
        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) && PrecedesValue(tokens)))
                {
                    var start = i++;
                    while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }

                    // A value such as 25-34 or 2027-01-01 in a list is a word, not arithmetic.
                    if (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '-' || text[i] == '+'))
                    {
                        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or ')' or '(')) { i++; }
                        tokens.Add(new Token(Kind.Word, text[start..i], start));
                    }
                    else
                    {
                        tokens.Add(new Token(Kind.Number, text[start..i], start));
                    }
                }
                else if (c == '\'')
                {
                    var start = ++i;
                    var builder = new StringBuilder();
                    while (true)
                    {
                        if (i >= text.Length)
                        {
                            throw new ExpressionException($"Unclosed text starting at position {start}.");
                        }

                        if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            builder.Append('\'');
                            i += 2;
                        }
                        else if (text[i] == '\'')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            builder.Append(text[i++]);
                        }
                    }

                    tokens.Add(new Token(Kind.Text, builder.ToString(), start - 1));
                }
                else if (char.IsAsciiLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-' or '+'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(Kind.Word, text[start..i], start));
                }
                else if (c is '(' or ')' or ',')
                {
                    tokens.Add(new Token(c == '(' ? Kind.Open : c == ')' ? Kind.Close : Kind.Comma, c.ToString(), i++));
                }
                else
                {
                    var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                    var op = two is "<=" or ">=" or "!=" or "<>" ? two : c is '=' or '<' or '>' or '≠' or '≤' or '≥' ? c.ToString() : null;
                    if (op is null)
                    {
                        throw new ExpressionException($"Unexpected '{c}' at position {i}.");
                    }

                    tokens.Add(new Token(Kind.Op, op switch { "≠" or "<>" => "!=", "≤" => "<=", "≥" => ">=", _ => op }, i));
                    i += op.Length is 2 && op != "≠" ? 2 : 1;
                }
            }

            tokens.Add(new Token(Kind.End, "", text.Length));
            return tokens;
        }

        private static bool PrecedesValue(List<Token> tokens) =>
            tokens.Count == 0 || tokens[^1].Kind is Kind.Op or Kind.Open or Kind.Comma || IsKeyword(tokens[^1]);

        private static bool IsKeyword(Token token) =>
            token.Kind == Kind.Word && token.Value.ToUpperInvariant() is "AND" or "OR" or "NOT" or "IN" or "BETWEEN";
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;

        private Token Current => tokens[_position];

        public Node ParseExpression() => ParseOr();

        public void ExpectEnd()
        {
            if (Current.Kind != Kind.End)
            {
                throw new ExpressionException($"Unexpected '{Current.Value}' at position {Current.Position}.");
            }
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Keyword("OR"))
            {
                left = new Or(left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (Keyword("AND"))
            {
                left = new And(left, ParseNot());
            }

            return left;
        }

        private Node ParseNot() => Keyword("NOT") ? new Not(ParseNot()) : ParseComparison();

        private Node ParseComparison()
        {
            var left = ParseOperand();
            if (Current.Kind == Kind.Op)
            {
                var op = tokens[_position++].Value;
                return new Compare(op, left, ParseOperand());
            }

            if (Keyword("BETWEEN"))
            {
                var low = ParseOperand();
                if (!Keyword("AND"))
                {
                    throw new ExpressionException($"BETWEEN needs AND at position {Current.Position}.");
                }

                return new Between(left, low, ParseOperand());
            }

            var negated = IsKeyword("NOT") && tokens[_position + 1] is { Kind: Kind.Word } next && next.Value.Equals("IN", StringComparison.OrdinalIgnoreCase);
            if (negated)
            {
                _position++;
            }

            if (Keyword("IN"))
            {
                Expect(Kind.Open, "(");
                var items = new List<object?> { ListItem() };
                while (Current.Kind == Kind.Comma)
                {
                    _position++;
                    items.Add(ListItem());
                }

                Expect(Kind.Close, ")");
                return new In(left, items, negated);
            }

            return left;
        }

        private object? ListItem()
        {
            var token = tokens[_position++];
            return token.Kind switch
            {
                Kind.Number => decimal.Parse(token.Value, NumberStyles.Number, CultureInfo.InvariantCulture),
                Kind.Text or Kind.Word => token.Value,
                _ => throw new ExpressionException($"Expected a list value at position {token.Position}."),
            };
        }

        private Node ParseOperand()
        {
            var token = tokens[_position++];
            switch (token.Kind)
            {
                case Kind.Number:
                    return new Literal(decimal.Parse(token.Value, NumberStyles.Number, CultureInfo.InvariantCulture));
                case Kind.Text:
                    return new Literal(token.Value);
                case Kind.Open:
                    var inner = ParseOr();
                    Expect(Kind.Close, ")");
                    return inner;
                case Kind.Word:
                    return token.Value.ToUpperInvariant() switch
                    {
                        "TRUE" => new Literal(true),
                        "FALSE" => new Literal(false),
                        "NULL" => new Literal(null),
                        "AND" or "OR" or "NOT" or "IN" or "BETWEEN" => throw new ExpressionException($"Unexpected {token.Value} at position {token.Position}."),
                        _ when IsIdentifier(token.Value) => new Variable(token.Value),
                        _ => throw new ExpressionException($"'{token.Value}' is not a variable name; quote text values."),
                    };
                default:
                    throw new ExpressionException(token.Kind == Kind.End ? "The expression ends too early." : $"Unexpected '{token.Value}' at position {token.Position}.");
            }
        }

        private static bool IsIdentifier(string word) => (char.IsAsciiLetter(word[0]) || word[0] == '_') && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');

        private bool IsKeyword(string keyword) => Current.Kind == Kind.Word && Current.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private bool Keyword(string keyword)
        {
            if (!IsKeyword(keyword))
            {
                return false;
            }

            _position++;
            return true;
        }

        private void Expect(Kind kind, string text)
        {
            if (Current.Kind != kind)
            {
                throw new ExpressionException($"Expected '{text}' at position {Current.Position}.");
            }

            _position++;
        }
    }

    private sealed class ExpressionException(string message) : Exception(message);
}

public static class ExpressionErrors
{
    [ErrorCode(422, "error.expression.invalid")]
    public const string InvalidCode = "EXPRESSION_INVALID";

    [ErrorCode(422, "error.expression.evaluation_failed")]
    public const string EvaluationCode = "EXPRESSION_EVALUATION_FAILED";

    public static Error Invalid(string message) => Error.Validation(InvalidCode, message);

    public static Error Evaluation(string message) => Error.Validation(EvaluationCode, message);
}
