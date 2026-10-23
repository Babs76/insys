/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
///   comparison  := sum (op sum | "IN" "(" list ")" | "NOT" "IN" "(" list ")" | "BETWEEN" sum "AND" sum)?
///   op          := = | != | ≠ | &lt; | &lt;= | ≤ | &gt; | &gt;= | ≥
///   sum         := product (("+" | "-") product)*
///   product     := unary (("*" | "/") unary)*
///   unary       := "-" unary | operand
///   operand     := number | 'text' | true | false | null | identifier | function | "(" expression ")"
///   function    := "year" "(" sum ")" | "today" "(" ")"
///   list        := item ("," item)*      items are numbers, 'text' or bare words such as ZA or 25-34
/// </code>
/// Arithmetic (Phase 4 review): numbers add, subtract, multiply and divide; a date plus or minus a number of days is a
/// date, and a date minus a date is a number of days. Dates come from variables (or text such as '2027-01-01' where a
/// date is expected). <c>year(d)</c> is a date's year; <c>today()</c> is the evaluation date the caller supplies.
/// Division by zero, overflow and dates out of range fail the evaluation.
/// Keywords are case-insensitive. Identifiers name variables (a-z, 0-9, _ and dots). Numbers are decimal; a missing
/// variable is an error, never a silent false. Limits keep evaluation cheap and safe: <see cref="MaxLength"/>
/// characters, <see cref="MaxDepth"/> levels of nesting, and <see cref="MaxSteps"/> evaluation steps. Any malformed
/// input fails with EXPRESSION_INVALID or EXPRESSION_EVALUATION_FAILED, never with another exception.
/// </summary>
public sealed class Expression
{
    /// <summary>The longest expression accepted, in characters.</summary>
    public const int MaxLength = 4000;

    /// <summary>The deepest nesting of brackets and NOTs accepted.</summary>
    public const int MaxDepth = 32;

    /// <summary>The most nodes and list comparisons one evaluation may visit.</summary>
    public const int MaxSteps = 10_000;

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

        if (text.Length > MaxLength)
        {
            return ExpressionErrors.Invalid($"An expression is at most {MaxLength} characters.");
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
        catch (Exception ex) when (ex is FormatException or OverflowException or InsufficientExecutionStackException)
        {
            return ExpressionErrors.Invalid("The expression is not well formed.");
        }
    }

