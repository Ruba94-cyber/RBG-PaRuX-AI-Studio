using System.Globalization;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Evaluates only short, explicit arithmetic expressions. This avoids waking a multi-billion
/// parameter model for calculator work while deliberately refusing prose, dates, identifiers and
/// unsupported syntax so normal routing remains authoritative for every ambiguous request.
/// </summary>
public static partial class LocalArithmeticAnswer
{
    private const int MaximumExpressionLength = 128;
    private const double MaximumAbsoluteResult = 1e100;

    [GeneratedRegex(@"^\s*(?:(?:quanto\s+fa|calcola|calculate|what\s+is)\s+)?(?<expression>[0-9\s.,+\-*/()×÷]+)\s*\??\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitExpression();

    [GeneratedRegex(@"^\d{4}-\d{1,2}-\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    public static bool TryCreate(string? prompt, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaximumExpressionLength) return false;

        Match match = ExplicitExpression().Match(prompt);
        if (!match.Success) return false;
        string expression = match.Groups["expression"].Value.Trim();
        if (IsoDate().IsMatch(expression) || !expression.Any(c => "+-*/×÷".Contains(c))) return false;

        try
        {
            var parser = new Parser(expression);
            double value = parser.Parse();
            if (!double.IsFinite(value) || Math.Abs(value) > MaximumAbsoluteResult) return false;
            answer = value.ToString("0.###############", CultureInfo.InvariantCulture);
            return true;
        }
        catch (DivideByZeroException)
        {
            bool italian = Regex.IsMatch(prompt, @"\b(quanto|calcola)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            answer = italian ? "Impossibile dividere per zero." : "Division by zero is undefined.";
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _text;
        private int _position;

        public Parser(string text)
        {
            _text = text.AsSpan();
            _position = 0;
        }

        public double Parse()
        {
            double value = ParseExpression();
            SkipWhitespace();
            if (_position != _text.Length) throw new FormatException();
            return value;
        }

        private double ParseExpression()
        {
            double value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Consume('+')) value += ParseTerm();
                else if (Consume('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            double value = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (Consume('*') || Consume('×')) value *= ParseFactor();
                else if (Consume('/') || Consume('÷'))
                {
                    double divisor = ParseFactor();
                    if (divisor == 0) throw new DivideByZeroException();
                    value /= divisor;
                }
                else return value;
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            if (Consume('+')) return ParseFactor();
            if (Consume('-')) return -ParseFactor();
            if (Consume('('))
            {
                double groupedValue = ParseExpression();
                SkipWhitespace();
                if (!Consume(')')) throw new FormatException();
                return groupedValue;
            }

            int start = _position;
            bool decimalSeen = false;
            while (_position < _text.Length)
            {
                char current = _text[_position];
                if (char.IsDigit(current))
                {
                    _position++;
                    continue;
                }
                if ((current == '.' || current == ',') && !decimalSeen)
                {
                    decimalSeen = true;
                    _position++;
                    continue;
                }
                break;
            }
            if (start == _position) throw new FormatException();
            string token = _text[start.._position].ToString().Replace(',', '.');
            if (!double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
                throw new FormatException();
            return value;
        }

        private bool Consume(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected) return false;
            _position++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }
    }
}
