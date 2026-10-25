using System.Collections.Generic;

public static class CsvWriter
{
    public static string Row(IEnumerable<string> fields)
    {
        return string.Join(",", fields);
    }
}
