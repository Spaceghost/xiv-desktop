using System.Globalization;

namespace XivDesktop.Core.Palette;

/// <summary>
/// A small, safe arithmetic evaluator for the palette: numbers, <c>+ - * / % ^</c> (and <c>**</c>), parentheses,
/// unary minus, the constants <c>pi</c>, <c>e</c> and <c>tau</c>, and the functions <c>sqrt abs round floor
/// ceil sin cos tan asin acos atan ln log log2 exp min max</c>. Nothing else: no variables, no assignment,
/// no reflection, no string evaluation. Input is limited in length and nesting.
/// </summary>
public static class Calculator
{
    public const int MaxLength = 256;
    private const int MaxDepth = 32;

    /// <summary>Evaluates <paramref name="expression"/>; false with a reason when it is not arithmetic or not finite.</summary>
    public static bool TryEvaluate(string? expression, out double value, out string error)
    {
        value = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "empty";
            return false;
        }

        if (expression.Length > MaxLength)
        {
            error = "too long";
            return false;
        }

        try
        {
            var p = new Parser(expression);
            value = p.ParseExpression(0);
            p.SkipSpace();
            if (!p.AtEnd)
                throw new FormatException($"unexpected '{p.Current}'");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException(double.IsNaN(value) ? "not a number" : "infinite");
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// True when the text looks like it was meant as arithmetic (it has an operator or a function and a digit
    /// or constant), so the palette only offers a calculator row for "2+2" or "sqrt 2", not for "firefox".
    /// </summary>
    public static bool LooksLikeMath(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > MaxLength)
            return false;
        var hasDigit = t.Any(char.IsDigit) || ContainsWord(t, "pi") || ContainsWord(t, "tau");
        var hasOp = t.IndexOfAny(['+', '*', '/', '^', '%', '(']) >= 0 || (t.IndexOf('-', 1) > 0);
        return hasDigit && (hasOp || Functions.Keys.Any(f => ContainsWord(t, f))) && TryEvaluate(t, out _, out _);
    }

    private static bool ContainsWord(string t, string w)
    {
        var i = t.IndexOf(w, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && (i == 0 || !char.IsLetter(t[i - 1])) && (i + w.Length == t.Length || !char.IsLetter(t[i + w.Length]));
    }

    /// <summary>Formats a result compactly: integers without a fraction, others to 12 significant digits.</summary>
    public static string Format(double v)
    {
        if (Math.Abs(v) < 1e15 && Math.Abs(v - Math.Round(v)) < 1e-9 * Math.Max(1, Math.Abs(v)))
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        return v.ToString("G12", CultureInfo.InvariantCulture);
    }

    private static readonly Dictionary<string, (int Arity, Func<double[], double> F)> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = (1, a => Math.Sqrt(a[0])),
        ["abs"] = (1, a => Math.Abs(a[0])),
        ["round"] = (1, a => Math.Round(a[0], MidpointRounding.AwayFromZero)),
        ["floor"] = (1, a => Math.Floor(a[0])),
        ["ceil"] = (1, a => Math.Ceiling(a[0])),
        ["sin"] = (1, a => Math.Sin(a[0])),
        ["cos"] = (1, a => Math.Cos(a[0])),
        ["tan"] = (1, a => Math.Tan(a[0])),
        ["asin"] = (1, a => Math.Asin(a[0])),
        ["acos"] = (1, a => Math.Acos(a[0])),
        ["atan"] = (1, a => Math.Atan(a[0])),
        ["ln"] = (1, a => Math.Log(a[0])),
        ["log"] = (1, a => Math.Log10(a[0])),
        ["log2"] = (1, a => Math.Log2(a[0])),
        ["exp"] = (1, a => Math.Exp(a[0])),
        ["min"] = (2, a => Math.Min(a[0], a[1])),
        ["max"] = (2, a => Math.Max(a[0], a[1])),
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E,
        ["tau"] = Math.Tau,
    };

