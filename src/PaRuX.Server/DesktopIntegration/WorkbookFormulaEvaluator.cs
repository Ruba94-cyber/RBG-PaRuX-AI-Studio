using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.DesktopIntegration;

internal sealed record WorkbookFormulaResult(
    string SheetName,
    string CellReference,
    bool Evaluated,
    double? NumericValue,
    string? Error);

/// <summary>
/// A deliberately bounded local evaluator for the formula subset PaRuX can verify itself. It is not
/// advertised as an Excel-compatible calculation engine. Supported expressions are numeric literals,
/// + - * / ^, parentheses, A1 references (including quoted cross-sheet references), rectangular ranges
/// inside multi-cell functions (SUM, AVERAGE, MIN, MAX, COUNT, PRODUCT), standard mathematical and rounding
/// functions (ABS, SQRT, INT, SIGN, POWER, MOD, ROUND, ROUNDUP, ROUNDDOWN, TRUNC, LN, EXP, PI),
/// and formulas depending on other supported formulas. Unsupported Excel functions remain genuine
/// formulas in the XLSX with no invented cached value and are recalculated by Excel/LibreOffice on open.
/// </summary>
internal sealed class WorkbookFormulaEvaluator
{
    private static readonly Regex ReferenceAtStart = new(
        @"^(?:(?:'((?:[^']|'')+)'|([A-Za-z_][A-Za-z0-9_.]*))!)?\$?([A-Za-z]{1,3})\$?([1-9][0-9]{0,6})(?::\$?([A-Za-z]{1,3})\$?([1-9][0-9]{0,6}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly Dictionary<string, double> _literalNumbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _formulas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _unsupportedFormulas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _evaluated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sheetLookup = new(StringComparer.OrdinalIgnoreCase);

    internal WorkbookFormulaEvaluator(WorkbookContent content)
        : this(content, existingWorkbook: null)
    {
    }

    internal WorkbookFormulaEvaluator(WorkbookContent content, WorkbookContent? existingWorkbook)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (WorkbookSheet sheet in existingWorkbook?.Sheets ?? new List<WorkbookSheet>())
        {
            string name = sheet.Name ?? string.Empty;
            if (name.Length == 0)
                throw new InvalidDataException("Existing workbook contains an empty sheet name.");
            if (_sheetLookup.ContainsKey(name))
                throw new InvalidDataException("Existing workbook contains duplicate sheet name after Excel normalization: " + name);
            _sheetLookup[name] = name;
            AddSheetContent(name, sheet, replaceCells: false);
        }

        uint patchSheetId = 1;
        var patchSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkbookSheet sheet in content.Sheets ?? new List<WorkbookSheet>())
        {
            string requestedName = OfficeDocumentWriter.SafeSheetName(sheet.Name, patchSheetId++);
            if (!patchSheetNames.Add(requestedName))
                throw new InvalidDataException("Workbook contains duplicate sheet name after Excel normalization: " + requestedName);

            string name = _sheetLookup.TryGetValue(requestedName, out string? existingName)
                ? existingName
                : requestedName;
            _sheetLookup[name] = name;
            AddSheetContent(name, sheet, replaceCells: true);
        }
    }

    private void AddSheetContent(string sheetName, WorkbookSheet sheet, bool replaceCells)
    {
        uint rowIndex = 1;
        foreach (WorkbookRow row in sheet.Rows ?? new List<WorkbookRow>())
        {
            int column = 0;
            foreach (string value in row.Cells ?? new List<string>())
            {
                string reference = OfficeDocumentWriter.ColumnName(column++) + rowIndex;
                string key = Key(sheetName, reference);
                if (replaceCells)
                {
                    _literalNumbers.Remove(key);
                    _formulas.Remove(key);
                    _unsupportedFormulas.Remove(key);
                }
                if (TryNumber(value, out double number))
                    _literalNumbers[key] = number;
            }
            rowIndex++;
        }

        foreach (WorkbookCell cell in sheet.StructuredCells ?? new List<WorkbookCell>())
        {
            string reference = NormalizeCellReference(cell.Reference);
            string key = Key(sheetName, reference);
            _literalNumbers.Remove(key);
            _formulas.Remove(key);
            _unsupportedFormulas.Remove(key);
            if (cell.Formula != null)
            {
                if (replaceCells)
                {
                    if (!string.IsNullOrWhiteSpace(cell.Formula))
                        _formulas[key] = NormalizeFormula(cell.Formula);
                }
                else
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(cell.Formula))
                            throw new InvalidDataException("Existing shared formula follower cannot be evaluated independently.");
                        _formulas[key] = NormalizeFormula(cell.Formula);
                    }
                    catch (InvalidDataException ex)
                    {
                        _unsupportedFormulas[key] = ex.Message;
                    }
                }
            }
            else if (TryTypedNumber(cell, out double number))
                _literalNumbers[key] = number;
        }
    }

    internal IReadOnlyList<WorkbookFormulaResult> EvaluateAll()
    {
        var results = new List<WorkbookFormulaResult>(_formulas.Count + _unsupportedFormulas.Count);
        foreach (string key in _formulas.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            SplitKey(key, out string sheet, out string reference);
            try
            {
                double value = EvaluateCell(sheet, reference, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                results.Add(new WorkbookFormulaResult(sheet, reference, true, value, null));
            }
            catch (Exception ex) when (ex is InvalidDataException or DivideByZeroException or OverflowException)
            {
                _errors[key] = ex.Message;
                results.Add(new WorkbookFormulaResult(sheet, reference, false, null, ex.Message));
            }
        }
        foreach ((string key, string error) in _unsupportedFormulas.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            SplitKey(key, out string sheet, out string reference);
            results.Add(new WorkbookFormulaResult(sheet, reference, false, null, error));
        }
        return results;
    }

    internal bool TryGetCachedValue(string sheetName, string cellReference, out double value) =>
        _evaluated.TryGetValue(Key(ResolveSheet(sheetName), NormalizeCellReference(cellReference)), out value);

    private double EvaluateCell(string sheetName, string reference, HashSet<string> stack)
    {
        string sheet = ResolveSheet(sheetName);
        string normalized = NormalizeCellReference(reference);
        string key = Key(sheet, normalized);
        if (_evaluated.TryGetValue(key, out double cached)) return cached;
        if (_literalNumbers.TryGetValue(key, out double literal)) return literal;
        if (_unsupportedFormulas.TryGetValue(key, out string? unsupported)) throw new InvalidDataException(unsupported);
        if (_errors.TryGetValue(key, out string? previousError)) throw new InvalidDataException(previousError);
        if (!_formulas.TryGetValue(key, out string? formula))
            throw new InvalidDataException("Referenced cell " + sheet + "!" + normalized + " is empty or non-numeric.");
        if (!stack.Add(key))
            throw new InvalidDataException("Circular formula reference detected at " + sheet + "!" + normalized + ".");

        try
        {
            var parser = new FormulaParser(
                formula,
                sheet,
                (targetSheet, targetReference) => EvaluateCell(targetSheet, targetReference, stack),
                (targetSheet, from, to) => ExpandRange(targetSheet, from, to)
                    .Select(cell => EvaluateCell(cell.Sheet, cell.Reference, stack)).ToList());
            double result = parser.Parse();
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException("Formula produced a non-finite result.");
            _evaluated[key] = result;
            return result;
        }
        finally { stack.Remove(key); }
    }

    private IEnumerable<(string Sheet, string Reference)> ExpandRange(string sheetName, string from, string to)
    {
        string sheet = ResolveSheet(sheetName);
        (int fromColumn, int fromRow) = ParseCellCoordinates(from);
        (int toColumn, int toRow) = ParseCellCoordinates(to);
        int minColumn = Math.Min(fromColumn, toColumn);
        int maxColumn = Math.Max(fromColumn, toColumn);
        int minRow = Math.Min(fromRow, toRow);
        int maxRow = Math.Max(fromRow, toRow);
        long cellCount = (long)(maxColumn - minColumn + 1) * (maxRow - minRow + 1);
        if (cellCount > 100_000)
            throw new InvalidDataException("Formula range is too large for PaRuX local evaluation (max 100,000 cells).");

        for (int row = minRow; row <= maxRow; row++)
            for (int column = minColumn; column <= maxColumn; column++)
                yield return (sheet, OfficeDocumentWriter.ColumnName(column) + row.ToString(CultureInfo.InvariantCulture));
    }

    private string ResolveSheet(string sheetName)
    {
        string normalized = sheetName ?? string.Empty;
        if (_sheetLookup.TryGetValue(normalized, out string? actual)) return actual;
        throw new InvalidDataException("Formula references unknown sheet '" + normalized + "'.");
    }

    internal static string NormalizeFormula(string formula)
    {
        string value = (formula ?? string.Empty).Trim();
        if (value.StartsWith("=", StringComparison.Ordinal)) value = value[1..].Trim();
        if (value.Length == 0 || value.Length > 16_000)
            throw new InvalidDataException("Workbook formula must contain 1..16,000 characters.");
        if (value.IndexOfAny(new[] { '[', ']' }) >= 0)
            throw new InvalidDataException("External workbook links are not allowed in native workbook formulas.");
        return value;
    }

    internal static string NormalizeCellReference(string reference)
    {
        string value = (reference ?? string.Empty).Trim().Replace("$", string.Empty, StringComparison.Ordinal);
        Match match = Regex.Match(value, @"^([A-Za-z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        if (!match.Success) throw new InvalidDataException("Invalid A1 cell reference: " + value);
        int column = ColumnIndex(match.Groups[1].Value);
        int row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (column > 16_383 || row > 1_048_576)
            throw new InvalidDataException("Cell reference exceeds Excel worksheet limits: " + value);
        return match.Groups[1].Value.ToUpperInvariant() + row.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool TryParseRangeToken(
        string text,
        string currentSheet,
        out string sheet,
        out string from,
        out string? to,
        out int consumed)
    {
        sheet = currentSheet;
        from = string.Empty;
        to = null;
        consumed = 0;
        Match match = ReferenceAtStart.Match(text ?? string.Empty);
        if (!match.Success) return false;
        string? quoted = match.Groups[1].Success ? match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal) : null;
        string? unquoted = match.Groups[2].Success ? match.Groups[2].Value : null;
        if (!string.IsNullOrWhiteSpace(quoted)) sheet = quoted!;
        else if (!string.IsNullOrWhiteSpace(unquoted)) sheet = unquoted!;
        from = NormalizeCellReference(match.Groups[3].Value + match.Groups[4].Value);
        if (match.Groups[5].Success)
            to = NormalizeCellReference(match.Groups[5].Value + match.Groups[6].Value);
        consumed = match.Length;
        return true;
    }

    private static bool TryTypedNumber(WorkbookCell cell, out double number)
    {
        number = 0;
        string kind = (cell.DataType ?? "auto").Trim().ToLowerInvariant();
        if (kind is "string" or "boolean" or "date") return false;
        return TryNumber(cell.Value, out number);
    }

    private static bool TryNumber(string? value, out double number) => double.TryParse(
        value ?? string.Empty,
        NumberStyles.Float | NumberStyles.AllowThousands,
        CultureInfo.InvariantCulture,
        out number);

    private static string Key(string sheet, string reference) => sheet + "\u0000" + reference;

    private static void SplitKey(string key, out string sheet, out string reference)
    {
        int split = key.IndexOf('\0');
        sheet = key[..split];
        reference = key[(split + 1)..];
    }

    private static (int Column, int Row) ParseCellCoordinates(string reference)
    {
        string normalized = NormalizeCellReference(reference);
        int index = 0;
        while (index < normalized.Length && char.IsLetter(normalized[index])) index++;
        return (ColumnIndex(normalized[..index]), int.Parse(normalized[index..], CultureInfo.InvariantCulture));
    }

    private static int ColumnIndex(string letters)
    {
        int value = 0;
        foreach (char raw in letters.ToUpperInvariant())
        {
            if (raw is < 'A' or > 'Z') throw new InvalidDataException("Invalid Excel column: " + letters);
            value = checked(value * 26 + (raw - 'A' + 1));
        }
        return value - 1;
    }

    private sealed class FormulaParser
    {
        private readonly string _text;
        private readonly string _currentSheet;
        private readonly Func<string, string, double> _cell;
        private readonly Func<string, string, string, IReadOnlyList<double>> _range;
        private int _position;

        internal FormulaParser(
            string text,
            string currentSheet,
            Func<string, string, double> cell,
            Func<string, string, string, IReadOnlyList<double>> range)
        {
            _text = text;
            _currentSheet = currentSheet;
            _cell = cell;
            _range = range;
        }

        internal double Parse()
        {
            double value = ParseExpression();
            SkipWhitespace();
            if (_position != _text.Length)
                throw new InvalidDataException("Unsupported formula syntax near '" + _text[_position..Math.Min(_text.Length, _position + 40)] + "'.");
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
            double value = ParsePower();
            while (true)
            {
                SkipWhitespace();
                if (Consume('*')) value *= ParsePower();
                else if (Consume('/'))
                {
                    double divisor = ParsePower();
                    if (divisor == 0) throw new DivideByZeroException("Formula division by zero.");
                    value /= divisor;
                }
                else return value;
            }
        }

        private double ParsePower()
        {
            double value = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (Consume('^'))
                {
                    double exponent = ParseFactor();
                    value = Math.Pow(value, exponent);
                    if (double.IsNaN(value) || double.IsInfinity(value))
                        throw new InvalidDataException("Formula produced a non-finite result.");
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
                double nested = ParseExpression();
                SkipWhitespace();
                Expect(')');
                return nested;
            }

            int start = _position;
            string identifier = ReadIdentifier();
            if (identifier.Length > 0)
            {
                SkipWhitespace();
                if (Peek() == '(')
                    return ParseFunction(identifier);
                _position = start;
            }

            if (TryReadReference(out string sheet, out string from, out string? to))
            {
                if (to != null)
                    throw new InvalidDataException("A range is valid only as an argument to a multi-cell function (e.g. SUM, AVERAGE, MIN, MAX, COUNT, PRODUCT).");
                return _cell(sheet, from);
            }

            _position = start;
            return ParseNumber();
        }

        private double ParseFunction(string name)
        {
            string function = name.ToUpperInvariant();
            bool isRangeFunction = function is "SUM" or "AVERAGE" or "MIN" or "MAX" or "COUNT" or "PRODUCT";
            bool isSupported = isRangeFunction || function is "ABS" or "SQRT" or "INT" or "SIGN" or "POWER" or "MOD" or "ROUND" or "ROUNDUP" or "ROUNDDOWN" or "TRUNC" or "LN" or "EXP" or "PI";
            if (!isSupported)
                throw new InvalidDataException("Unsupported local formula function: " + name + ".");

            Expect('(');
            var values = new List<double>();
            while (true)
            {
                SkipWhitespace();
                if (Peek() == ')') break;

                int save = _position;
                if (TryReadReference(out string sheet, out string from, out string? to) && to != null)
                {
                    if (!isRangeFunction)
                        throw new InvalidDataException(function + " does not accept cell ranges as arguments.");
                    values.AddRange(_range(sheet, from, to));
                }
                else
                {
                    _position = save;
                    values.Add(ParseExpression());
                }
                SkipWhitespace();
                if (!Consume(',')) break;
            }
            Expect(')');

            if (function == "PI")
            {
                if (values.Count != 0) throw new InvalidDataException("PI accepts no arguments.");
                return Math.PI;
            }

            if (values.Count == 0)
                throw new InvalidDataException(function + " requires at least one numeric argument.");

            return function switch
            {
                "SUM" => values.Sum(),
                "AVERAGE" => values.Average(),
                "MIN" => values.Min(),
                "MAX" => values.Max(),
                "COUNT" => (double)values.Count,
                "PRODUCT" => values.Aggregate(1.0, (acc, item) => acc * item),
                "ABS" => ExpectUnary(function, values, Math.Abs),
                "SQRT" => ExpectUnary(function, values, v => v < 0 ? throw new InvalidDataException("SQRT requires a non-negative number.") : Math.Sqrt(v)),
                "INT" => ExpectUnary(function, values, Math.Floor),
                "SIGN" => ExpectUnary(function, values, v => (double)Math.Sign(v)),
                "LN" => ExpectUnary(function, values, v => v <= 0 ? throw new InvalidDataException("LN requires a positive number.") : Math.Log(v)),
                "EXP" => ExpectUnary(function, values, Math.Exp),
                "POWER" => ExpectBinary(function, values, (b, e) =>
                {
                    double pow = Math.Pow(b, e);
                    if (double.IsNaN(pow) || double.IsInfinity(pow))
                        throw new InvalidDataException("POWER produced a non-finite result.");
                    return pow;
                }),
                "MOD" => ExpectBinary(function, values, (num, div) =>
                {
                    if (div == 0) throw new DivideByZeroException("Formula division by zero in MOD.");
                    return num - div * Math.Floor(num / div);
                }),
                "ROUND" => EvaluateRound(function, values, (num, digits) =>
                {
                    if (digits >= 0)
                        return Math.Round(num, Math.Clamp(digits, 0, 15), MidpointRounding.AwayFromZero);
                    double factor = Math.Pow(10, -digits);
                    return Math.Round(num / factor, MidpointRounding.AwayFromZero) * factor;
                }),
                "ROUNDUP" => EvaluateRound(function, values, (num, digits) =>
                {
                    double factor = Math.Pow(10, digits);
                    return num >= 0
                        ? Math.Ceiling(num * factor) / factor
                        : Math.Floor(num * factor) / factor;
                }),
                "ROUNDDOWN" or "TRUNC" => EvaluateRound(function, values, (num, digits) =>
                {
                    double factor = Math.Pow(10, digits);
                    return Math.Truncate(num * factor) / factor;
                }),
                _ => throw new InvalidDataException("Unsupported formula function.")
            };
        }

        private static double ExpectUnary(string function, List<double> values, Func<double, double> op)
        {
            if (values.Count != 1)
                throw new InvalidDataException(function + " requires exactly one argument.");
            double result = op(values[0]);
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException(function + " produced a non-finite result.");
            return result;
        }

        private static double ExpectBinary(string function, List<double> values, Func<double, double, double> op)
        {
            if (values.Count != 2)
                throw new InvalidDataException(function + " requires exactly two arguments.");
            double result = op(values[0], values[1]);
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException(function + " produced a non-finite result.");
            return result;
        }

        private static double EvaluateRound(string function, List<double> values, Func<double, int, double> op)
        {
            if (values.Count is < 1 or > 2)
                throw new InvalidDataException(function + " requires 1 or 2 arguments.");
            int digits = values.Count == 2
                ? (int)Math.Round(values[1], MidpointRounding.AwayFromZero)
                : 0;
            double result = op(values[0], digits);
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException(function + " produced a non-finite result.");
            return result;
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            int start = _position;
            bool dot = false;
            while (_position < _text.Length)
            {
                char c = _text[_position];
                if (char.IsDigit(c)) { _position++; continue; }
                if (c == '.' && !dot) { dot = true; _position++; continue; }
                if ((c == 'e' || c == 'E') && _position > start)
                {
                    _position++;
                    if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-')) _position++;
                    continue;
                }
                break;
            }
            if (start == _position || !double.TryParse(_text[start.._position], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value))
                throw new InvalidDataException("Expected a numeric literal, cell reference or supported function.");
            return value;
        }

        private bool TryReadReference(out string sheet, out string from, out string? to)
        {
            SkipWhitespace();
            if (!TryParseRangeToken(_text[_position..], _currentSheet, out sheet, out from, out to, out int consumed))
                return false;
            _position += consumed;
            return true;
        }

        private string ReadIdentifier()
        {
            SkipWhitespace();
            int start = _position;
            if (_position >= _text.Length || !(char.IsLetter(_text[_position]) || _text[_position] == '_')) return string.Empty;
            _position++;
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_')) _position++;
            return _text[start.._position];
        }

        private char Peek()
        {
            SkipWhitespace();
            return _position < _text.Length ? _text[_position] : '\0';
        }

        private bool Consume(char expected)
        {
            SkipWhitespace();
            if (_position >= _text.Length || _text[_position] != expected) return false;
            _position++;
            return true;
        }

        private void Expect(char expected)
        {
            if (!Consume(expected)) throw new InvalidDataException("Expected '" + expected + "' in formula.");
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }
    }
}
