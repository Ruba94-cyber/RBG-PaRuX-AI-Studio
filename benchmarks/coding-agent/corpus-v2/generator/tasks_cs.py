from common import task, cs_verify, CS_FIXTURE_PROJ

S = ['src/']
TASKS = []
A = TASKS.append

P = {'src/Lib.csproj': CS_FIXTURE_PROJ}
NOTE = ' All types live in the global namespace in src/. Do not add a Main method or change src/Lib.csproj.'

# ---- 1 percentile (bugfix, optimization)
A(task('cs-bug-percentile', 'csharp', 'bugfix', 'optimization', 'medium',
 'Stats.Percentile(IReadOnlyList<double> values, double p) in src/Stats.cs must return the p-th percentile (0..100) using linear interpolation between closest ranks on the SORTED values (rank = p/100 * (n-1)). It must not reorder the caller\'s list, must throw ArgumentException for an empty list and ArgumentOutOfRangeException when p is outside 0..100. Fix it.' + NOTE,
 dict(P, **{'src/Stats.cs': '''using System.Collections.Generic;

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        int idx = (int)(p / 100.0 * values.Count);
        return values[idx];
    }
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("median odd", () => Stats.Percentile(new List<double> { 5, 1, 3 }, 50) == 3);
        T.Run("median even interpolates", () => Math.Abs(Stats.Percentile(new List<double> { 4, 1, 3, 2 }, 50) - 2.5) < 1e-9);
        T.Run("p0", () => Stats.Percentile(new List<double> { 9, 2, 7 }, 0) == 2);
        T.Run("p100", () => Stats.Percentile(new List<double> { 9, 2, 7 }, 100) == 9);
        T.Run("p25", () => Math.Abs(Stats.Percentile(new List<double> { 10, 20, 30, 40, 50 }, 25) - 20) < 1e-9);
        T.Run("p90 interpolated", () => Math.Abs(Stats.Percentile(new List<double> { 1, 2, 3, 4 }, 90) - 3.7) < 1e-9);
        T.Run("single", () => Stats.Percentile(new List<double> { 7 }, 80) == 7);
        T.Run("no reorder", () => { var l = new List<double> { 3, 1, 2 }; Stats.Percentile(l, 50); return l[0] == 3 && l[1] == 1 && l[2] == 2; });
        T.Run("empty throws", () => { try { Stats.Percentile(new List<double>(), 50); return false; } catch (ArgumentException e) when (e is not ArgumentOutOfRangeException) { return true; } });
        T.Run("range throws", () => { try { Stats.Percentile(new List<double> { 1 }, 101); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        T.Run("negative throws", () => { try { Stats.Percentile(new List<double> { 1 }, -1); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        return T.Done();
    }
}
'''),
 {'src/Stats.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) throw new ArgumentException("values is empty", nameof(values));
        if (p < 0 || p > 100) throw new ArgumentOutOfRangeException(nameof(p));
        var sorted = values.OrderBy(v => v).ToArray();
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
'''}, S, 600, 35))

# ---- 2 word wrap (bugfix, held-out)
A(task('cs-bug-wordwrap', 'csharp', 'bugfix', 'held-out', 'medium',
 'TextTools.Wrap(string text, int width) in src/TextTools.cs must greedily wrap on single spaces so no line exceeds `width` characters (a single word longer than width stays alone on its line, unbroken). Words are separated by any amount of whitespace, lines are joined with "\\n", and there is no trailing newline or trailing space. width < 1 throws ArgumentOutOfRangeException. Empty/whitespace text returns "". Fix the current implementation.' + NOTE,
 dict(P, **{'src/TextTools.cs': '''using System;
using System.Text;

public static class TextTools
{
    public static string Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        int col = 0;
        foreach (var word in text.Split(' '))
        {
            if (col + word.Length > width)
            {
                sb.Append('\\n');
                col = 0;
            }
            sb.Append(word).Append(' ');
            col += word.Length + 1;
        }
        return sb.ToString();
    }
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("simple", () => TextTools.Wrap("the quick brown fox", 9) == "the quick\\nbrown fox");
        T.Run("exact fit", () => TextTools.Wrap("abc def", 7) == "abc def");
        T.Run("one over", () => TextTools.Wrap("abc def", 6) == "abc\\ndef");
        T.Run("long word alone", () => TextTools.Wrap("hi extraordinarily ok", 5) == "hi\\nextraordinarily\\nok");
        T.Run("multi space", () => TextTools.Wrap("a   b \\t c", 10) == "a b c");
        T.Run("no trailing", () => !TextTools.Wrap("aa bb cc", 5).EndsWith(" ") && !TextTools.Wrap("aa bb cc", 5).EndsWith("\\n"));
        T.Run("empty", () => TextTools.Wrap("   ", 5) == "");
        T.Run("width guard", () => { try { TextTools.Wrap("a", 0); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        return T.Done();
    }
}
'''),
 {'src/TextTools.cs': '''using System;
using System.Text;

public static class TextTools
{
    public static string Wrap(string text, int width)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        int col = 0;
        foreach (var word in words)
        {
            if (col == 0)
            {
                sb.Append(word);
                col = word.Length;
            }
            else if (col + 1 + word.Length <= width)
            {
                sb.Append(' ').Append(word);
                col += 1 + word.Length;
            }
            else
            {
                sb.Append('\\n').Append(word);
                col = word.Length;
            }
        }
        return sb.ToString();
    }
}
'''}, S, 600, 35))

# ---- 3 overlap (bugfix, held-out, mandatory)
A(task('cs-bug-interval-overlap', 'csharp', 'bugfix', 'held-out', 'medium',
 'Booking.Overlaps(a, b) and Booking.FindConflicts(existing, candidate) in src/Booking.cs decide whether half-open time ranges [Start, End) collide. Two ranges overlap only when each starts strictly before the other ends, so back-to-back bookings (a.End == b.Start) do NOT conflict, and a booking that fully contains another does conflict. FindConflicts returns the existing bookings that overlap the candidate, in original order. A range with End <= Start is invalid and must throw ArgumentException in Overlaps. Fix the current logic.' + NOTE,
 dict(P, **{'src/Booking.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public record Slot(DateTime Start, DateTime End);

public static class Booking
{
    public static bool Overlaps(Slot a, Slot b)
    {
        return a.Start <= b.End && b.Start <= a.End && a.Start != b.Start;
    }

    public static List<Slot> FindConflicts(IEnumerable<Slot> existing, Slot candidate)
    {
        return existing.Where(e => Overlaps(e, candidate)).ToList();
    }
}
'''}),
 cs_verify('''
public static class Program
{
    static Slot R(int s, int e) => new Slot(new DateTime(2024, 1, 1, 0, 0, 0).AddHours(s), new DateTime(2024, 1, 1, 0, 0, 0).AddHours(e));
    public static int Main()
    {
        T.Run("partial overlap", () => Booking.Overlaps(R(1, 5), R(3, 8)));
        T.Run("contained", () => Booking.Overlaps(R(1, 10), R(3, 4)) && Booking.Overlaps(R(3, 4), R(1, 10)));
        T.Run("same start", () => Booking.Overlaps(R(2, 4), R(2, 6)));
        T.Run("identical", () => Booking.Overlaps(R(2, 4), R(2, 4)));
        T.Run("back to back is fine", () => !Booking.Overlaps(R(1, 3), R(3, 5)) && !Booking.Overlaps(R(3, 5), R(1, 3)));
        T.Run("disjoint", () => !Booking.Overlaps(R(1, 2), R(5, 6)));
        T.Run("invalid", () => { try { Booking.Overlaps(R(3, 3), R(1, 2)); return false; } catch (ArgumentException) { return true; } });
        T.Run("conflicts list", () =>
        {
            var ex = new List<Slot> { R(0, 2), R(2, 4), R(3, 6), R(8, 9) };
            var c = Booking.FindConflicts(ex, R(3, 5));
            return c.Count == 2 && c[0] == ex[1] && c[1] == ex[2];
        });
        T.Run("no conflicts", () => Booking.FindConflicts(new List<Slot> { R(0, 1) }, R(1, 2)).Count == 0);
        return T.Done();
    }
}
'''),
 {'src/Booking.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public record Slot(DateTime Start, DateTime End);

public static class Booking
{
    public static bool Overlaps(Slot a, Slot b)
    {
        if (a.End <= a.Start) throw new ArgumentException("invalid range", nameof(a));
        if (b.End <= b.Start) throw new ArgumentException("invalid range", nameof(b));
        return a.Start < b.End && b.Start < a.End;
    }

    public static List<Slot> FindConflicts(IEnumerable<Slot> existing, Slot candidate)
    {
        return existing.Where(e => Overlaps(e, candidate)).ToList();
    }
}
'''}, S, 600, 35, mandatory=True))

# ---- 4 rate limiter (feature, optimization)
A(task('cs-feat-token-bucket', 'csharp', 'feature', 'optimization', 'medium',
 'Implement TokenBucket in src/TokenBucket.cs. Constructor TokenBucket(int capacity, double refillPerSecond, Func<DateTime> clock). It starts full. bool TryAcquire(int tokens = 1) refills based on elapsed clock time (fractional tokens accumulate, never exceeding capacity), then consumes `tokens` if enough are available and returns true, else consumes nothing and returns false. tokens < 1 or > capacity throws ArgumentOutOfRangeException. Property double Available reports the current (refilled) token count.' + NOTE,
 dict(P, **{'src/Placeholder.cs': '''// TokenBucket is implemented in TokenBucket.cs (to be added).
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        var now = new DateTime(2024, 1, 1);
        var b = new TokenBucket(5, 2.0, () => now);
        T.Run("starts full", () => b.Available == 5);
        T.Run("consume", () => b.TryAcquire(3) && Math.Abs(b.Available - 2) < 1e-9);
        T.Run("insufficient consumes nothing", () => !b.TryAcquire(3) && Math.Abs(b.Available - 2) < 1e-9);
        T.Run("refill fractional", () => { now = now.AddMilliseconds(250); return Math.Abs(b.Available - 2.5) < 1e-9; });
        T.Run("refill enough", () => { now = now.AddSeconds(1); return b.TryAcquire(4) && Math.Abs(b.Available - 0.5) < 1e-9; });
        T.Run("capped at capacity", () => { now = now.AddHours(1); return Math.Abs(b.Available - 5) < 1e-9; });
        T.Run("default one token", () => { var c = new TokenBucket(1, 1, () => now); return c.TryAcquire() && !c.TryAcquire(); });
        T.Run("bad tokens", () =>
        {
            try { b.TryAcquire(0); return false; } catch (ArgumentOutOfRangeException) { }
            try { b.TryAcquire(6); return false; } catch (ArgumentOutOfRangeException) { }
            return true;
        });
        T.Run("clock going backwards does not add tokens", () =>
        {
            var t0 = new DateTime(2024, 1, 1); var c = new TokenBucket(3, 1, () => t0);
            c.TryAcquire(3); t0 = t0.AddSeconds(-10);
            return c.Available == 0;
        });
        return T.Done();
    }
}
'''),
 {'src/TokenBucket.cs': '''using System;

public class TokenBucket
{
    private readonly int _capacity;
    private readonly double _rate;
    private readonly Func<DateTime> _clock;
    private double _tokens;
    private DateTime _last;

    public TokenBucket(int capacity, double refillPerSecond, Func<DateTime> clock)
    {
        _capacity = capacity;
        _rate = refillPerSecond;
        _clock = clock;
        _tokens = capacity;
        _last = clock();
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = (now - _last).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsed * _rate);
            _last = now;
        }
        else if (elapsed < 0)
        {
            _last = now;
        }
    }

    public double Available
    {
        get { Refill(); return _tokens; }
    }

    public bool TryAcquire(int tokens = 1)
    {
        if (tokens < 1 || tokens > _capacity) throw new ArgumentOutOfRangeException(nameof(tokens));
        Refill();
        if (_tokens + 1e-12 < tokens) return false;
        _tokens -= tokens;
        return true;
    }
}
'''}, S, 600, 35))

# ---- 5 csv writer (feature, held-out)
A(task('cs-feat-csv-escape', 'csharp', 'feature', 'held-out', 'easy',
 'CsvWriter.Row(IEnumerable<string> fields) in src/CsvWriter.cs currently joins fields with commas. Make it RFC 4180 compliant: a field that contains a comma, a double quote, CR or LF is wrapped in double quotes with embedded quotes doubled; null becomes an empty field; other fields are untouched. Add CsvWriter.Table(IEnumerable<IEnumerable<string>> rows) that joins rows with "\\r\\n" (no trailing line break).' + NOTE,
 dict(P, **{'src/CsvWriter.cs': '''using System.Collections.Generic;

