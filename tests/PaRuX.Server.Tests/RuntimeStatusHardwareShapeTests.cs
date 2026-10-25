using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeStatusHardwareShapeTests
{
    [Fact]
    public void Hardware_status_keeps_every_original_runtime_status_field_at_the_root()
    {
        var status = new RuntimeManagerStatus
        {
            Backends = new List<RuntimeBackendStatus> { new() { Backend = "text-cuda", Installed = true, Running = true, Healthy = true } },
            CurrentManagedProviderEndpoint = "http://127.0.0.1:1234",
            GpuDetected = true,
            VramTotalMib = 16303,
            VramFreeMib = 9000,
            RamTotalBytes = 27_000_000_000,
            GamingActive = true,
            GamingShutdownPending = true,
            LastGamingShutdownFailure = "stuck",
            ActiveRole = "coding",
            ActiveProfile = "Qwen3.5-4B Standard GPU",
            ActiveModelPath = "/models/a.gguf",
            ActiveVisionProjectorPath = "/models/mm.gguf",
            ActiveHasVisionSupport = true,
            LaunchPlanExplanation = "plan",
            TextLaunchTrace = new List<string> { "t1" }
        };

        RuntimeManagerHardwareStatus shaped = RuntimeHardwareEndpointExtensions.FromStatus(status);

        foreach (var property in typeof(RuntimeManagerStatus).GetProperties())
        {
            var target = typeof(RuntimeManagerHardwareStatus).GetProperty(property.Name);
            Assert.True(target != null, "Hardware status drops original field " + property.Name);
            Assert.Equal(property.GetValue(status), target!.GetValue(shaped));
        }
    }
}