    /// <summary>Evaluates to true or false. Values may be decimal (or any number), string, bool or null.</summary>
    /// <param name="maxSteps">At most <see cref="MaxSteps"/>; a lower limit can be set for a caller's own budget.</param>
    /// <param name="today">The date <c>today()</c> returns; an expression that uses it fails without one.</param>
    public Result<bool> Evaluate(IReadOnlyDictionary<string, object?> variables, int maxSteps = MaxSteps, DateOnly? today = null)
    {
        try
        {
            var steps = new StepCounter(Math.Min(maxSteps, MaxSteps), today);
            return Eval(_root, variables, steps) is bool b ? b : throw new ExpressionException("The expression does not evaluate to true or false.");
        }
        catch (ExpressionException ex)
        {
            return ExpressionErrors.Evaluation(ex.Message);
        }
        catch (DivideByZeroException)
        {
            return ExpressionErrors.Evaluation("Division by zero.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or InsufficientExecutionStackException
                                       or ArgumentOutOfRangeException)
        {
            return ExpressionErrors.Evaluation("A value could not be calculated or compared.");
        }
    }

    private static object? Eval(Node node, IReadOnlyDictionary<string, object?> variables, StepCounter steps)
    {
        steps.Take(1);
        return node switch
        {
            Literal l => l.Value,
            Variable v => variables.TryGetValue(v.Name, out var value)
                ? Normalise(value)
                : throw new ExpressionException($"No value for '{v.Name}'."),
            Not n => !AsBool(Eval(n.Operand, variables, steps)),
            And a => AsBool(Eval(a.Left, variables, steps)) && AsBool(Eval(a.Right, variables, steps)),
            Or o => AsBool(Eval(o.Left, variables, steps)) || AsBool(Eval(o.Right, variables, steps)),
            Compare c => Comparison(c.Op, Eval(c.Left, variables, steps), Eval(c.Right, variables, steps)),
            In i => InList(i, Eval(i.Operand, variables, steps), steps),
            Between b => Comparison(">=", Eval(b.Operand, variables, steps), Eval(b.Low, variables, steps))
                         && Comparison("<=", Eval(b.Operand, variables, steps), Eval(b.High, variables, steps)),
            Arithmetic a => Calculate(a.Op, Eval(a.Left, variables, steps), Eval(a.Right, variables, steps)),
            Negate n => Eval(n.Operand, variables, steps) is decimal d ? -d : throw new ExpressionException("Only a number is negated."),
            Year y => AsDate(Eval(y.Operand, variables, steps)) is { } date ? (decimal)date.Year : throw new ExpressionException("year() needs a date."),
            Today => steps.Today ?? throw new ExpressionException("today() needs the evaluation date."),
            _ => throw new ExpressionException("Unknown expression."),
        };
    }

    /// <summary>Numbers, and dates plus or minus days; a date minus a date is days.</summary>
    private static object Calculate(char op, object? left, object? right)
    {
        if (left is decimal a && right is decimal b)
        {
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                _ => b == 0 ? throw new DivideByZeroException() : a / b,
            };
        }

        var leftDate = AsDate(left);
        var rightDate = AsDate(right);
        return (op, leftDate, rightDate, left, right) switch
        {
            ('+', { } d, null, _, decimal days) => d.AddDays(Days(days)),
            ('+', null, { } d, decimal days, _) => d.AddDays(Days(days)),
            ('-', { } d, null, _, decimal days) => d.AddDays(-Days(days)),
            ('-', { } from, { } to, _, _) => (decimal)(from.DayNumber - to.DayNumber),
            _ => throw new ExpressionException($"Cannot apply {op} to {Describe(left)} and {Describe(right)}."),
        };
    }

    private static int Days(decimal days) =>
        decimal.Truncate(days) == days && days is >= -3_650_000 and <= 3_650_000 ? (int)days : throw new ExpressionException("Days are a whole number.");

    /// <summary>A date, or text that is a date (yyyy-MM-dd); otherwise null.</summary>
    private static DateOnly? AsDate(object? value) => value switch
    {
        DateOnly date => date,
        string text when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
        _ => null,
    };

    private static bool InList(In list, object? value, StepCounter steps)
    {
        steps.Take(list.Items.Count);
        return list.Items.Any(item => Equal(value, item)) != list.Negated;
    }

    private sealed class StepCounter(int limit, DateOnly? today)
    {
        private int _steps;

        public DateOnly? Today => today;

        public void Take(int count)
        {
            _steps += count;
            if (_steps > limit)
            {
                throw new ExpressionException($"The expression needs more than {limit} evaluation steps.");
            }
        }
    }

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
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
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
        (DateOnly, _) or (_, DateOnly) when AsDate(left) is { } x && AsDate(right) is { } y => x == y,
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
            (DateOnly, _) or (_, DateOnly) when AsDate(left) is { } x && AsDate(right) is { } y => x.CompareTo(y),
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
        DateOnly => "a date",
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
        Arithmetic a => Collect(a.Left).Concat(Collect(a.Right)),
        Negate n => Collect(n.Operand),
        Year y => Collect(y.Operand),
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

    private sealed record Arithmetic(char Op, Node Left, Node Right) : Node;

    private sealed record Negate(Node Operand) : Node;

    private sealed record Year(Node Operand) : Node;

    private sealed record Today : Node;

    private enum Kind
    {
        Number,
        Text,
        Word,
        Op,
        Arith,
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
            var inList = false;
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

                    // A value such as 25-34 or 2027-01-01 in a list is a word; elsewhere - and + are arithmetic.
                    if (i < text.Length && (char.IsAsciiLetter(text[i]) || (inList && text[i] is '-' or '+')))
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
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' || (inList && text[i] is '-' or '+')))
                    {
                        i++;
                    }

