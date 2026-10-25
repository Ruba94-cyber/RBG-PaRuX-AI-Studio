using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-08 capability honesty: the ONNX engine advertises text only, rejects what it cannot do
/// explicitly instead of silently ignoring it, never treats provider discovery as NPU execution,
/// and binds the thinking preference in a form the ORT GenAI Jinja engine can actually evaluate.
/// </summary>
public sealed class WindowsMlCapabilityHonestyTests
{
    private const string Qwen3Template =
        "{%- if add_generation_prompt %}\n{{- '<|im_start|>assistant\\n' }}\n"
        + "{%- if enable_thinking is defined and enable_thinking is false %}\n{{- '<think>\\n\\n</think>\\n\\n' }}\n"
        + "{%- endif %}\n{%- endif %}";

    [Fact]
    public void Descriptor_advertises_text_only_and_disclaims_npu_proof()
    {
        LocalInferenceEngineDescriptor descriptor = new LocalInferenceEngineRegistry()
            .ListDescriptors().Single(item => item.Id == LocalInferenceEngineRegistry.WindowsMlGenAiId);

        Assert.Equal(LocalInferenceCapabilities.Text, descriptor.Capabilities);
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.Vision));
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.ToolCalling));
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.StructuredOutput));
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.ContinuousBatching));
    }

    [Theory]
    [InlineData(false, true, false, false, false)]   // vision
    [InlineData(false, false, true, false, false)]   // tool calling
    [InlineData(false, false, false, true, false)]   // structured output
    [InlineData(true, false, false, false, false)]   // provider fallback required
    [InlineData(false, false, false, false, true)]   // Gaming Mode
    public void Turns_needing_unverified_capabilities_never_route_to_the_onnx_engine(
        bool providerFallback, bool vision, bool tools, bool structured, bool gaming)
    {
        Assert.False(LocalInferenceEngineCoordinator.IsTurnEligibleForAlternative(
            manualModelLock: false,
            hasVisualInput: vision,
            requiresToolCalling: tools,
            requiresProviderFallback: providerFallback,
            requiresStructuredOutput: structured,
            gamingActive: gaming,
            selectedGgufPath: "C:/models/model.gguf"));
    }

    [Fact]
    public void Built_in_cpu_and_directml_providers_have_distinct_identities_and_unknown_providers_are_refused()
    {
        foreach (string name in new[] { "CPUExecutionProvider", "DmlExecutionProvider", "VitisAIExecutionProvider" })
        {
            Assert.True(WindowsMlGenAiProtocol.IsSupportedGenAiProvider(name), name);
            string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
            {
                Name = name, ReadyState = "Ready", LibraryFileVersion = "1.0", LibrarySha256 = new string('b', 64)
            });
            Assert.True(WindowsMlGenAiProtocol.TryParseRuntimeIdentity(identity, out string parsed, out _, out _));
            Assert.Equal(name, parsed);
        }
        Assert.False(WindowsMlGenAiProtocol.IsSupportedGenAiProvider("MadeUpNpuExecutionProvider"));
    }

    [Fact]
    public void Provider_readiness_alone_is_not_device_evidence_for_a_ready_handshake()
    {
        var ready = new WindowsMlGenAiWorkerEvent
        {
            Type = "ready",
            Provider = "VitisAIExecutionProvider",
            ThinkingControl = WindowsMlGenAiProtocol.ThinkingControlJinja,
            CancellationControl = WindowsMlGenAiProtocol.RequestCancellationControl,
            PromptCountingControl = WindowsMlGenAiProtocol.PromptCountingControl,
            DeviceEvidence = "provider-ready-no-ep-device-enumeration"
        };
        Assert.Throws<InvalidDataException>(() =>
            WindowsMlGenAiProtocol.ValidateReadyHandshake(ready, "VitisAIExecutionProvider"));
    }

    [Fact]
    public void Thinking_false_rewrites_boolean_is_tests_the_genai_jinja_engine_cannot_evaluate()
    {
        string bound = ChatTemplateThinking.Bind(Qwen3Template, enableThinking: false);
        Assert.StartsWith("{% set enable_thinking = false %}", bound, StringComparison.Ordinal);
        Assert.DoesNotContain("enable_thinking is false", bound, StringComparison.Ordinal);
        Assert.Contains("enable_thinking == false", bound, StringComparison.Ordinal);

        string notFalse = ChatTemplateThinking.Bind("{% if enable_thinking is not false %}x{% endif %}", true);
        Assert.Contains("enable_thinking != false", notFalse, StringComparison.Ordinal);
    }

    [Fact]
    public void Thinking_false_without_template_support_is_refused_not_silently_ignored()
    {
        Assert.Throws<NotSupportedException>(() => ChatTemplateThinking.Bind("{{ messages }}", enableThinking: false));
        Assert.Equal("{{ messages }}", ChatTemplateThinking.Bind("{{ messages }}", enableThinking: true));
        Assert.Throws<InvalidDataException>(() => ChatTemplateThinking.Bind(" ", true));
    }
}
