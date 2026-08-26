using System.Globalization;
using System.Text.Json;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// The <c>calc</c> tool: a small recursive-descent evaluator over doubles.
///
/// Artillery convention throughout the function table: forward trig takes DEGREES, inverse trig
/// returns degrees. That is why <c>mod360</c> exists — <c>%</c> is C#'s double remainder, whose
/// sign follows the dividend, so bearings need explicit normalisation.
///
/// Never throws: each expression in a batch is evaluated independently and a failure turns into
/// an "error:" line.
/// </summary>
public static class Calculator
{
    private const double Deg = Math.PI / 180.0;

    /// <summary>Convenience overload for the tool dispatcher: reads the "expression" field.</summary>
    public static string Evaluate(JsonElement args)
    {
        string? expression = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("expression", out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            expression = el.GetString();
        }
        return Evaluate(expression);
    }

    /// <summary>
    /// Evaluates a ';'-separated batch. One line out per expression; one failing expression
    /// never affects the others.
    /// </summary>
    public static string Evaluate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "need expression";

        var lines = new List<string>();
        foreach (var raw in expression.Split(';'))
        {
            var expr = raw.Trim();
            if (expr.Length == 0) continue;

            try
            {
                var value = new Parser(expr).Evaluate();

                // Division by zero and friends produce Infinity/NaN. Handing those to the LLM as
                // a "result" invites it to fire at a non-finite coordinate, so they are errors.
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    lines.Add($"{expr} → error: 表达式结果非有限数值");
                }
                else
                {
                    lines.Add($"{expr} = {value.ToString("G10", CultureInfo.InvariantCulture)}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"{expr} → error: {ex.Message}");
            }
        }

        if (lines.Count == 0) return "empty expression";
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Grammar, lowest binding first:
    /// <code>
    /// Expr    := Term (('+' | '-') Term)*            left associative
    /// Term    := Factor (('*' | '/' | '%') Factor)*  left associative
    /// Factor  := ('-' | '+') Factor
    ///          | Primary ('^' Factor)?               '^' right associative
    /// Primary := '(' Expr ')' | number | ident [ '(' args ')' ]
    /// </code>
    /// '^' therefore binds tighter than unary minus (-3^2 = -9, the mathematical convention)
    /// while a unary sign on its right-hand side is absorbed by the Factor recursion (2^-3).
    /// </summary>
    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;

        public Parser(string s) => _s = s;

        public double Evaluate()
        {
            var value = ParseExpr();
            SkipWhitespace();
            if (_pos < _s.Length) throw Fail($"unexpected '{_s[_pos]}' at position {_pos}");
            return value;
        }

        private double ParseExpr()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _s.Length) return left;
                var c = _s[_pos];
                if (c != '+' && c != '-') return left;
                _pos++;
                var right = ParseTerm();
                left = c == '+' ? left + right : left - right;
            }
        }

        private double ParseTerm()
        {
            var left = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _s.Length) return left;
                var c = _s[_pos];
                if (c != '*' && c != '/' && c != '%') return left;
                _pos++;
                var right = ParseFactor();
                left = c switch
                {
                    '*' => left * right,
                    '/' => left / right,
                    _ => left % right,
                };
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            if (_pos < _s.Length && _s[_pos] == '-')
            {
                _pos++;
                return -ParseFactor();
            }
            if (_pos < _s.Length && _s[_pos] == '+')
            {
                _pos++;
                return ParseFactor();
            }

            var value = ParsePrimary();
            SkipWhitespace();
            if (_pos < _s.Length && _s[_pos] == '^')
            {
                _pos++;
                return Math.Pow(value, ParseFactor());
            }
            return value;
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _s.Length) throw Fail("unexpected end of expression");

            var c = _s[_pos];

            if (c == '(')
            {
                _pos++;
                var inner = ParseExpr();
                Expect(')');
                return inner;
            }

            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c)) return ParseIdentifier();

            throw Fail($"unexpected '{c}' at position {_pos}");
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < _s.Length)
            {
                var c = _s[_pos];
                if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E')
                {
                    _pos++;
                }
                else if ((c == '+' || c == '-') && _pos > start && (_s[_pos - 1] == 'e' || _s[_pos - 1] == 'E'))
                {
                    // Exponent sign only; a '+' anywhere else is an operator.
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            var text = _s[start.._pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw Fail($"bad number '{text}'");
            }
            return value;
        }

        private double ParseIdentifier()
        {
            var start = _pos;
            while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;

            // Names are case insensitive.
            var name = _s[start.._pos].ToLowerInvariant();

            SkipWhitespace();
            if (_pos < _s.Length && _s[_pos] == '(')
            {
                _pos++;
                var args = new List<double>();
                // At least one argument: a zero-arg call trips the ')' as unexpected.
                args.Add(ParseExpr());
                while (true)
                {
                    SkipWhitespace();
                    if (_pos < _s.Length && _s[_pos] == ',')
                    {
                        _pos++;
                        args.Add(ParseExpr());
                        continue;
                    }
                    break;
                }
                Expect(')');
                return Call(name, args);
            }

            return name switch
            {
                "pi" => Math.PI,
                "e" => Math.E,
                _ => throw Fail($"unknown constant '{name}'"),
            };
        }

        private double Call(string name, List<double> args)
        {
            switch (name)
            {
                // min/max are the only variadic functions; everything else is strict arity.
                case "min": return Min(args);
                case "max": return Max(args);

                case "atan2": Arity(name, args, 2); return Math.Atan2(args[0], args[1]) / Deg;
                case "pow": Arity(name, args, 2); return Math.Pow(args[0], args[1]);
                case "hypot": Arity(name, args, 2); return Math.Sqrt(args[0] * args[0] + args[1] * args[1]);

                case "sin": Arity(name, args, 1); return Math.Sin(args[0] * Deg);
                case "cos": Arity(name, args, 1); return Math.Cos(args[0] * Deg);
                case "tan": Arity(name, args, 1); return Math.Tan(args[0] * Deg);
                case "asin": Arity(name, args, 1); return Math.Asin(args[0]) / Deg;
                case "acos": Arity(name, args, 1); return Math.Acos(args[0]) / Deg;
                case "atan": Arity(name, args, 1); return Math.Atan(args[0]) / Deg;
                case "sqrt": Arity(name, args, 1); return Math.Sqrt(args[0]);
                case "abs": Arity(name, args, 1); return Math.Abs(args[0]);
                case "ln": Arity(name, args, 1); return Math.Log(args[0]);
                case "log10": Arity(name, args, 1); return Math.Log10(args[0]);
                case "exp": Arity(name, args, 1); return Math.Exp(args[0]);
                case "floor": Arity(name, args, 1); return Math.Floor(args[0]);
                case "ceil": Arity(name, args, 1); return Math.Ceiling(args[0]);
                // Away from zero, not banker's rounding: round(0.5) = 1.
                case "round": Arity(name, args, 1); return Math.Round(args[0], MidpointRounding.AwayFromZero);
                // Normalises a bearing into [0, 360).
                case "mod360": Arity(name, args, 1); return (args[0] % 360.0 + 360.0) % 360.0;

                default: throw Fail($"unknown function '{name}'");
            }
        }

        private static double Min(List<double> args)
        {
            var value = args[0];
            for (var i = 1; i < args.Count; i++) value = Math.Min(value, args[i]);
            return value;
        }

        private static double Max(List<double> args)
        {
            var value = args[0];
            for (var i = 1; i < args.Count; i++) value = Math.Max(value, args[i]);
            return value;
        }

        private static void Arity(string name, List<double> args, int expected)
        {
            if (args.Count == expected) return;
            throw Fail(expected == 1 ? $"{name} takes 1 argument" : $"{name} takes {expected} arguments");
        }

        private void Expect(char c)
        {
            SkipWhitespace();
            if (_pos >= _s.Length || _s[_pos] != c) throw Fail($"expected '{c}' at position {_pos}");
            _pos++;
        }

        private void SkipWhitespace()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
        }

        private static Exception Fail(string message) => new FormatException(message);
    }
}
