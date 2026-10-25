using System.Collections.Generic;
using System.Linq;

public static class CsvWriter
{
    private static string Escape(string field)
    {
        if (field == null) return "";
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    public static string Row(IEnumerable<string> fields)
    {
        return string.Join(",", fields.Select(Escape));
    }

    public static string Table(IEnumerable<IEnumerable<string>> rows)
    {
        return string.Join("\r\n", rows.Select(Row));
    }
}