                    tokens.Add(new Token(Kind.Word, text[start..i], start));
                }
                else if (c is '(' or ')' or ',')
                {
                    inList = c == '(' ? tokens.Count > 0 && tokens[^1] is { Kind: Kind.Word } last && last.Value.Equals("IN", StringComparison.OrdinalIgnoreCase)
                        : c == ')' ? false : inList;
                    tokens.Add(new Token(c == '(' ? Kind.Open : c == ')' ? Kind.Close : Kind.Comma, c.ToString(), i++));
                }
                else if (c is '+' or '-' or '*' or '/')
                {
                    tokens.Add(new Token(Kind.Arith, c.ToString(), i++));
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
            tokens.Count == 0 || tokens[^1].Kind is Kind.Op or Kind.Arith or Kind.Open or Kind.Comma || IsKeyword(tokens[^1]);

        private static bool IsKeyword(Token token) =>
            token.Kind == Kind.Word && token.Value.ToUpperInvariant() is "AND" or "OR" or "NOT" or "IN" or "BETWEEN";
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;
        private int _depth;

        private Token Current => tokens[Math.Min(_position, tokens.Count - 1)];

        private Token Next() => tokens[Math.Min(_position++, tokens.Count - 1)];

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

        private Node ParseNot()
        {
            if (!Keyword("NOT"))
            {
                return ParseComparison();
            }

            Enter();
            var operand = new Not(ParseNot());
            _depth--;
            return operand;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth)
            {
                throw new ExpressionException($"The expression is nested more than {MaxDepth} levels deep.");
            }
        }

        private Node ParseComparison()
        {
            var left = ParseSum();
            if (Current.Kind == Kind.Op)
            {
                var op = Next().Value;
                return new Compare(op, left, ParseSum());
            }

            if (Keyword("BETWEEN"))
            {
                var low = ParseSum();
                if (!Keyword("AND"))
                {
                    throw new ExpressionException($"BETWEEN needs AND at position {Current.Position}.");
                }

                return new Between(left, low, ParseSum());
            }

            var negated = IsKeyword("NOT") && _position + 1 < tokens.Count && tokens[_position + 1] is { Kind: Kind.Word } next
                && next.Value.Equals("IN", StringComparison.OrdinalIgnoreCase);
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
            var token = Next();
            return token.Kind switch
            {
                Kind.Number => Number(token),
                Kind.Text or Kind.Word => token.Value,
                _ => throw new ExpressionException($"Expected a list value at position {token.Position}."),
            };
        }

        private Node ParseSum()
        {
            var left = ParseProduct();
            while (Current is { Kind: Kind.Arith, Value: "+" or "-" })
            {
                var op = Next().Value[0];
                left = new Arithmetic(op, left, ParseProduct());
            }

            return left;
        }

        private Node ParseProduct()
        {
            var left = ParseUnary();
            while (Current is { Kind: Kind.Arith, Value: "*" or "/" })
            {
                var op = Next().Value[0];
                left = new Arithmetic(op, left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Current is not { Kind: Kind.Arith, Value: "-" })
            {
                return ParseOperand();
            }

            _position++;
            Enter();
            var operand = new Negate(ParseUnary());
            _depth--;
            return operand;
        }

        private Node ParseOperand()
        {
            var token = Next();
            if (token.Kind == Kind.Word && Current.Kind == Kind.Open && token.Value.ToUpperInvariant() is "YEAR" or "TODAY")
            {
                _position++;
                Enter();
                Node call = token.Value.Equals("TODAY", StringComparison.OrdinalIgnoreCase) ? new Today() : new Year(ParseSum());
                Expect(Kind.Close, ")");
                _depth--;
                return call;
            }

            switch (token.Kind)
            {
                case Kind.Number:
                    return new Literal(Number(token));
                case Kind.Text:
                    return new Literal(token.Value);
                case Kind.Open:
                    Enter();
                    var inner = ParseOr();
                    Expect(Kind.Close, ")");
                    _depth--;
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

        private static decimal Number(Token token) =>
            decimal.TryParse(token.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new ExpressionException($"'{token.Value}' at position {token.Position} is not a number.");

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