public static class CsvWriter
{
    public static string Row(IEnumerable<string> fields)
    {
        return string.Join(",", fields);
    }
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("plain", () => CsvWriter.Row(new[] { "a", "b" }) == "a,b");
        T.Run("comma", () => CsvWriter.Row(new[] { "a,b", "c" }) == "\\"a,b\\",c");
        T.Run("quote", () => CsvWriter.Row(new[] { "say \\"hi\\"" }) == "\\"say \\"\\"hi\\"\\"\\"");
        T.Run("newline", () => CsvWriter.Row(new[] { "l1\\nl2", "x" }) == "\\"l1\\nl2\\",x");
        T.Run("cr", () => CsvWriter.Row(new[] { "a\\rb" }) == "\\"a\\rb\\"");
        T.Run("null empty", () => CsvWriter.Row(new string[] { "a", null, "c" }) == "a,,c");
        T.Run("empty row", () => CsvWriter.Row(new string[0]) == "");
        T.Run("table", () => CsvWriter.Table(new[] { new[] { "h1", "h,2" }, new[] { "1", "2" } }) == "h1,\\"h,2\\"\\r\\n1,2");
        T.Run("table empty", () => CsvWriter.Table(new string[0][]) == "");
        T.Run("leading space untouched", () => CsvWriter.Row(new[] { " a " }) == " a ");
        return T.Done();
    }
}
'''),
 {'src/CsvWriter.cs': '''using System.Collections.Generic;
