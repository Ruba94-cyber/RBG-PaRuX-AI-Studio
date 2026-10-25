using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingRuntimeV2Tests
{
    [Fact]
    public void Contract_maps_verified_runtime_identity_without_inventing_cuda()
    {
        var probe = new TrainingRuntimeProbe
        {
            ImportsValid = true,
            PythonVersion = TrainingRuntimeCatalog.PythonVersion,
            UnslothVersion = TrainingRuntimeCatalog.UnslothVersion,
            TorchVersion = "2.9.0+rocm",
            Backend = "rocm-cuda-api",
            Detail = "imports-ok"
        };
        var manifest = new TrainingRuntimeManifest
        {
            SchemaVersion = TrainingRuntimeCatalog.ManifestSchemaVersion,
            PythonVersion = TrainingRuntimeCatalog.PythonVersion,
            UnslothVersion = TrainingRuntimeCatalog.UnslothVersion,
            TorchVersion = probe.TorchVersion,
            Backend = probe.Backend,
            ResolvedPackagesSha256 = new string('a', 64)
        };
        var status = new TrainingRuntimeStatus(
            Installed: true,
            Ready: true,
            Probe: probe,
            Manifest: manifest,
            AvailableMethods: TrainingMethod.All,
            Detail: "verified");

        TrainingEngineStatus dto = TrainingRuntimeV2Endpoints.ToContract(status);

        Assert.True(dto.Installed);
        Assert.True(dto.Ready);
        Assert.Equal("rocm-cuda-api", dto.Backend);
        Assert.False(dto.CudaVerified);
        Assert.Equal(TrainingRuntimeCatalog.PythonVersion, dto.PythonVersion);
        Assert.Equal(TrainingRuntimeCatalog.UnslothVersion, dto.UnslothVersion);
        Assert.Equal(manifest.ResolvedPackagesSha256, dto.ResolvedPackagesSha256);
        Assert.Equal(TrainingMethod.All, dto.AvailableMethods);
    }

    [Fact]
    public void Job_gate_rejects_installed_runtime_that_is_not_verified_ready()
    {
        var status = new TrainingRuntimeStatus(
            Installed: true,
            Ready: false,
            Probe: new TrainingRuntimeProbe { ImportsValid = true, Backend = "xpu-unvalidated" },
            Manifest: null,
            AvailableMethods: Array.Empty<string>(),
            Detail: "backend requires validation");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            TrainingRuntimeJobGate.ValidateStatus(status, TrainingMethod.Sft));
        Assert.Contains("not ready", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Job_gate_rejects_method_not_advertised_for_backend()
    {
        var status = new TrainingRuntimeStatus(
            Installed: true,
            Ready: true,
            Probe: new TrainingRuntimeProbe { ImportsValid = true, Backend = "cpu" },
            Manifest: null,
            AvailableMethods: new[] { TrainingMethod.TtsSft },
            Detail: "cpu tts only");

        InvalidOperationException error = Assert.ThrowsAny<InvalidOperationException>(() =>
            TrainingRuntimeJobGate.ValidateStatus(status, TrainingMethod.Sft));
        Assert.Contains("not enabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cpu", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TrainingEligibility.NonCudaUnsupported, Assert.IsType<TrainingRuntimeRefusalException>(error).Code);
    }

    [Fact]
    public void Job_gate_accepts_method_explicitly_advertised_for_backend()
    {
        var status = new TrainingRuntimeStatus(
            Installed: true,
            Ready: true,
            Probe: new TrainingRuntimeProbe { ImportsValid = true, Backend = "cpu" },
            Manifest: null,
            AvailableMethods: new[] { TrainingMethod.TtsSft },
            Detail: "cpu tts only");

        TrainingRuntimeJobGate.ValidateStatus(status, TrainingMethod.TtsSft);
    }

    [Fact]
    public void Windows_first_party_client_uses_v2_for_runtime_and_job_admission()
    {
        string root = FindRepositoryRoot();
        string client = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Services", "TrainingLabClient.cs"));

        Assert.Contains("/api/v2/training/engine", client, StringComparison.Ordinal);
        Assert.Contains("/api/v2/training/engine/install", client, StringComparison.Ordinal);
        Assert.Contains("/api/v2/training/jobs", client, StringComparison.Ordinal);
    }

    [Fact]
    public void Training_service_enforces_runtime_gate_before_any_job_is_queued()
    {
        string root = FindRepositoryRoot();
        string service = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "TrainingLabService.cs"));
        int createJob = service.IndexOf("public TrainingJobStatus CreateJob(TrainingJobRequest request)", StringComparison.Ordinal);
        int runtimeGate = service.IndexOf(
            "TrainingRuntimeJobGate.GetVerifiedStatus(RuntimeRoot, VenvRoot, method)",
            createJob,
            StringComparison.Ordinal);
        int journal = service.IndexOf("PersistEntry(entry);", createJob, StringComparison.Ordinal);
        int enqueue = service.IndexOf("_jobs[status.Id] = entry;", createJob, StringComparison.Ordinal);

        Assert.True(createJob >= 0, "TrainingLabService.CreateJob was not found.");
        Assert.True(runtimeGate > createJob, "CreateJob must enforce the verified runtime boundary itself.");
        Assert.True(journal > runtimeGate, "A durable recovery journal must exist after runtime admission.");
        Assert.True(enqueue > journal, "The job must not become visible before its recovery journal is durable.");
    }

    [Fact]
    public void Core_startup_eagerly_resolves_training_service_for_recovery()
    {
        string root = FindRepositoryRoot();
        string endpoints = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "TrainingRuntimeV2Endpoints.cs"));

        Assert.Contains("app.Services.GetRequiredService<TrainingLabService>()", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_training_branch_saves_and_resumes_trainer_checkpoints()
    {
        string root = FindRepositoryRoot();
        string service = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "TrainingLabService.cs"));

        Assert.Equal(5, CountOccurrences(service, "save_strategy=\"steps\""));
        Assert.Equal(5, CountOccurrences(service, "trainer.train(resume_from_checkpoint=resume_checkpoint)"));
        Assert.Contains("parux-training-complete", service, StringComparison.Ordinal);
        Assert.Contains("FindLatestCompleteCheckpoint", service, StringComparison.Ordinal);
        Assert.Contains("Recovery deferred by Gaming Mode", service, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test output directory.");
    }
}
