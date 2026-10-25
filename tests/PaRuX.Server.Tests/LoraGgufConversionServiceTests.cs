using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers LoraGgufConversionService (2026-08-27, closes the Adapter Manager Completion Gate's own
/// disclosed "GGUF-conversion pipeline itself ... remains real, disclosed future work" gap) and
/// AdapterManagerService.ConvertToGgufAsync's wiring around it. Every validation path here is
/// hermetic - it never launches Python, creates a venv, or installs a package, matching this
/// project's own established convention (TrainingLabService/RuntimeInstallService) of keeping a
/// multi-minute real engine install out of the permanent hermetic suite. The real end-to-end
/// conversion (venv creation, pip install, an actual convert_lora_to_gguf.py run against a real
/// synthetic PEFT adapter) requires a separate live run against a real server.
/// These mocked tests do not establish that live acceptance evidence.
/// </summary>
public sealed class LoraGgufConversionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-lora-conversion-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string RuntimeDir => Path.Combine(_root, "runtime");
    private string ScriptsDir => Path.Combine(_root, "scripts");

    private void CreateFakeVenvPython()
    {
        string python = OperatingSystem.IsWindows()
            ? Path.Combine(RuntimeDir, ".venv", "Scripts", "python.exe")
            : Path.Combine(RuntimeDir, ".venv", "bin", "python3");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "# stand-in, never actually executed by this test");
    }

    [Fact]
    public void BuildConvertArguments_produces_the_exact_flags_convert_lora_to_gguf_expects()
    {
        List<string> args = LoraGgufConversionService.BuildConvertArguments(
            "/scripts/convert_lora_to_gguf.py", "/adapters/my-adapter", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            "/out/adapter.gguf", "f16");

        Assert.Equal(new[]
        {
            "/scripts/convert_lora_to_gguf.py",
            "--outfile", "/out/adapter.gguf",
            "--outtype", "f16",
            "--base-model-id", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            "/adapters/my-adapter",
        }, args);
    }

    [Fact]
    public void BuildConvertArguments_uses_base_dir_instead_of_base_model_id_when_given()
    {
        List<string> args = LoraGgufConversionService.BuildConvertArguments(
            "/scripts/convert_lora_to_gguf.py", "/adapters/my-adapter", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            "/out/adapter.gguf", "f16", "/local/base-model-config");

        // The script's own argparse help says --base-model-id "will ignore --base" - so when a
        // local config dir is given, --base-model-id must never appear at all, not just be
        // shadowed, or a caller reading the arg list back could reasonably assume both apply.
        Assert.Contains("--base", args);
        Assert.Contains("/local/base-model-config", args);
        Assert.DoesNotContain("--base-model-id", args);
        Assert.DoesNotContain("unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", args);
    }

    [Fact]
    public void BuildConvertArguments_never_mutates_or_splits_a_path_containing_spaces()
    {
        List<string> args = LoraGgufConversionService.BuildConvertArguments(
            "/scripts/convert_lora_to_gguf.py", "/adapters/my adapter with spaces", "base/model",
            "/out/my output.gguf", "auto");

        // Each path stays a single element - ProcessStartInfo.ArgumentList (what these feed into)
        // passes each element as its own argv entry, so a space here can never be misparsed the
        // way a hand-quoted single command-line string could be.
        Assert.Contains("/adapters/my adapter with spaces", args);
        Assert.Contains("/out/my output.gguf", args);
        Assert.Equal(8, args.Count);
    }

    [Fact]
    public async Task GetEngineStatusAsync_reports_scripts_unavailable_when_the_vendored_files_are_missing()
    {
        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir); // ScriptsDir deliberately never populated
        LoraConversionEngineStatus status = await service.GetEngineStatusAsync(CancellationToken.None);

        Assert.False(status.ScriptsAvailable);
        Assert.False(status.Installed);
    }

    [Fact]
    public async Task GetEngineStatusAsync_reports_not_installed_when_scripts_exist_but_no_venv_does()
    {
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(Path.Combine(ScriptsDir, "convert_lora_to_gguf.py"), "# stand-in for the real vendored script");

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        LoraConversionEngineStatus status = await service.GetEngineStatusAsync(CancellationToken.None);

        Assert.True(status.ScriptsAvailable);
        Assert.False(status.Installed);
    }

    /// <summary>The real vendored scripts (llama-cpp-conversion/) ship next to the server binary
    /// and propagate to this test project's own output directory via the normal ProjectReference
    /// Content-copy behavior (confirmed: no special wiring needed for this). Using the
    /// parameterless constructor - the same one Program.cs's DI singleton uses - proves the actual
    /// packaged artifact is correct, not just a test double standing in for it.</summary>
    [Fact]
    public void Default_constructor_resolves_the_real_vendored_conversion_script()
    {
        var service = new LoraGgufConversionService();
        Assert.True(service.ScriptsAvailable);
    }

    [Fact]
    public async Task InstallEngineAsync_refuses_when_the_vendored_scripts_are_missing()
    {
        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallEngineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConvertAsync_rejects_a_nonexistent_adapter_directory_before_touching_anything()
    {
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(Path.Combine(ScriptsDir, "convert_lora_to_gguf.py"), "# stand-in");
        CreateFakeVenvPython();

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAsync(
            Path.Combine(_root, "no-such-adapter"), "some/base-model", Path.Combine(_root, "out.gguf"), "f16", CancellationToken.None));
    }

    [Fact]
    public async Task ConvertAsync_rejects_an_unknown_outtype()
    {
        string adapterDir = Path.Combine(_root, "adapter");
        Directory.CreateDirectory(adapterDir);
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(Path.Combine(ScriptsDir, "convert_lora_to_gguf.py"), "# stand-in");
        CreateFakeVenvPython();

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAsync(
            adapterDir, "some/base-model", Path.Combine(_root, "out.gguf"), "not-a-real-outtype", CancellationToken.None));
        Assert.Contains("not-a-real-outtype", ex.Message);
    }

    [Fact]
    public async Task ConvertAsync_rejects_a_blank_base_model_id()
    {
        string adapterDir = Path.Combine(_root, "adapter");
        Directory.CreateDirectory(adapterDir);
        Directory.CreateDirectory(ScriptsDir);
        File.WriteAllText(Path.Combine(ScriptsDir, "convert_lora_to_gguf.py"), "# stand-in");
        CreateFakeVenvPython();

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAsync(
            adapterDir, "  ", Path.Combine(_root, "out.gguf"), "f16", CancellationToken.None));
    }

    [Fact]
    public void BuildMergeArguments_resolves_a_base_model_id_when_no_local_dir_is_given()
    {
        List<string> args = LoraGgufConversionService.BuildMergeArguments(
            "/scripts/merge_lora.py", "/adapters/my-adapter", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", "/out/merged-model");

        Assert.Equal(new[]
        {
            "/scripts/merge_lora.py",
            "--base", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            "--outdir", "/out/merged-model",
            "/adapters/my-adapter",
        }, args);
    }

    [Fact]
    public void BuildMergeArguments_prefers_a_local_base_model_dir_over_the_hf_id_when_given()
    {
        List<string> args = LoraGgufConversionService.BuildMergeArguments(
            "/scripts/merge_lora.py", "/adapters/my-adapter", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            "/out/merged-model", "/local/full-base-model-checkout");

        Assert.Contains("/local/full-base-model-checkout", args);
        Assert.DoesNotContain("unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", args);
    }

    /// <summary>
    /// Everything about this request is valid except that the tooling is not installed, which is
    /// the only way to actually assert "engine missing" now that arguments are validated first.
    /// The adapter directory is created for the same reason: passing a path that does not exist
    /// used to make this test pass for the wrong reason on a machine with no venv, and fail on one
    /// that had it.
    /// </summary>
    [Fact]
    public async Task MergeAsync_refuses_when_the_engine_is_not_installed()
    {
        string adapter = Path.Combine(_root, "adapter");
        Directory.CreateDirectory(adapter);

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir); // no venv created
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.MergeAsync(adapter, "some/base-model", Path.Combine(_root, "merged"), CancellationToken.None));
        Assert.Contains("not installed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeAsync_rejects_a_nonexistent_adapter_directory()
    {
        CreateFakeVenvPython();

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.MergeAsync(
            Path.Combine(_root, "no-such-adapter"), "some/base-model", Path.Combine(_root, "merged"), CancellationToken.None));
    }

    [Fact]
    public async Task MergeAsync_rejects_a_nonexistent_local_base_model_dir()
    {
        string adapterDir = Path.Combine(_root, "adapter");
        Directory.CreateDirectory(adapterDir);
        CreateFakeVenvPython();

        var service = new LoraGgufConversionService(RuntimeDir, ScriptsDir);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.MergeAsync(
            adapterDir, "some/base-model", Path.Combine(_root, "merged"), CancellationToken.None, Path.Combine(_root, "no-such-base-dir")));
    }
}

