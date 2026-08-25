namespace IronNestAgentBridge.Agent;

/// <summary>
/// Tiny expression evaluator for the calc tool — the LLM's scratch arithmetic (trig
/// included) is unreliable, so it hands the expression over instead. Artillery convention
/// throughout: trig takes DEGREES and inverse trig returns DEGREES.
/// </summary>
public static class Calculator
{
    /// <summary>Evaluate one or more ';'-separated expressions; returns one "expr = value" line each.</summary>
    public static string Evaluate(string input)
    {
        var lines = new List<string>();
        foreach (var raw in input.Split(';'))
        {
            var expr = raw.Trim();
            if (expr.Length == 0)
                continue;
            try
            {
                var value = new Parser(expr).ParseFull();
                lines.Add($"{expr} = {value:G10}");
            }
            catch (Exception ex)
            {
                lines.Add($"{expr} → error: {ex.Message}");
            }
        }
        return lines.Count > 0 ? string.Join("\n", lines) : "empty expression";
    }

    private const double Deg = Math.PI / 180.0;

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public double ParseFull()
        {
            var v = ParseExpr();
            SkipSpace();
            if (_i < _s.Length)
                throw new Exception($"unexpected '{_s[_i]}' at position {_i}");
            return v;
        }

        private double ParseExpr()
        {
            var v = ParseTerm();
            while (true)
            {
                SkipSpace();
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        private double ParseTerm()
        {
            var v = ParseFactor();
            while (true)
            {
                SkipSpace();
                if (Eat('*')) v *= ParseFactor();
                else if (Eat('/')) v /= ParseFactor();
                else if (Eat('%')) v %= ParseFactor();
                else return v;
            }
        }

        // '^' is right-associative and binds tighter than unary minus: -3^2 = -(3^2) = -9,
        // matching mathematical convention (and 2^-3 still parses via the recursive call).
        private double ParseFactor()
        {
            SkipSpace();
            if (Eat('-'))
                return -ParseFactor();
            var v = ParsePrimary();
            SkipSpace();
            return Eat('^') ? Math.Pow(v, ParseFactor()) : v;
        }

        private double ParsePrimary()
        {
            SkipSpace();
            if (_i >= _s.Length)
                throw new Exception("unexpected end of expression");

            if (Eat('('))
            {
                var v = ParseExpr();
                Expect(')');
                return v;
            }

            var c = _s[_i];
            if (char.IsDigit(c) || c == '.')
            {
                var start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] is '.' or 'e' or 'E'
                       || (_s[_i] is '+' or '-' && _s[_i - 1] is 'e' or 'E')))
                    _i++;
                var text = _s.Substring(start, _i - start);
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var num)
                    ? num
                    : throw new Exception($"bad number '{text}'");
            }

            if (char.IsLetter(c))
            {
                var start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
                    _i++;
                var name = _s.Substring(start, _i - start).ToLowerInvariant();
                SkipSpace();
                if (_i < _s.Length && _s[_i] == '(')
                    return Call(name);
                return name switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    _ => throw new Exception($"unknown constant '{name}'"),
                };
            }

            throw new Exception($"unexpected '{c}' at position {_i}");
        }

        private double Call(string name)
        {
            Expect('(');
            var args = new List<double> { ParseExpr() };
            SkipSpace();
            while (Eat(','))
            {
                args.Add(ParseExpr());
                SkipSpace();
            }
            Expect(')');

            double A1() => args.Count == 1 ? args[0] : throw new Exception($"{name} takes 1 argument");
            (double, double) A2() => args.Count == 2 ? (args[0], args[1]) : throw new Exception($"{name} takes 2 arguments");

            switch (name)
            {
                case "sin": return Math.Sin(A1() * Deg);
                case "cos": return Math.Cos(A1() * Deg);
                case "tan": return Math.Tan(A1() * Deg);
                case "asin": return Math.Asin(A1()) / Deg;
                case "acos": return Math.Acos(A1()) / Deg;
                case "atan": return Math.Atan(A1()) / Deg;
                case "atan2": { var (y, x) = A2(); return Math.Atan2(y, x) / Deg; }
                case "sqrt": return Math.Sqrt(A1());
                case "abs": return Math.Abs(A1());
                case "ln": return Math.Log(A1());
                case "log10": return Math.Log10(A1());
                case "exp": return Math.Exp(A1());
                case "floor": return Math.Floor(A1());
                case "ceil": return Math.Ceiling(A1());
                case "round": return Math.Round(A1());
                case "pow": { var (a, b) = A2(); return Math.Pow(a, b); }
                case "min": return args.Min();
                case "max": return args.Max();
                case "hypot": { var (a, b) = A2(); return Math.Sqrt(a * a + b * b); }
                case "mod360": return ((A1() % 360.0) + 360.0) % 360.0;
                default: throw new Exception($"unknown function '{name}'");
            }
        }

        private void SkipSpace()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                _i++;
        }

        private bool Eat(char c)
        {
            if (_i < _s.Length && _s[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        private void Expect(char c)
        {
            SkipSpace();
            if (!Eat(c))
                throw new Exception($"expected '{c}' at position {_i}");
        }
    }
}
