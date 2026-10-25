using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers the Adapter Manager: registry CRUD
/// against a real temp SQLite file, export/delete filesystem behavior against real temp
/// directories, TrainingLabService's auto-registration hook, and endpoint auth. The training-job
/// hook is tested directly against AdapterManagerService.RegisterFromTrainingJob rather than
/// running a real Unsloth job (same tier separation this project already applies - a real
/// training run needs a GPU and a multi-minute Python venv, not something a hermetic suite
/// depends on).</summary>
public sealed class AdapterManagerTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "parux-adapter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _filesDirectory;

    public AdapterManagerTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_dbDirectory);
        _filesDirectory = Path.Combine(_dbDirectory, "files");
        Directory.CreateDirectory(_filesDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDirectory, true); } catch { }
    }

    private AdapterManagerService NewService() => new(
        new AdapterStore(_dbDirectory),
        new LoraGgufConversionService(Path.Combine(_dbDirectory, "lora-conversion-runtime"), Path.Combine(_dbDirectory, "lora-conversion-scripts")));

    [Fact]
    public void Register_then_list_then_get_round_trips_through_real_sqlite()
    {
        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest
        {
            Name = "My adapter", BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", AdapterPath = _filesDirectory,
        });

        Assert.True(created.Enabled);
        Assert.Equal("QLoRA", created.Method);
        Assert.Single(adapters.List());
        Assert.Equal("My adapter", adapters.Get(created.Id)?.Name);
    }

    [Fact]
    public void Register_rejects_missing_required_fields()
    {
        AdapterManagerService adapters = NewService();
        Assert.Throws<InvalidDataException>(() => adapters.Register(new RegisterAdapterRequest { Name = "x" }));
    }

    [Fact]
    public void SetEnabled_toggles_and_persists()
    {
        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = _filesDirectory });

        Assert.True(adapters.SetEnabled(created.Id, false));
        Assert.False(adapters.Get(created.Id)!.Enabled);
    }

    [Fact]
    public void Rename_updates_the_name()
    {
        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "old", BaseModel = "m", AdapterPath = _filesDirectory });

        Assert.True(adapters.Rename(created.Id, "new"));
        Assert.Equal("new", adapters.Get(created.Id)?.Name);
    }

    [Fact]
    public void Delete_removes_only_the_registry_row_not_the_files()
    {
        AdapterManagerService adapters = NewService();
        File.WriteAllText(Path.Combine(_filesDirectory, "adapter_model.safetensors"), "fake");
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = _filesDirectory });

        Assert.True(adapters.Delete(created.Id));
        Assert.Null(adapters.Get(created.Id));
        Assert.True(Directory.Exists(_filesDirectory)); // files untouched
    }

    [Fact]
    public void DeleteWithFiles_removes_the_registry_row_and_the_real_directory()
    {
        string adapterDir = Path.Combine(_dbDirectory, "delete-me");
        Directory.CreateDirectory(adapterDir);
        File.WriteAllText(Path.Combine(adapterDir, "adapter_model.safetensors"), "fake");

        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = adapterDir });

        Assert.True(adapters.DeleteWithFiles(created.Id));
        Assert.Null(adapters.Get(created.Id));
        Assert.False(Directory.Exists(adapterDir));
    }

    [Fact]
    public void Export_copies_real_files_to_a_new_real_directory_without_touching_the_source()
    {
        string sourceDir = Path.Combine(_dbDirectory, "source-adapter");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "adapter_model.safetensors"), "fake-weights");
        string destDir = Path.Combine(_dbDirectory, "exported");

        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = sourceDir });

        string result = adapters.Export(created.Id, destDir);

        Assert.True(File.Exists(Path.Combine(result, "adapter_model.safetensors")));
        Assert.True(File.Exists(Path.Combine(sourceDir, "adapter_model.safetensors"))); // source untouched
    }

    [Fact]
    public void Export_refuses_to_export_into_the_adapters_own_source_directory()
    {
        string sourceDir = Path.Combine(_dbDirectory, "source-adapter-2");
        Directory.CreateDirectory(sourceDir);

        AdapterManagerService adapters = NewService();
        AdapterDto created = adapters.Register(new RegisterAdapterRequest { Name = "x", BaseModel = "m", AdapterPath = sourceDir });

        Assert.Throws<InvalidDataException>(() => adapters.Export(created.Id, sourceDir));
    }

    [Fact]
    public void RegisterFromTrainingJob_registers_a_real_completed_jobs_adapter()
    {
        AdapterManagerService adapters = NewService();
        var job = new TrainingJobStatus { Id = "job-1", Status = "completed", AdapterPath = _filesDirectory, CreatedUtc = DateTime.UtcNow };
        var request = new TrainingJobRequest { BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", TrainJsonlPath = "C:\\data\\train.jsonl" };

        AdapterDto? registered = adapters.RegisterFromTrainingJob(job, request);

        Assert.NotNull(registered);
        Assert.Equal("job-1", registered!.SourceTrainingJobId);
        Assert.Equal(request.TrainJsonlPath, registered.SourceDatasetPath);
        Assert.Single(adapters.List());
    }

    [Fact]
    public void RegisterFromTrainingJob_fails_visibly_when_the_adapter_directory_is_missing()
    {
        AdapterManagerService adapters = NewService();
        var job = new TrainingJobStatus { Id = "job-2", Status = "completed", AdapterPath = Path.Combine(_dbDirectory, "never-created") };
        var request = new TrainingJobRequest();

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => adapters.RegisterFromTrainingJob(job, request));

        Assert.Contains("no longer exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(adapters.List());
    }

    [Fact]
    public async Task Adapters_list_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/adapters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Adapters_list_endpoint_accepts_the_desktop_session_token()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/adapters");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