using System.Linq;

public static class CsvWriter
{
    private static string Escape(string field)
    {
        if (field == null) return "";
        if (field.IndexOfAny(new[] { ',', '"', '\\r', '\\n' }) < 0) return field;
        return "\\"" + field.Replace("\\"", "\\"\\"") + "\\"";
    }

    public static string Row(IEnumerable<string> fields)
    {
        return string.Join(",", fields.Select(Escape));
    }

    public static string Table(IEnumerable<IEnumerable<string>> rows)
    {
        return string.Join("\\r\\n", rows.Select(Row));
    }
}
'''}, S, 480, 30))

# ---- 6 inventory reserve (feature, held-out)
A(task('cs-feat-inventory-reserve', 'csharp', 'feature', 'held-out', 'medium',
 'Inventory in src/Inventory.cs tracks stock per SKU. Add Reserve(string sku, int qty) returning a reservation id (int, starting at 1 and increasing), Release(int reservationId) which returns the quantity to available stock (releasing an unknown or already released id returns false, otherwise true), and Commit(int reservationId) which permanently removes reserved stock (returns false for unknown ids). Available(sku) = stock minus active reservations. Reserve must throw InvalidOperationException when qty exceeds Available, and ArgumentOutOfRangeException when qty < 1. Unknown SKUs have 0 stock.' + NOTE,
 dict(P, **{'src/Inventory.cs': '''using System.Collections.Generic;

