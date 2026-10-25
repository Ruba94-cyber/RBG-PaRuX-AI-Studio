using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsMlGenAiProtocolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-winml-genai-" + Guid.NewGuid().ToString("N"));

    public WindowsMlGenAiProtocolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Serving_layout_requires_graph_genai_config_and_chat_template()
    {
        File.WriteAllBytes(Path.Combine(_root, "model.onnx"), new byte[] { 1 });
        Assert.False(WindowsMlGenAiProtocol.HasServingLayout(_root, out string missingConfig));
        Assert.Contains("genai_config", missingConfig, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(_root, "genai_config.json"), "{}");
        Assert.False(WindowsMlGenAiProtocol.HasServingLayout(_root, out string missingTemplate));
        Assert.Contains("chat_template", missingTemplate, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(_root, "chat_template.jinja"), "{{ messages }}");
        Assert.True(WindowsMlGenAiProtocol.HasServingLayout(_root, out _));
    }

    [Theory]
    [InlineData("NvTensorRtRtxExecutionProvider", "NvTensorRtRtx")]
    [InlineData("VitisAIExecutionProvider", "VitisAI")]
    [InlineData("OpenVINOExecutionProvider", "OpenVINO")]
    [InlineData("QNNExecutionProvider", "QNN")]
    public void Windows_ml_provider_names_map_to_documented_genai_aliases(string windowsMl, string genAi)
    {
        Assert.Equal(genAi, WindowsMlGenAiProtocol.ToGenAiProviderName(windowsMl));
        Assert.True(WindowsMlGenAiProtocol.IsSupportedGenAiProvider(windowsMl));
    }

    [Fact]
    public void Provider_not_supported_by_genai_never_receives_promotable_identity()
    {
        Assert.False(WindowsMlGenAiProtocol.IsSupportedGenAiProvider("MIGraphXExecutionProvider"));
        Assert.Throws<NotSupportedException>(() => WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
        {
            Name = "MIGraphXExecutionProvider",
            LibraryFileVersion = "1",
            LibrarySha256 = new string('c', 64)
        }));
    }

    [Fact]
    public void Runtime_identity_round_trips_exact_provider_and_fingerprint()
    {
        string sha = new string('a', 64);
        string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
        {
            Name = "VitisAIExecutionProvider",
            ReadyState = "Ready",
            LibraryFileVersion = "1.8.68.0",
            LibrarySha256 = sha
        });

        Assert.True(WindowsMlGenAiProtocol.TryParseRuntimeIdentity(identity, out string provider, out string version, out string parsedSha));
        Assert.Equal("VitisAIExecutionProvider", provider);
        Assert.Equal("1.8.68.0", version);
        Assert.Equal(sha, parsedSha);
    }

    [Fact]
    public void Runtime_identity_changes_when_provider_binary_changes()
    {
        string first = WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
        {
            Name = "NvTensorRtRtxExecutionProvider",
            LibraryFileVersion = "1.0",
            LibrarySha256 = new string('1', 64)
        });
        string second = WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
        {
            Name = "NvTensorRtRtxExecutionProvider",
            LibraryFileVersion = "1.0",
            LibrarySha256 = new string('2', 64)
        });
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Invalid_provider_names_and_old_or_unknown_protocol_versions_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateProviderName("../provider"));
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":999,\"type\":\"ready\"}"));
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":3,\"type\":\"ready\"}"));
    }

    [Fact]
    public void Delta_and_prompt_count_require_valid_request_events()
    {
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"id\":\"x\",\"type\":\"delta\"}"));
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"done\"}"));
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"cancelled\"}"));
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"id\":\"x\",\"type\":\"prompt_count\",\"promptTokens\":0}"));

        WindowsMlGenAiWorkerEvent evt = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"id\":\"x\",\"type\":\"delta\",\"content\":\"ok\",\"promptTokens\":8,\"generatedTokens\":1}");
        Assert.Equal("ok", evt.Content);
        Assert.Equal(8, evt.PromptTokens);

        WindowsMlGenAiWorkerEvent counted = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"id\":\"count-1\",\"type\":\"prompt_count\",\"promptTokens\":42}");
        Assert.Equal("prompt_count", counted.Type);
        Assert.Equal(42, counted.PromptTokens);
    }

    [Fact]
    public void Cancelled_is_a_valid_request_scoped_terminal()
    {
        WindowsMlGenAiWorkerEvent evt = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"id\":\"request-1\",\"type\":\"cancelled\",\"promptTokens\":9,\"generatedTokens\":2}");

        Assert.Equal("cancelled", evt.Type);
        Assert.Equal("request-1", evt.Id);
        Assert.Equal(9, evt.PromptTokens);
        Assert.Equal(2, evt.GeneratedTokens);
    }

    [Fact]
    public void Ready_handshake_requires_verified_thinking_cancellation_prompt_counting_and_one_device_for_promotion()
    {
        WindowsMlGenAiWorkerEvent ready = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"ready\",\"provider\":\"VitisAIExecutionProvider\","
            + "\"thinkingControl\":\"jinja-enable-thinking\","
            + "\"cancellationControl\":\"request-id-cancel-drain\","
            + "\"promptCountingControl\":\"exact-rendered-prompt-count\","
            + "\"deviceEvidence\":\"single-compatible-ep-device\","
            + "\"compatibleDevices\":[\"VitisAI|NPU|AMD|1022|1502\"]}");

        string device = WindowsMlGenAiProtocol.ValidateReadyHandshake(
            ready, "VitisAIExecutionProvider", requireUnambiguousDevice: true,
            requireThinkingControl: true, requireCancellationControl: true,
            requirePromptCountingControl: true);

        Assert.Equal("VitisAI|NPU|AMD|1022|1502", device);
    }

    [Fact]
    public void Ready_handshake_fails_closed_when_required_controls_or_device_identity_are_missing()
    {
        WindowsMlGenAiWorkerEvent ambiguous = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"ready\",\"provider\":\"OpenVINOExecutionProvider\","
            + "\"thinkingControl\":\"jinja-enable-thinking\","
            + "\"cancellationControl\":\"request-id-cancel-drain\","
            + "\"promptCountingControl\":\"exact-rendered-prompt-count\","
            + "\"deviceEvidence\":\"multiple-compatible-ep-devices\","
            + "\"compatibleDevices\":[\"OpenVINO|CPU|Intel|8086|0001\",\"OpenVINO|GPU|Intel|8086|0002\"]}");
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            ambiguous, "OpenVINOExecutionProvider", requireUnambiguousDevice: true));

        WindowsMlGenAiWorkerEvent noThinking = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"ready\",\"provider\":\"QNNExecutionProvider\","
            + "\"thinkingControl\":\"not-exposed-by-template\","
            + "\"cancellationControl\":\"request-id-cancel-drain\","
            + "\"promptCountingControl\":\"exact-rendered-prompt-count\","
            + "\"deviceEvidence\":\"single-compatible-ep-device\","
            + "\"compatibleDevices\":[\"QNN|NPU|Qualcomm|17cb|0001\"]}");
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            noThinking, "QNNExecutionProvider"));

        WindowsMlGenAiWorkerEvent noCancellation = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"ready\",\"provider\":\"QNNExecutionProvider\","
            + "\"thinkingControl\":\"jinja-enable-thinking\","
            + "\"promptCountingControl\":\"exact-rendered-prompt-count\","
            + "\"deviceEvidence\":\"single-compatible-ep-device\","
            + "\"compatibleDevices\":[\"QNN|NPU|Qualcomm|17cb|0001\"]}");
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            noCancellation, "QNNExecutionProvider"));

        WindowsMlGenAiWorkerEvent noPromptCounting = WindowsMlGenAiProtocol.ParseWorkerEvent(
            "{\"apiVersion\":4,\"type\":\"ready\",\"provider\":\"QNNExecutionProvider\","
            + "\"thinkingControl\":\"jinja-enable-thinking\","
            + "\"cancellationControl\":\"request-id-cancel-drain\","
            + "\"deviceEvidence\":\"single-compatible-ep-device\","
            + "\"compatibleDevices\":[\"QNN|NPU|Qualcomm|17cb|0001\"]}");
        Assert.Throws<InvalidDataException>(() => WindowsMlGenAiProtocol.ValidateReadyHandshake(
            noPromptCounting, "QNNExecutionProvider"));
    }
}