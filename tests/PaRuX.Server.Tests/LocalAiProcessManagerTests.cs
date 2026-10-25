using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// 2026-08-27, LoRA-at-inference: BuildLoraArguments is a pure argument-list construction with no
/// process/filesystem access, so unlike the rest of LocalAiProcessManager (which reaches a real,
/// shared machine-wide llama-server PID and is deliberately never exercised end-to-end in this
/// hermetic suite - see RuntimeManagerTests' own doc comment for that established convention) this
/// one function is fully safe to test directly.
/// </summary>
public sealed class LocalAiProcessManagerTests
{
    [Fact]
    public void BuildLoraArguments_returns_empty_for_null_or_no_adapters()
    {
        Assert.Empty(LocalAiProcessManager.BuildLoraArguments(null));
        Assert.Empty(LocalAiProcessManager.BuildLoraArguments(Array.Empty<string>()));
    }

    [Fact]
    public void BuildLoraArguments_emits_one_lora_flag_pair_per_adapter_in_order()
    {
        List<string> args = LocalAiProcessManager.BuildLoraArguments(new[] { "C:\\adapters\\a.gguf", "C:\\adapters\\b.gguf" });

        Assert.Equal(new[] { "--lora", "C:\\adapters\\a.gguf", "--lora", "C:\\adapters\\b.gguf" }, args);
    }

    [Fact]
    public void BuildLoraArguments_does_not_quote_or_mutate_the_path_itself()
    {
        // ArgumentList.Add (not a shell string) is what actually launches the process - this
        // function's only job is producing the flag/value pairs, never shell-escaping, since
        // there is no shell involved (see LaunchAndWaitAsync's use of ProcessStartInfo.ArgumentList).
        List<string> args = LocalAiProcessManager.BuildLoraArguments(new[] { "path with spaces/adapter.gguf" });
        Assert.Equal("path with spaces/adapter.gguf", args[1]);
    }
}

/// <summary>LocalAiPaths.SetRoleAdapter writes to a real, fixed %LOCALAPPDATA%\PaRuX\
/// model-role-adapters.json - same hazard ModelRolesTests' own doc comment documents for
/// SetRoleOverride, same fix: only the validation paths that fail BEFORE any write are safe to
/// exercise directly. GetRoleAdapter/ClearRoleAdapter on a role nothing has ever assigned are pure
/// reads/no-ops and safe regardless.</summary>
public sealed class LocalAiPathsRoleAdapterTests
{
    [Fact]
    public void SetRoleAdapter_rejects_an_unknown_role_before_any_write()
    {
        Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetRoleAdapter("not-a-real-role", "C:\\anything.gguf"));
    }

    [Fact]
    public void SetRoleAdapter_rejects_a_missing_file_before_any_write()
    {
        string missing = Path.Combine(Path.GetTempPath(), "parux-adapter-test-missing-" + Guid.NewGuid().ToString("N") + ".gguf");
        Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetRoleAdapter("fast", missing));
    }

    [Fact]
    public void SetRoleAdapter_rejects_a_non_gguf_file_with_an_honest_conversion_hint()
    {
        // A real, existing file - but not .gguf - to reach the extension check specifically,
        // without ever calling the actual sidecar-file write.
        string notGguf = Path.Combine(Path.GetTempPath(), "parux-adapter-test-" + Guid.NewGuid().ToString("N") + ".safetensors");
        File.WriteAllText(notGguf, "not a gguf adapter");
        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetRoleAdapter("fast", notGguf));
            Assert.Contains("convert_lora_to_gguf.py", ex.Message);
        }
        finally { try { File.Delete(notGguf); } catch { /* best effort */ } }
    }

    [Fact]
    public void GetRoleAdapter_returns_null_after_clearing_any_prior_assignment()
    {
        // ClearRoleAdapter first rather than assuming a fresh machine has never assigned one -
        // deterministic regardless of what real state this specific developer machine happens to
        // already have, matching this file's own real-shared-state discipline.
        LocalAiPaths.ClearRoleAdapter("vision");
        Assert.Null(LocalAiPaths.GetRoleAdapter("vision"));
    }

    [Fact]
    public void ClearRoleAdapter_on_an_unassigned_role_is_a_safe_no_op()
    {
        LocalAiPaths.ClearRoleAdapter("vision");
        LocalAiPaths.ClearRoleAdapter("vision"); // calling it again must not throw
        Assert.Null(LocalAiPaths.GetRoleAdapter("vision"));
    }
}