public class Inventory
{
    private readonly Dictionary<string, int> _stock = new Dictionary<string, int>();

    public void Add(string sku, int qty)
    {
        _stock[sku] = (_stock.TryGetValue(sku, out var cur) ? cur : 0) + qty;
    }

    public int Stock(string sku) => _stock.TryGetValue(sku, out var cur) ? cur : 0;
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("available = stock", () => { var i = new Inventory(); i.Add("a", 10); return i.Available("a") == 10 && i.Available("zzz") == 0; });
        T.Run("reserve reduces available", () => { var i = new Inventory(); i.Add("a", 10); int r = i.Reserve("a", 4); return r == 1 && i.Available("a") == 6 && i.Stock("a") == 10; });
        T.Run("ids increase", () => { var i = new Inventory(); i.Add("a", 10); return i.Reserve("a", 1) == 1 && i.Reserve("a", 1) == 2; });
        T.Run("over-reserve throws", () => { var i = new Inventory(); i.Add("a", 3); i.Reserve("a", 2); try { i.Reserve("a", 2); return false; } catch (InvalidOperationException) { return true; } });
        T.Run("failed reserve changes nothing", () => { var i = new Inventory(); i.Add("a", 3); try { i.Reserve("a", 9); } catch (InvalidOperationException) { } return i.Available("a") == 3 && i.Reserve("a", 1) == 1; });
        T.Run("qty guard", () => { var i = new Inventory(); i.Add("a", 3); try { i.Reserve("a", 0); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        T.Run("release", () => { var i = new Inventory(); i.Add("a", 5); int r = i.Reserve("a", 5); return i.Release(r) && i.Available("a") == 5 && !i.Release(r) && !i.Release(99); });
        T.Run("commit", () => { var i = new Inventory(); i.Add("a", 5); int r = i.Reserve("a", 2); return i.Commit(r) && i.Stock("a") == 3 && i.Available("a") == 3 && !i.Release(r) && !i.Commit(r); });
        T.Run("commit unknown", () => new Inventory().Commit(7) == false);
        T.Run("unknown sku reserve", () => { var i = new Inventory(); try { i.Reserve("nope", 1); return false; } catch (InvalidOperationException) { return true; } });
        return T.Done();
    }
}
'''),
 {'src/Inventory.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public class Inventory
{
    private readonly Dictionary<string, int> _stock = new Dictionary<string, int>();
    private readonly Dictionary<int, (string Sku, int Qty)> _reservations = new Dictionary<int, (string, int)>();
    private int _nextId = 1;

    public void Add(string sku, int qty)
    {
        _stock[sku] = (_stock.TryGetValue(sku, out var cur) ? cur : 0) + qty;
    }

    public int Stock(string sku) => _stock.TryGetValue(sku, out var cur) ? cur : 0;

    public int Available(string sku) =>
        Stock(sku) - _reservations.Values.Where(r => r.Sku == sku).Sum(r => r.Qty);

    public int Reserve(string sku, int qty)
    {
        if (qty < 1) throw new ArgumentOutOfRangeException(nameof(qty));
        if (qty > Available(sku)) throw new InvalidOperationException("insufficient stock");
        int id = _nextId++;
        _reservations[id] = (sku, qty);
        return id;
    }

    public bool Release(int reservationId) => _reservations.Remove(reservationId);

    public bool Commit(int reservationId)
    {
        if (!_reservations.TryGetValue(reservationId, out var r)) return false;
        _reservations.Remove(reservationId);
        _stock[r.Sku] = Stock(r.Sku) - r.Qty;
        return true;
    }
}
'''}, S, 600, 35))