/// <summary>AdapterManagerService.ConvertToGgufAsync's own validation, hermetic against a real
/// temp SQLite adapters.db but a LoraGgufConversionService pointed at an isolated temp directory
/// with no vendored scripts - so these tests exercise the real wiring end to end up to (but never
/// including) an actual subprocess launch.</summary>
public sealed class AdapterManagerConversionTests : IDisposable
{
    private readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "parux-adapter-conversion-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _filesDirectory;

    public AdapterManagerConversionTests()
    {
        Directory.CreateDirectory(_dbDirectory);
        _filesDirectory = Path.Combine(_dbDirectory, "files");
        Directory.CreateDirectory(_filesDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDirectory, recursive: true); } catch { /* best effort */ }
    }

    private AdapterManagerService NewService() => new(
        new AdapterStore(_dbDirectory),
        new LoraGgufConversionService(Path.Combine(_dbDirectory, "lora-conversion-runtime"), Path.Combine(_dbDirectory, "lora-conversion-scripts")));

    [Fact]
    public async Task ConvertToGgufAsync_rejects_an_unknown_adapter_id()
    {
        AdapterManagerService adapters = NewService();
        await Assert.ThrowsAsync<InvalidDataException>(() => adapters.ConvertToGgufAsync("parux-test-does-not-exist", "f16", CancellationToken.None));
    }

    [Fact]
    public async Task ConvertToGgufAsync_rejects_an_adapter_whose_files_are_gone()
    {
        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest
        {
            Name = "x", BaseModel = "m", AdapterPath = Path.Combine(_dbDirectory, "deleted-adapter-dir")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapters.ConvertToGgufAsync(created.Id, "f16", CancellationToken.None));
    }

    [Fact]
    public async Task MergeToBaseModelAsync_rejects_an_unknown_adapter_id()
    {
        AdapterManagerService adapters = NewService();
        await Assert.ThrowsAsync<InvalidDataException>(() => adapters.MergeToBaseModelAsync(
            "parux-test-does-not-exist", Path.Combine(_dbDirectory, "merged-out"), CancellationToken.None));
    }

    [Fact]
    public async Task MergeToBaseModelAsync_rejects_an_adapter_whose_files_are_gone()
    {
        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest
        {
            Name = "x", BaseModel = "m", AdapterPath = Path.Combine(_dbDirectory, "deleted-adapter-dir")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapters.MergeToBaseModelAsync(
            created.Id, Path.Combine(_dbDirectory, "merged-out"), CancellationToken.None));
    }

    [Fact]
    public async Task MergeToBaseModelAsync_rejects_a_blank_output_directory()
    {
        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = _filesDirectory });

        await Assert.ThrowsAsync<InvalidDataException>(() => adapters.MergeToBaseModelAsync(created.Id, "  ", CancellationToken.None));
    }

    [Fact]
    public void GgufPath_round_trips_through_SetGgufPath_and_is_null_until_set()
    {
        var store = new AdapterStore(_dbDirectory);
        AdapterDto created = store.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = _filesDirectory });
        Assert.Null(store.Get(created.Id)!.GgufPath);

        string ggufPath = Path.Combine(_filesDirectory, "adapter.gguf");
        AdapterDto? updated = store.SetGgufPath(created.Id, ggufPath);

        Assert.NotNull(updated);
        Assert.Equal(ggufPath, updated!.GgufPath);
        Assert.Equal(ggufPath, store.Get(created.Id)!.GgufPath);
    }

    [Fact]
    public void SetGgufPath_returns_null_for_an_adapter_deleted_concurrently()
    {
        var store = new AdapterStore(_dbDirectory);
        AdapterDto created = store.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = _filesDirectory });
        store.Delete(created.Id);

        Assert.Null(store.SetGgufPath(created.Id, Path.Combine(_filesDirectory, "adapter.gguf")));
    }
}
