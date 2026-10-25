using System;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Formats a structured Universal Diff result as copyable unified text.</summary>
    public static class UniversalDiffFormatter
    {
        public static string Format(DiffResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var output = new StringBuilder();
            foreach (DiffLine line in result.Lines)
            {
                string prefix = line.Kind switch
                {
                    "added" => "+ ",
                    "removed" => "- ",
                    _ => "  "
                };
                output.Append(prefix).Append(line.Text).Append('\n');
            }

            if (output.Length > 0) output.Length--;
            return output.ToString();
        }
    }
}