    private sealed class Parser(string s)
    {
        private int i;
        private int depth;

        public bool AtEnd => i >= s.Length;

        public char Current => s[i];

        public void SkipSpace()
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
        }

        // Precedence climbing: + - (1), * / % (2), unary minus (3), ^ (4, right-associative).
        public double ParseExpression(int minPrec)
        {
            if (++depth > MaxDepth)
                throw new FormatException("too deeply nested");
            var left = ParseUnary();
            while (true)
            {
                SkipSpace();
                if (AtEnd)
                    break;
                var (op, prec, len, right) = PeekOp();
                if (op == '\0' || prec < minPrec)
                    break;
                i += len;
                var rhs = ParseExpression(right ? prec : prec + 1);
                left = op switch
                {
                    '+' => left + rhs,
                    '-' => left - rhs,
                    '*' => left * rhs,
                    '/' => rhs == 0 ? throw new FormatException("division by zero") : left / rhs,
                    '%' => rhs == 0 ? throw new FormatException("division by zero") : left % rhs,
                    _ => Math.Pow(left, rhs),
                };
            }

            depth--;
            return left;
        }

        private (char Op, int Prec, int Len, bool RightAssoc) PeekOp()
        {
            var c = s[i];
            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
                return ('^', 4, 2, true);
            return c switch
            {
                '+' or '-' => (c, 1, 1, false),
                '*' or '/' or '%' or '×' or '÷' => (c switch { '×' => '*', '÷' => '/', _ => c }, 2, 1, false),
                '^' => ('^', 4, 1, true),
                _ => ('\0', 0, 0, false),
            };
        }

        private double ParseUnary()
        {
            SkipSpace();
            if (AtEnd)
                throw new FormatException("incomplete expression");
            if (s[i] == '-')
            {
                i++;
                // -2^2 = -(2^2), as in maths
                return -ParseExpression(3);
            }

            if (s[i] == '+')
            {
                i++;
                return ParseExpression(3);
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpace();
            if (AtEnd)
                throw new FormatException("incomplete expression");
            var c = s[i];
            if (c == '(')
            {
                i++;
                var v = ParseExpression(0);
                Expect(')');
                return v;
            }

            if (char.IsDigit(c) || c == '.')
                return ParseNumber();

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < s.Length && char.IsLetterOrDigit(s[i]))
                    i++;
                var name = s[start..i];
                if (Functions.TryGetValue(name, out var fn))
                {
                    SkipSpace();
                    var args = new List<double>();
                    if (!AtEnd && s[i] == '(')
                    {
                        i++;
                        args.Add(ParseExpression(0));
                        SkipSpace();
                        while (!AtEnd && s[i] == ',')
                        {
                            i++;
                            args.Add(ParseExpression(0));
                            SkipSpace();
                        }

                        Expect(')');
                    }
                    else if (fn.Arity == 1)
                    {
                        args.Add(ParseExpression(3)); // "sqrt 16"
                    }

                    if (args.Count != fn.Arity)
                        throw new FormatException($"{name} takes {fn.Arity} argument{(fn.Arity == 1 ? "" : "s")}");
                    return fn.F([.. args]);
                }

                if (Constants.TryGetValue(name, out var k))
                    return k;
                throw new FormatException($"unknown name '{name}'");
            }

            throw new FormatException($"unexpected '{c}'");
        }

        private double ParseNumber()
        {
            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '_'))
                i++;
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || ((s[i + 1] == '-' || s[i + 1] == '+') && i + 2 < s.Length && char.IsDigit(s[i + 2]))))
            {
                i += 2;
                while (i < s.Length && char.IsDigit(s[i]))
                    i++;
            }

            var text = s[start..i].Replace("_", "", StringComparison.Ordinal);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"bad number '{text}'");
            return v;
        }

        private void Expect(char c)
        {
            SkipSpace();
            if (AtEnd || s[i] != c)
                throw new FormatException($"expected '{c}'");
            i++;
        }
    }
}
