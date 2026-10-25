using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentFileLinesTests
{
    [Fact]
    public void Reads_requested_source_line_instead_of_character_offset()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-lines-" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(1, 2_400).Select(line =>
                line == 2_280 ? "void AggiungiEcuRapido() { }" : "// filler line " + line));

            string result = CodingAgentTools.ReadNumberedFileLines(path, 2_279, 3);

            Assert.Contains("2279: // filler line 2279", result, StringComparison.Ordinal);
            Assert.Contains("2280: void AggiungiEcuRapido() { }", result, StringComparison.Ordinal);
            Assert.Contains("2281: // filler line 2281", result, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TaskRelevantPreviewFindsNamedSymbolDeepInLargeFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, Enumerable.Range(1, 300)
                .Select(line => line == 229 ? "private void AggiungiEcuRapido()" : "ordinary line " + line));

            string preview = CodingAgentTools.ReadTaskRelevantFileLines(
                path, "Change AggiungiEcuRapido and preserve behavior");

            Assert.Contains("229: private void AggiungiEcuRapido()", preview);
            Assert.Contains("228: ordinary line 228", preview);
        }
        finally { File.Delete(path); }
    }
}
