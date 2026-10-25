using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Integration of ModalityRouter into the live chat path (ChatProxyService.ProxyAsync) with a fake machine.</summary>
public sealed class ModalityChatRoutingTests
{
    private sealed class FakeBackend : IModalityChatBackend
    {
        public ModalityRouteFacts Facts = new(false, true, true, true, false, Array.Empty<ToolModelFact>(),
            new InstalledRuntime("b20000", ComponentOwnership.PARUX_MANAGED));
        public List<string> Calls { get; } = new();
        public Task<ModalityRouteFacts> GetFactsAsync(string role, CancellationToken ct) { Calls.Add("facts:" + role); return Task.FromResult(Facts); }
        public Task<TranscribeResponse> TranscribeAsync(TranscribeRequest r, CancellationToken ct)
        {
            Calls.Add("stt");
            return Task.FromResult(new TranscribeResponse { Text = "hello from audio" });
        }
        public Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest r, CancellationToken ct)
        {
            Calls.Add("tts:" + r.Text);
            return Task.FromResult(new SynthesizeResponse { Base64Data = "AAAA", DurationSeconds = 1.5 });
        }
        public RouteRequest CreateToolModelPin(string modelId) => new() { Role = "standard", Mode = RouteMode.Manual, ExactCandidateFileName = modelId };
    }

    private static ChatCompletionRequest Req(string text, bool audio = false, bool consent = false)
    {
        var m = new ChatCompletionMessage { Role = "user", Content = text };
        if (audio) m.Audio.Add(new ChatAudioContent { Base64Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }) });
        return new ChatCompletionRequest { Messages = new() { m }, ComputerUseConsent = consent };
    }

    private static (DefaultHttpContext Ctx, MemoryStream Body) Http()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static string Text(MemoryStream body) => System.Text.Encoding.UTF8.GetString(body.ToArray());

    [Fact]
    public async Task Audio_attachment_reaches_the_speech_engine_and_the_routed_event_carries_the_explanation()
    {
        var backend = new FakeBackend();
        var (ctx, body) = Http();
        ModalityChatResult result = await ModalityChatGate.TryHandleAsync(Req("transcribe this", audio: true), backend, ctx.Response, default);

        Assert.True(result.Handled);
        Assert.Equal(new[] { "facts:audio_transcribe", "stt" }, backend.Calls);
        string sse = Text(body);
        Assert.Contains("\"parux_event\":\"model_routed\"", sse);
        Assert.Contains("speech_stt", sse);
        Assert.Contains("Speech-to-text runs on the local PaRuX speech engine", sse); // explanation shown to the client
        Assert.Contains("hello from audio", sse);
    }

    [Fact]
    public async Task Audio_with_a_question_is_transcribed_then_continues_on_the_text_path_with_the_transcript()
    {
        var backend = new FakeBackend();
        var (ctx, body) = Http();
        ChatCompletionRequest request = Req("what is the speaker's mood?", audio: true);
        ModalityChatResult result = await ModalityChatGate.TryHandleAsync(request, backend, ctx.Response, default);

        Assert.False(result.Handled);
        Assert.NotNull(result.Route);
        Assert.Empty(body.ToArray());
        ChatCompletionMessage message = request.Messages[0];
        Assert.Contains("[Audio transcript]\nhello from audio", message.Content);
        Assert.Empty(message.Audio);

        JsonObject_Annotate(result.Route!, out string reason);
        Assert.Contains("transcript-first", reason);
    }

    private static void JsonObject_Annotate(ModalityRouteOutcome route, out string reason)
    {
        var routed = new System.Text.Json.Nodes.JsonObject();
        ModalityChatGate.Annotate(routed, route);
        reason = routed["modalityReason"]!.GetValue<string>();
    }

    [Fact]
    public async Task Speech_request_is_synthesised_by_the_tts_engine_not_a_text_model()
    {
        var backend = new FakeBackend();
        var (ctx, body) = Http();
        ChatCompletionRequest request = Req("leggi ad alta voce");
        request.Messages.Insert(0, new ChatCompletionMessage { Role = "assistant", Content = "Ciao a tutti" });
        ModalityChatResult result = await ModalityChatGate.TryHandleAsync(request, backend, ctx.Response, default);

        Assert.True(result.Handled);
        Assert.Contains("tts:Ciao a tutti", backend.Calls);
        string sse = Text(body);
        Assert.Contains("speech_audio", sse);
        Assert.Contains("speech_tts", sse);
    }

    [Fact]
    public async Task Missing_speech_engine_is_an_honest_typed_503_and_nothing_is_substituted()
    {
        var backend = new FakeBackend { Facts = new FakeBackend().Facts with { SttInstalled = false } };
        var (ctx, body) = Http();
        ModalityChatResult result = await ModalityChatGate.TryHandleAsync(Req("", audio: true), backend, ctx.Response, default);

        Assert.True(result.Handled);
        Assert.Equal(503, ctx.Response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(Text(body));
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("no_eligible_engine", error.GetProperty("code").GetString());
        Assert.Equal("audio.transcribe", error.GetProperty("capability").GetString());
        Assert.DoesNotContain("stt", backend.Calls);
    }

    [Fact]
    public async Task Computer_use_requires_explicit_consent_and_otherwise_hands_off_to_the_agent()
    {
        var backend = new FakeBackend();
        var (denied, deniedBody) = Http();
        ModalityChatResult refused = await ModalityChatGate.TryHandleAsync(Req("use my computer to open Notepad"), backend, denied.Response, default);
        Assert.True(refused.Handled);
        Assert.Equal(403, denied.Response.StatusCode);
        Assert.Contains("consent_required", Text(deniedBody));

        var (granted, grantedBody) = Http();
        ModalityChatResult ok = await ModalityChatGate.TryHandleAsync(
            Req("use my computer to open Notepad", consent: true), backend, granted.Response, default);
        Assert.True(ok.Handled);
        Assert.Equal(200, granted.Response.StatusCode);
        string sse = Text(grantedBody);
        Assert.Contains("computer_use_handoff", sse);
        Assert.Contains("computer_use_agent", sse);
    }

    [Fact]
    public async Task Gaming_mode_refuses_every_modality_turn_with_409()
    {
        var backend = new FakeBackend { Facts = new FakeBackend().Facts with { GamingActive = true } };
        foreach (ChatCompletionRequest request in new[] { Req("", audio: true), Req("read aloud"), Req("use my computer", consent: true) })
        {
            var (ctx, body) = Http();
            Assert.True((await ModalityChatGate.TryHandleAsync(request, backend, ctx.Response, default)).Handled);
            Assert.Equal(409, ctx.Response.StatusCode);
            Assert.Contains("gaming_hard_off", Text(body));
        }
    }

    [Fact]
    public async Task Tool_use_pins_the_verified_model_and_refuses_when_none_is_verified()
    {
        var backend = new FakeBackend
        {
            Facts = new FakeBackend().Facts with
            {
                TextModels = new[] { new ToolModelFact("plain.gguf", false, true, 99), new ToolModelFact("tools.gguf", true, true, 3) }
            }
        };
        var (ctx, body) = Http();
        ChatCompletionRequest request = Req("use the tools to check the weather");
        ModalityChatResult ok = await ModalityChatGate.TryHandleAsync(request, backend, ctx.Response, default);
        Assert.False(ok.Handled);
        Assert.Equal("tools.gguf", request.ForcedModelRoute!.ExactCandidateFileName);
        Assert.Equal("tools.gguf", ok.Route!.EngineId);
        Assert.Empty(body.ToArray());

        backend.Facts = backend.Facts with { TextModels = new[] { new ToolModelFact("plain.gguf", false, true, 99) } };
        var (ctx2, body2) = Http();
        ChatCompletionRequest second = Req("use the tools to check the weather");
        ModalityChatResult no = await ModalityChatGate.TryHandleAsync(second, backend, ctx2.Response, default);
        Assert.True(no.Handled);
        Assert.Equal(503, ctx2.Response.StatusCode);
        Assert.Null(second.ForcedModelRoute);
        Assert.Contains("verified tool calling", Text(body2));
    }

    [Fact]
    public async Task Ordinary_chat_manual_selection_and_continuations_are_never_rerouted()
    {
        var backend = new FakeBackend();
        var (ctx, body) = Http();
        Assert.False((await ModalityChatGate.TryHandleAsync(Req("tell me a joke"), backend, ctx.Response, default)).Handled);
        Assert.False((await ModalityChatGate.TryHandleAsync(Req("how do I transcribe a podcast?"), backend, ctx.Response, default)).Handled);

        ChatCompletionRequest manual = Req("", audio: true);
        manual.ForcedModelRoute = new RouteRequest { Role = "standard" };
        Assert.False((await ModalityChatGate.TryHandleAsync(manual, backend, ctx.Response, default)).Handled);
        ChatCompletionRequest cont = Req("read aloud");
        cont.ContinueFinalMessage = true;
        Assert.False((await ModalityChatGate.TryHandleAsync(cont, backend, ctx.Response, default)).Handled);
        Assert.False((await ModalityChatGate.TryHandleAsync(Req("read aloud"), null, ctx.Response, default)).Handled);
        Assert.Empty(backend.Calls);
        Assert.Empty(body.ToArray());
    }

    [Fact]
    public void Model_routed_event_for_a_continuing_turn_carries_the_modality_explanation()
    {
        var route = new ModalityRouteOutcome(true, "tool.use", "text_model_tool_calling", "tools.gguf", null, Array.Empty<string>(), "because verified");
        var routed = new System.Text.Json.Nodes.JsonObject { ["parux_event"] = "model_routed" };
        ModalityChatGate.Annotate(routed, route);
        Assert.Equal("because verified", routed["modalityReason"]!.GetValue<string>());
        Assert.Equal("tool.use", routed["modality"]!.GetValue<string>());
    }

    // ---- through the real ChatProxyService + HTTP pipeline

    [Fact]
    public async Task Real_chat_endpoint_routes_audio_to_the_speech_engine_before_any_text_runtime_starts()
    {
        var fake = new FakeBackend();
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IModalityChatBackend>();
            services.AddSingleton<IModalityChatBackend>(fake);
        }));
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/chat/completions",
            Req("trascrivi questo audio", audio: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("model_routed", sse);
        Assert.Contains("hello from audio", sse);
        Assert.Contains("stt", fake.Calls);
    }
}
