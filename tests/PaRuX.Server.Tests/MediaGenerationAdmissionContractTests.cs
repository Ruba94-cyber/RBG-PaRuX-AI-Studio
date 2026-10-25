using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaGenerationAdmissionContractTests
{
    [Fact]
    public void Media_admission_measures_the_replacement_runtime_before_generation()
    {
        string root = FindRepositoryRoot();
        string generation = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "MediaGenerationService.cs"));
        string runtime = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "LocalRuntimeService.cs"));
        string comfy = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "ComfyWorkflowRunner.cs"));

        int quiesce = generation.IndexOf("PrepareForExclusiveMediaAsync(ct)", StringComparison.Ordinal);
        int startMedia = generation.IndexOf("PrepareMediaRuntimeForAdmissionAsync(ct)", StringComparison.Ordinal);
        int admission = generation.IndexOf("CheckMediaWorkloadAdmissionAsync(ct)", StringComparison.Ordinal);
        int execution = generation.IndexOf("_comfy.RunImageAsync", StringComparison.Ordinal);
        Assert.True(quiesce >= 0 && startMedia > quiesce && admission > startMedia && execution > admission,
            "Media admission must drain text, start the replacement runtime, then measure it before generation.");

        Assert.Contains("_process.StartMediaAsync(cancellationToken)", runtime, StringComparison.Ordinal);
        int thermalCheck = runtime.IndexOf("enforceCurrentSystemRam: false", StringComparison.Ordinal);
        int runtimeStart = runtime.IndexOf("_process.StartMediaAsync(cancellationToken)", StringComparison.Ordinal);
        Assert.True(thermalCheck >= 0 && runtimeStart > thermalCheck,
            "Thermal admission must run before loading the replacement runtime.");
        Assert.Contains("ExecutionResourceGuard.CheckCurrentAsync(profile, enforceCurrentVram: false, cancellationToken)", runtime, StringComparison.Ordinal);
        Assert.Contains("PrepareAndAdmitMediaAsync(", generation, StringComparison.Ordinal);
        Assert.Contains("ScheduleTextRestoreAfterFailedGeneration", generation, StringComparison.Ordinal);
        Assert.Contains("TriggerBackgroundTextRestore()", comfy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_media_preflight_schedules_text_restore()
    {
        var order = new List<string>();
        Task StartMediaRuntime()
        {
            order.Add("start");
            return Task.CompletedTask;
        }
        Task<ExecutionResourceDecision> RejectResources()
        {
            order.Add("check");
            return Task.FromResult(new ExecutionResourceDecision(
                Allowed: false,
                TelemetryComplete: true,
                SystemRamUsedPercent: 91.2,
                MaximumVramUsedPercent: null,
                MaximumGpuTemperatureC: null,
                Message: "system RAM 91.2% > 86%"));
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaGenerationService.PrepareAndAdmitMediaAsync(
                StartMediaRuntime,
                RejectResources,
                () => order.Add("restore")));

        Assert.Contains("system RAM", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "start", "check", "restore" }, order);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }
}
