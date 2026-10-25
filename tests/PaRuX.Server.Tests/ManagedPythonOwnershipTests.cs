using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Defense-in-depth regression coverage for the PaRuX-managed Python/uv invariant. These checks
/// exercise LocalAiPaths directly rather than the HTTP/ComponentInventory layer, so a future API
/// refactor cannot accidentally make a persisted PATH/user binding authoritative again.
/// </summary>
public sealed class ManagedPythonOwnershipTests
{
    [Theory]
    [InlineData("python")]
    [InlineData("uv")]
    [InlineData("PYTHON")]
    [InlineData("Uv")]
    public void Managed_python_components_cannot_be_located_or_skipped(string id)
    {
        string standIn = Path.GetTempFileName();
        try
        {
            Assert.Null(LocalAiPaths.GetComponentLocation(id));
            Assert.False(LocalAiPaths.IsComponentSkipped(id));
            Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetComponentLocation(id, standIn));
            Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetComponentSkipped(id));

            // Clearing a stale legacy preference is deliberately harmless/no-op for these ids.
            LocalAiPaths.ClearComponentLocation(id);
            LocalAiPaths.ClearComponentSkipped(id);
            Assert.Null(LocalAiPaths.GetComponentLocation(id));
            Assert.False(LocalAiPaths.IsComponentSkipped(id));
        }
        finally
        {
            File.Delete(standIn);
        }
    }
}
