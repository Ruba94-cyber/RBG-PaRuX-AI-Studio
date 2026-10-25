using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>BIC-08: DirectML is an explicit, adapter-pinned, evidence-verified engine variant; built-in
/// providers never leak into discovery or AUTO; thinking output is pinned for the GenAI Jinja engine.</summary>
public sealed class WindowsMlDirectMlPinTests
{
    private static WindowsMlProviderInfo Provider(string name) => new()
    {
        Name = name, ReadyState = "Ready", LibraryFileVersion = "1.0", LibrarySha256 = new string('c', 64)
    };

    private static WindowsMlGenAiWorkerEvent Ready(string provider, string? pin, string? evidence, params string[] devices) => new()
    {
        Type = "ready",
        Provider = provider,
        ThinkingControl = WindowsMlGenAiProtocol.ThinkingControlJinja,
        CancellationControl = WindowsMlGenAiProtocol.RequestCancellationControl,
        PromptCountingControl = WindowsMlGenAiProtocol.PromptCountingControl,
        DeviceEvidence = devices.Length == 1 ? WindowsMlGenAiProtocol.SingleCompatibleDeviceEvidence : "multiple-compatible-ep-devices",
        PinnedAdapter = pin,
        AdapterEvidence = evidence,
        CompatibleDevices = devices.ToList()
    };

    private const string Rtx = "DmlExecutionProvider|GPU|NVIDIA|10de|2c02";
    private const string Amd = "DmlExecutionProvider|GPU|AMD|1002|1900";
    private const string Verified = "process-video-memory 1002:1900=0MB,10de:2c02=2087MB";

    [Fact]
    public void Pinned_identity_round_trips_and_only_directml_may_be_pinned()
    {
        string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider("DmlExecutionProvider"), "10DE:2C02");
        Assert.EndsWith("|adapter=10de:2c02", identity, StringComparison.Ordinal);
        Assert.True(WindowsMlGenAiProtocol.TryParseRuntimeIdentity(identity, out string name, out _, out _, out string? pin));
        Assert.Equal("DmlExecutionProvider", name);
        Assert.Equal("10de:2c02", pin);