# ---- 7 strategy table (refactor, optimization)
A(task('cs-refactor-discount-table', 'csharp', 'refactor', 'optimization', 'easy',
 'DiscountPolicy.Percent(string customerTier) in src/DiscountPolicy.cs uses a chain of if/else-if statements. Refactor it to a static readonly Dictionary<string,int> lookup (case-insensitive keys) with no `else if` left in the file. Behaviour must be identical: unknown or null tiers give 0.' + NOTE,
 dict(P, **{'src/DiscountPolicy.cs': '''public static class DiscountPolicy
{
    public static int Percent(string customerTier)
    {
        if (customerTier == null) return 0;
        string t = customerTier.ToLowerInvariant();
        if (t == "bronze") return 2;
        else if (t == "silver") return 5;
        else if (t == "gold") return 10;
        else if (t == "platinum") return 15;
        else if (t == "staff") return 30;
        else return 0;
    }
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("tiers", () => DiscountPolicy.Percent("bronze") == 2 && DiscountPolicy.Percent("silver") == 5 && DiscountPolicy.Percent("gold") == 10 && DiscountPolicy.Percent("platinum") == 15 && DiscountPolicy.Percent("staff") == 30);
        T.Run("case-insensitive", () => DiscountPolicy.Percent("GoLD") == 10 && DiscountPolicy.Percent("STAFF") == 30);
        T.Run("unknown", () => DiscountPolicy.Percent("diamond") == 0 && DiscountPolicy.Percent("") == 0);
        T.Run("null", () => DiscountPolicy.Percent(null) == 0);
        T.Run("no else-if", () =>
        {
            var dir = AppContext.BaseDirectory;
            var cur = new DirectoryInfo(dir);
            while (cur != null && !File.Exists(Path.Combine(cur.FullName, "src", "DiscountPolicy.cs"))) cur = cur.Parent;
            if (cur == null) return false;
            var text = File.ReadAllText(Path.Combine(cur.FullName, "src", "DiscountPolicy.cs"));
            return !System.Text.RegularExpressions.Regex.IsMatch(text, "else\\\\s+if") && text.Contains("Dictionary<string");
        });
        return T.Done();
    }
}
'''),
 {'src/DiscountPolicy.cs': '''using System;
using System.Collections.Generic;

public static class DiscountPolicy
{
    private static readonly Dictionary<string, int> Table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["bronze"] = 2,
        ["silver"] = 5,
        ["gold"] = 10,
        ["platinum"] = 15,
        ["staff"] = 30,
    };

    public static int Percent(string customerTier)
    {
        if (customerTier == null) return 0;
        return Table.TryGetValue(customerTier, out var percent) ? percent : 0;
    }
}
'''}, S, 420, 30))

# ---- 8 extract class (refactor, held-out)
A(task('cs-refactor-extract-tax', 'csharp', 'refactor', 'held-out', 'medium',
 'Order in src/Order.cs computes tax inline. Extract the tax computation into a new public class TaxCalculator (src/TaxCalculator.cs) with an instance method `decimal Tax(decimal netAmount, string countryCode)` and have Order.Total() call it. Rates: "DE" 19%, "FR" 20%, "IT" 22%, "US" 0%; unknown countries throw ArgumentException. Round tax to 2 decimals using MidpointRounding.AwayFromZero. Order must keep its constructor and Total() behaviour, and must no longer contain the rate numbers itself.' + NOTE,
 dict(P, **{'src/Order.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public class Order
{
    private readonly List<(decimal Price, int Qty)> _lines = new List<(decimal, int)>();
    private readonly string _country;

    public Order(string countryCode) { _country = countryCode; }

    public void AddLine(decimal price, int qty) { _lines.Add((price, qty)); }

    public decimal Total()
    {
        decimal net = _lines.Sum(l => l.Price * l.Qty);
        decimal rate;
        switch (_country)
        {
            case "DE": rate = 0.19m; break;
            case "FR": rate = 0.20m; break;
            case "IT": rate = 0.22m; break;
            case "US": rate = 0m; break;
            default: throw new ArgumentException("unknown country: " + _country);
        }
        return net + Math.Round(net * rate, 2, MidpointRounding.AwayFromZero);
    }
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("de total", () => { var o = new Order("DE"); o.AddLine(10.00m, 3); return o.Total() == 35.70m; });
        T.Run("rounding half up", () => { var o = new Order("FR"); o.AddLine(0.25m, 1); return o.Total() == 0.30m; });
        T.Run("us no tax", () => { var o = new Order("US"); o.AddLine(9.99m, 2); return o.Total() == 19.98m; });
        T.Run("unknown throws", () => { var o = new Order("XX"); o.AddLine(1m, 1); try { o.Total(); return false; } catch (ArgumentException) { return true; } });
        T.Run("calculator class", () => { var c = new TaxCalculator(); return c.Tax(100m, "IT") == 22.00m && c.Tax(100m, "US") == 0m && c.Tax(0.05m, "DE") == 0.01m; });
        T.Run("calculator unknown", () => { try { new TaxCalculator().Tax(1m, "ZZ"); return false; } catch (ArgumentException) { return true; } });
        T.Run("order has no inline rates", () =>
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory);
            while (cur != null && !File.Exists(Path.Combine(cur.FullName, "src", "Order.cs"))) cur = cur.Parent;
            if (cur == null) return false;
            var text = File.ReadAllText(Path.Combine(cur.FullName, "src", "Order.cs"));
            return !text.Contains("0.19") && !text.Contains("0.22") && text.Contains("TaxCalculator");
        });
        return T.Done();
    }
}
'''),
 {'src/Order.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public class Order
{
    private readonly List<(decimal Price, int Qty)> _lines = new List<(decimal, int)>();
    private readonly string _country;
    private readonly TaxCalculator _tax = new TaxCalculator();

    public Order(string countryCode) { _country = countryCode; }

    public void AddLine(decimal price, int qty) { _lines.Add((price, qty)); }

    public decimal Total()
    {
        decimal net = _lines.Sum(l => l.Price * l.Qty);
        return net + _tax.Tax(net, _country);
    }
}
''',
  'src/TaxCalculator.cs': '''using System;
using System.Collections.Generic;

public class TaxCalculator
{
    private static readonly Dictionary<string, decimal> Rates = new Dictionary<string, decimal>
    {
        ["DE"] = 0.19m,
        ["FR"] = 0.20m,
        ["IT"] = 0.22m,
        ["US"] = 0m,
    };

    public decimal Tax(decimal netAmount, string countryCode)
    {
        if (countryCode == null || !Rates.TryGetValue(countryCode, out var rate))
            throw new ArgumentException("unknown country: " + countryCode);
        return Math.Round(netAmount * rate, 2, MidpointRounding.AwayFromZero);
    }
}
'''}, S, 600, 35))

