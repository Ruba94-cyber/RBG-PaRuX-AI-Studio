using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedDiffusersResidentProtocolTests
{
    [Fact]
    public void Python_worker_resets_command_before_each_input_frame()
    {
        string source = ManagedDiffusersResidentSession.PythonWorker;
        int loop = source.IndexOf("for raw in sys.stdin:", StringComparison.Ordinal);
        Assert.True(loop >= 0);
        string protocolLoop = source[loop..];

        int reset = protocolLoop.IndexOf("command = {}", StringComparison.Ordinal);
        int parse = protocolLoop.IndexOf("command = json.loads(raw)", StringComparison.Ordinal);
        Assert.True(reset >= 0 && parse > reset,
            "Each protocol iteration must clear command before parsing so malformed JSON cannot inherit a previous request id.");
        Assert.DoesNotContain("if \"command\" in locals()", protocolLoop, StringComparison.Ordinal);
    }

    [Fact]
    public void Resident_worker_is_forced_offline_and_uses_the_pinned_local_model_directory()
    {
        string source = ManagedDiffusersResidentSession.PythonWorker;
        Assert.Contains("HF_HUB_OFFLINE", source, StringComparison.Ordinal);
        Assert.Contains("TRANSFORMERS_OFFLINE", source, StringComparison.Ordinal);
        Assert.Contains("local_files_only=True", source, StringComparison.Ordinal);
        Assert.Contains(ManagedDiffusersImageEngine.ModelRevision, source, StringComparison.Ordinal);
    }
}