        // different pin => different identity (separate Engine Race evidence)
        Assert.NotEqual(identity, WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider("DmlExecutionProvider"), "1002:1900"));
        Assert.Throws<InvalidDataException>(() =>
            WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider("CPUExecutionProvider"), "10de:2c02"));
        Assert.Throws<InvalidDataException>(() =>
            WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider("DmlExecutionProvider"), "nonsense"));
        Assert.False(WindowsMlGenAiProtocol.TryParseRuntimeIdentity(
            WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider("CPUExecutionProvider")) + "|adapter=10de:2c02",
            out _, out _, out _, out _));
    }

    [Fact]
    public void Handshake_requires_a_pin_for_directml_and_verifies_presence_and_execution_evidence()
    {
        // happy path
        string device = WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("DmlExecutionProvider", "10de:2c02", Verified, Rtx), "DmlExecutionProvider",
            expectedAdapterPin: "10de:2c02");
        Assert.Equal(Rtx, device);

        // DirectML without a pin is refused (no implicit adapter choice)
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("DmlExecutionProvider", null, null, Rtx), "DmlExecutionProvider"));
        // two adapters enumerated
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("DmlExecutionProvider", "10de:2c02", Verified, Rtx, Amd), "DmlExecutionProvider",
            expectedAdapterPin: "10de:2c02"));
        // the pinned adapter is gone / a different one is the sole device
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("DmlExecutionProvider", "10de:2c02", Verified, Amd), "DmlExecutionProvider",
            expectedAdapterPin: "10de:2c02"));
        // worker did not echo the pin
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("DmlExecutionProvider", "1002:1900", Verified, Rtx), "DmlExecutionProvider",
            expectedAdapterPin: "10de:2c02"));
        // device list alone is not execution proof
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("DmlExecutionProvider", "10de:2c02", null, Rtx), "DmlExecutionProvider",
            expectedAdapterPin: "10de:2c02"));
        // pins are meaningless (and refused) for other providers
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            Ready("CPUExecutionProvider", "10de:2c02", Verified, "CPUExecutionProvider|CPU|AMD|1022|0007"),
            "CPUExecutionProvider", expectedAdapterPin: "10de:2c02"));
    }

    [Fact]
    public void Thinking_control_stays_strict_by_default_for_the_handshake()
    {
        WindowsMlGenAiWorkerEvent ready = Ready("QNNExecutionProvider", null, null, "QNNExecutionProvider|NPU|Qualcomm|5143|0001");
        ready.ThinkingControl = "not-exposed-by-template";
        Assert.Throws<InvalidDataException>(() =>
            WindowsMlGenAiProtocol.ValidateReadyHandshake(ready, "QNNExecutionProvider"));
        Assert.Equal("QNNExecutionProvider|NPU|Qualcomm|5143|0001",
            WindowsMlGenAiProtocol.ValidateReadyHandshake(ready, "QNNExecutionProvider", requireThinkingControl: false));
    }

    [Fact]
    public void Default_adapter_policy_prefers_largest_dedicated_vram_non_software_allowlisted_unique_adapter()
    {
        static GpuAdapterStatus A(uint vendor, uint device, long vram, bool software = false) => new()
        {
            VendorId = vendor, DeviceId = device, DedicatedVideoMemoryBytes = vram, IsSoftware = software
        };
        var inventory = new GpuInventoryStatus
        {
            Available = true,
            Adapters =
            {
                A(0x1002, 0x1900, 512L << 20),
                A(0x10de, 0x2c02, 16L << 30),
                A(0x1414, 0x008c, 64L << 30, software: true)   // Microsoft Basic Render Driver
            }
        };
        Assert.Equal("10de:2c02", WindowsMlDmlAdapterPolicy.SelectDefaultPin(inventory));

        inventory.Adapters.Add(A(0x10de, 0x2c02, 16L << 30)); // two identical cards cannot be told apart by a pin
        Assert.Null(WindowsMlDmlAdapterPolicy.SelectDefaultPin(inventory));
        Assert.Null(WindowsMlDmlAdapterPolicy.SelectDefaultPin(new GpuInventoryStatus { Available = false }));
        Assert.Null(WindowsMlDmlAdapterPolicy.SelectDefaultPin(new GpuInventoryStatus
        {
            Available = true, Adapters = { A(0x1234, 1, 8L << 30), A(0x10de, 1, 8L << 30, software: true) }
        }));
    }

    [Fact]
    public void Default_adapter_policy_breaks_equal_vram_ties_deterministically()
    {
        static GpuAdapterStatus A(uint vendor, uint device) => new()
        {
            VendorId = vendor, DeviceId = device, DedicatedVideoMemoryBytes = 8L << 30
        };
        var forward = new GpuInventoryStatus { Available = true, Adapters = { A(0x10de, 0x2000), A(0x1002, 0x7000), A(0x10de, 0x1000) } };
        var reversed = new GpuInventoryStatus { Available = true, Adapters = { A(0x10de, 0x1000), A(0x1002, 0x7000), A(0x10de, 0x2000) } };
        Assert.Equal("1002:7000", WindowsMlDmlAdapterPolicy.SelectDefaultPin(forward));
        Assert.Equal("1002:7000", WindowsMlDmlAdapterPolicy.SelectDefaultPin(reversed));
    }

    [Fact]
    public void Worker_status_and_discovery_never_report_the_built_in_providers()
    {
        string root = FindRoot();
        string program = File.ReadAllText(Path.Combine(root, "src", "PaRuX.WinMlWorker", "Program.cs"));
        int statusStart = program.IndexOf("case \"status\":", StringComparison.Ordinal);
        int statusEnd = program.IndexOf("case \"ensure-tensorrt-rtx\":", StringComparison.Ordinal);
        Assert.True(statusStart > 0 && statusEnd > statusStart);
        string statusBranch = program[statusStart..statusEnd];
        Assert.DoesNotContain("BuiltInProviders", statusBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("CPUExecutionProvider", statusBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("DmlExecutionProvider", statusBranch, StringComparison.Ordinal);
        // Status() / Providers() build discovery output from the Windows ML catalog only.
        int helpers = program.IndexOf("static WorkerResult Status(", StringComparison.Ordinal);
        string statusHelper = program[helpers..program.IndexOf("static ProviderInfo Provider(", StringComparison.Ordinal)];
        Assert.DoesNotContain("BuiltInProviders", statusHelper, StringComparison.Ordinal);
        // The only descriptor consumers are the explicit ensure-provider branch and the class itself.
        Assert.Equal(1, CountOccurrences(program, "BuiltInProviders.Describe("));
    }

    [Theory]
    [InlineData("CPUExecutionProvider", null)]
    [InlineData("DmlExecutionProvider", null)]
    public void Auto_never_applies_built_in_provider_evidence_unless_pinned(string provider, string? pin)
    {
        ManagedModelVariantIdentity baseline = new("m/x", "rev", "q4.gguf", ManagedModelFormat.Gguf, "q4", "C:/m/q4.gguf");
        ManagedModelVariantIdentity candidate = new("m/x", "rev", "onnx", ManagedModelFormat.Onnx, "int4", "C:/m/onnx");
        string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider(provider), pin);

        // not promoted evidence never applies, whatever the provider
        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(
            Winner(baseline, candidate, identity, promote: false), baseline));
        // an unpinned DirectML identity never drives AUTO even with promoted evidence
        if (provider == "DmlExecutionProvider")
            Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(
                Winner(baseline, candidate, identity, promote: true), baseline));
        // a pinned identity is only an evidence key; applicability still needs the full guarded record
        string pinned = WindowsMlGenAiProtocol.BuildRuntimeIdentity(Provider("DmlExecutionProvider"), "10de:2c02");
        Assert.True(LocalInferenceEngineCoordinator.IsApplicableWinner(
            Winner(baseline, candidate, pinned, promote: true), baseline));
        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(
            Winner(baseline, candidate, pinned, promote: false), baseline));
    }

    [Fact]
    public void Thinking_binding_pins_is_not_false_and_leaves_is_defined_tests_alone()
    {
        string template = "{%- if enable_thinking is defined and enable_thinking is not false %}A{% endif %}"
            + "{%- if enable_thinking is false %}B{% endif %}";
        string bound = ChatTemplateThinking.Bind(template, enableThinking: true);
        Assert.Equal(
            "{% set enable_thinking = true %}\n"
            + "{%- if enable_thinking is defined and enable_thinking != false %}A{% endif %}"
            + "{%- if enable_thinking == false %}B{% endif %}",
            bound);
        Assert.Contains("enable_thinking is defined", bound, StringComparison.Ordinal);
        Assert.StartsWith("{% set enable_thinking = false %}\n",
            ChatTemplateThinking.Bind(template, enableThinking: false), StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static RuntimeEngineRaceEvidenceRecord Winner(
        ManagedModelVariantIdentity baseline, ManagedModelVariantIdentity candidate, string version, bool promote)
    {
        var request = new RuntimeEngineRaceRequest(
            LocalInferenceEngineRegistry.LlamaCppCudaId, baseline,
            LocalInferenceEngineRegistry.WindowsMlGenAiId, candidate, "coding",
            SameWeightsAndQualityComparable: true, BaselineQualityScore: 95, CandidateQualityScore: 95,
            SustainedValidation: true, BaselineEngineVersion: "llama-build", CandidateEngineVersion: version);
        return new RuntimeEngineRaceEvidenceRecord
        {
            HardwareKey = "gpu",
            Request = request,
            Result = new RuntimeEngineRaceResult
            {
                Status = "completed",
                Promotion = new RuntimePromotionDecision(promote, 1, 1.2, promote ? 20 : -5, promote ? "winner" : "rejected")
            }
        };
    }
}
