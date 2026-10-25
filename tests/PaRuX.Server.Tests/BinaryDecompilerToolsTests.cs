using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BinaryDecompilerToolsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-decompiler-tests-" + Guid.NewGuid().ToString("N"));

    public BinaryDecompilerToolsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task DecompileAsync_rejects_targets_outside_the_selected_workspace_before_tool_discovery()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BinaryDecompilerTools.DecompileAsync(_tempDir, "../outside.dll", null, CancellationToken.None));

        Assert.Contains("escape", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderStatusAsync_is_bounded_and_truthful_when_optional_tools_are_absent()
    {
        string status = await BinaryDecompilerTools.RenderStatusAsync(CancellationToken.None);

        Assert.Contains("ILSpy", status);
        Assert.Contains("Ghidra", status);
        Assert.True(status.Length < 4096);
    }

    [Fact]
    public async Task DecompileAsync_returns_a_diagnostic_instead_of_fabricating_output_when_no_requested_tool_exists()
    {
        string file = Path.Combine(_tempDir, "blob.bin");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        BinaryDecompilerTools.DecompileResult result = await BinaryDecompilerTools.DecompileAsync(
            _tempDir, "blob.bin", "definitely-not-a-real-decompiler", CancellationToken.None);

        Assert.Equal("none", result.ToolId);
        Assert.Equal(-1, result.ExitCode);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("not found", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrEmpty(result.Output));
    }
}