# ---- 9 mutation during enumeration (bugfix, optimization)
A(task('cs-bug-expire-sessions', 'csharp', 'bugfix', 'optimization', 'easy',
 'SessionStore.ExpireOlderThan(DateTime cutoff) in src/SessionStore.cs must remove every session whose LastSeen is strictly before the cutoff and return how many were removed. It currently throws InvalidOperationException because it removes items from the list while enumerating it. Fix it.' + NOTE,
 dict(P, **{'src/SessionStore.cs': '''using System;
using System.Collections.Generic;

public class SessionStore
{
    private readonly List<(string Id, DateTime LastSeen)> _sessions = new List<(string, DateTime)>();

    public void Touch(string id, DateTime when)
    {
        int i = _sessions.FindIndex(s => s.Id == id);
        if (i >= 0) _sessions[i] = (id, when);
        else _sessions.Add((id, when));
    }

    public bool Contains(string id) => _sessions.Exists(s => s.Id == id);
    public int Count => _sessions.Count;

    public int ExpireOlderThan(DateTime cutoff)
    {
        int removed = 0;
        foreach (var s in _sessions)
        {
            if (s.LastSeen < cutoff)
            {
                _sessions.Remove(s);
                removed++;
            }
        }
        return removed;
    }
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        var d0 = new DateTime(2024, 1, 1);
        T.Run("removes old only", () =>
        {
            var s = new SessionStore();
            s.Touch("a", d0); s.Touch("b", d0.AddHours(2)); s.Touch("c", d0.AddHours(1)); s.Touch("d", d0.AddHours(3));
            int n = s.ExpireOlderThan(d0.AddHours(2));
            return n == 2 && !s.Contains("a") && !s.Contains("c") && s.Contains("b") && s.Contains("d") && s.Count == 2;
        });
        T.Run("strictly before", () => { var s = new SessionStore(); s.Touch("x", d0); return s.ExpireOlderThan(d0) == 0 && s.Contains("x"); });
        T.Run("all expire", () => { var s = new SessionStore(); for (int i = 0; i < 50; i++) s.Touch("s" + i, d0); return s.ExpireOlderThan(d0.AddDays(1)) == 50 && s.Count == 0; });
        T.Run("empty", () => new SessionStore().ExpireOlderThan(d0) == 0);
        return T.Done();
    }
}
'''),
 {'src/SessionStore.cs': '''using System;
using System.Collections.Generic;

public class SessionStore
{
    private readonly List<(string Id, DateTime LastSeen)> _sessions = new List<(string, DateTime)>();

    public void Touch(string id, DateTime when)
    {
        int i = _sessions.FindIndex(s => s.Id == id);
        if (i >= 0) _sessions[i] = (id, when);
        else _sessions.Add((id, when));
    }

    public bool Contains(string id) => _sessions.Exists(s => s.Id == id);
    public int Count => _sessions.Count;

    public int ExpireOlderThan(DateTime cutoff)
    {
        return _sessions.RemoveAll(s => s.LastSeen < cutoff);
    }
}
'''}, S, 420, 25))

# ---- 10 case-insensitive lookup (bugfix, held-out)
A(task('cs-bug-header-lookup', 'csharp', 'bugfix', 'held-out', 'easy',
 'HeaderBag in src/HeaderBag.cs stores HTTP headers. Header names are case-insensitive: Set("Content-Type", ...) then Get("content-type") must find it, Set with a different casing replaces the value, Remove must work in any casing, and Names must still return the name casing used by the most recent Set. Get returns null when absent. Fix the implementation.' + NOTE,
 dict(P, **{'src/HeaderBag.cs': '''using System.Collections.Generic;

public class HeaderBag
{
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

    public void Set(string name, string value) { _values[name] = value; }
    public string Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public bool Remove(string name) => _values.Remove(name);
    public IEnumerable<string> Names => _values.Keys;
}
'''}),
 cs_verify('''
public static class Program
{
    public static int Main()
    {
        T.Run("get any case", () => { var h = new HeaderBag(); h.Set("Content-Type", "a"); return h.Get("content-type") == "a" && h.Get("CONTENT-TYPE") == "a"; });
        T.Run("set replaces", () => { var h = new HeaderBag(); h.Set("X-A", "1"); h.Set("x-a", "2"); return h.Get("X-A") == "2" && h.Names.Count() == 1; });
        T.Run("name casing is latest", () => { var h = new HeaderBag(); h.Set("X-A", "1"); h.Set("x-a", "2"); return h.Names.Single() == "x-a"; });
        T.Run("remove any case", () => { var h = new HeaderBag(); h.Set("Accept", "x"); return h.Remove("ACCEPT") && h.Get("accept") == null && !h.Remove("accept"); });
        T.Run("absent null", () => new HeaderBag().Get("nope") == null);
        T.Run("multiple names", () => { var h = new HeaderBag(); h.Set("A", "1"); h.Set("B", "2"); return h.Names.OrderBy(x => x).SequenceEqual(new[] { "A", "B" }); });
        return T.Done();
    }
}
'''),
 {'src/HeaderBag.cs': '''using System;
using System.Collections.Generic;
using System.Linq;

public class HeaderBag
{
    private readonly Dictionary<string, (string Name, string Value)> _values =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) { _values[name] = (name, value); }
    public string Get(string name) => _values.TryGetValue(name, out var v) ? v.Value : null;
    public bool Remove(string name) => _values.Remove(name);
    public IEnumerable<string> Names => _values.Values.Select(v => v.Name);
}
'''}, S, 420, 25))
