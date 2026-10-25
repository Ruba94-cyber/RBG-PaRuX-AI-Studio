using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Exercises the public Ollama-compatible HTTP routes through the actual ASP.NET application and
/// the OpenAI-compatible provider seam. The provider response is deterministic; no model process
/// or network dependency is involved.
/// </summary>
public sealed class OllamaCompatibilityIntegrationTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public OllamaCompatibilityIntegrationTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Ps_endpoint_returns_ok_and_models_array_over_http()
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.GetAsync("/api/ps");
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument output = JsonDocument.Parse(payload);
        Assert.True(output.RootElement.TryGetProperty("models", out JsonElement models));
        Assert.Equal(JsonValueKind.Array, models.ValueKind);
    }

    [Fact]
    public async Task Show_endpoint_includes_required_sdk_metadata_without_inventing_alias_parameters()
    {
        using HttpClient client = CreateAuthenticatedClient(_factory);
        using HttpResponseMessage response = await client.PostAsync("/api/show", JsonBody("{\"model\":\"parux-main\"}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement info = payload.RootElement.GetProperty("model_info");
        Assert.Equal(JsonValueKind.Object, info.ValueKind);
        Assert.Empty(info.EnumerateObject());
        Assert.Equal("parux", payload.RootElement.GetProperty("details").GetProperty("family").GetString());
        Assert.True(payload.RootElement.TryGetProperty("capabilities", out JsonElement capabilities));
        Assert.Equal(JsonValueKind.Array, capabilities.ValueKind);
        Assert.Contains(capabilities.EnumerateArray(), c => c.GetString() == "completion");
    }

    [Fact]
    public async Task Embeddings_endpoint_rejects_missing_prompt_over_http()
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/embeddings", JsonBody("{}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("prompt is required", payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A real client (or a fuzzer) can send any JSON value. Non-object bodies and wrong-typed
    /// fields used to surface as an empty HTTP 500; every Ollama route must reject them as an
    /// explicit 400 before any model work.</summary>
    [Theory]
    [InlineData("/api/chat", "[]")]
    [InlineData("/api/chat", "null")]
    [InlineData("/api/chat", "7")]
    [InlineData("/api/chat", "{\"model\": ")]
    [InlineData("/api/generate", "[]")]
    [InlineData("/api/generate", "\"text\"")]
    [InlineData("/api/show", "[]")]
    [InlineData("/api/show", "{\"model\": ")]
    [InlineData("/api/embed", "[]")]
    [InlineData("/api/embed", "{\"model\":5,\"input\":\"x\"}")]
    [InlineData("/api/embed", "{\"model\":\"\",\"input\":\"x\"}")]
    [InlineData("/api/embeddings", "[]")]
    [InlineData("/api/embeddings", "null")]
    public async Task Malformed_request_bodies_are_rejected_with_400_never_500(string route, string body)
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync(route, JsonBody(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(provider.LastRequestBody);
    }

    [Fact]
    public async Task Text_only_provider_image_rejection_is_reported_as_explicit_400_for_buffered_and_streamed_chat()
    {
        const string providerError = "{\"error\":{\"code\":500,\"message\":\"image input is not supported - hint: if this is unexpected, you may need to provide the mmproj\",\"type\":\"server_error\"}}";
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1EAAAAASUVORK5CYII=";
        foreach (bool stream in new[] { false, true })
        {
            using var provider = new RecordingProviderHandler(providerError, "application/json", HttpStatusCode.InternalServerError);
            using var providerHttp = new HttpClient(provider);
            using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
            using HttpClient client = CreateAuthenticatedClient(appFactory);

            using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody(
                "{\"model\":\"parux-main\",\"stream\":" + (stream ? "true" : "false")
                + ",\"messages\":[{\"role\":\"user\",\"content\":\"describe\",\"images\":[\"" + png + "\"]}]}"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("image input is not supported", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(500, "image input is not supported", 400)]
    [InlineData(500, "IMAGE INPUT IS NOT SUPPORTED - hint", 400)]
    [InlineData(500, "out of memory", 500)]
    [InlineData(503, "image input is not supported", 503)]
    [InlineData(400, "bad", 400)]
    public void ClientFacingStatus_only_reclassifies_the_provider_vision_capability_error(int status, string body, int expected) =>
        Assert.Equal(expected, OllamaCompatibilityEndpoints.ClientFacingStatus(status, body));

    [Fact]
    public async Task Embed_endpoint_rejects_missing_input_over_http()
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/embed", JsonBody("{}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("input is required", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Responses_compatibility_route_normalizes_and_returns_chat_completion_over_http()
    {
        const string providerResponse = """
        {"id":"chatcmpl-responses","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"local answer"}}],"usage":{"prompt_tokens":4,"completion_tokens":2}}
        """;
        using var provider = new RecordingProviderHandler(providerResponse, "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/v1/responses",
            JsonBody("{\"model\":\"parux-main\",\"input\":\"hello\",\"max_output_tokens\":24}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument output = JsonDocument.Parse(payload);
        Assert.Equal("completed", output.RootElement.GetProperty("status").GetString());
        Assert.Equal("local answer", output.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        Assert.Equal("hello", forwarded.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(24, forwarded.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Anthropic_compatibility_route_normalizes_and_returns_message_over_http()
    {
        const string providerResponse = """
        {"id":"chatcmpl-anthropic","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"local answer"}}],"usage":{"prompt_tokens":4,"completion_tokens":2}}
        """;
        using var provider = new RecordingProviderHandler(providerResponse, "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/v1/messages",
            JsonBody("{\"model\":\"parux-main\",\"max_tokens\":24,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument output = JsonDocument.Parse(payload);
        Assert.Equal("message", output.RootElement.GetProperty("type").GetString());
        Assert.Equal("end_turn", output.RootElement.GetProperty("stop_reason").GetString());
        Assert.Equal("local answer", output.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        Assert.Equal("hello", forwarded.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(24, forwarded.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("/v1/responses", "{\"input\":\"hello\",\"future_field\":true}")]
    [InlineData("/v1/messages", "{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"future_field\":true}")]
    public async Task Compatibility_routes_reject_unmapped_fields_before_calling_provider(string path, string body)
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync(path, JsonBody(body));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("future_field", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider.LastRequestBody);
    }

    [Fact]
    public async Task Buffered_chat_translates_vision_schema_tools_and_returns_tool_usage_over_http()
    {
        const string providerResponse = """
        {
          "id":"chatcmpl-ollama-buffered",
          "choices":[{
            "finish_reason":"tool_calls",
            "message":{"role":"assistant","content":"","tool_calls":[
              {"id":"call_weather","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Turin\"}"}}
            ]}
          }],
          "usage":{"prompt_tokens":13,"completion_tokens":5}
        }
        """;
        using var provider = new RecordingProviderHandler(providerResponse, "application/json");
        using var providerHttp = new HttpClient(provider);
        var router = new FakeRoleRouter { Succeed = true };
        using var appFactory = CreateConfiguredFactory(router, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);
        const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl8mWQAAAAASUVORK5CYII=";
        const string requestBody = """
        {
          "model":"parux-coding",
          "stream":false,
          "think":true,
          "logprobs":true,
          "top_logprobs":2,
          "options":{"temperature":0.2,"top_p":0.7,"seed":19,"stop":["END"],"num_predict":80},
          "messages":[{"role":"user","content":"Inspect this and check weather","images":["IMAGE_PLACEHOLDER"]}],
          "tools":[{"type":"function","function":{"name":"weather","description":"Look up weather","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]
        }
        """;
        string body = requestBody.Replace("IMAGE_PLACEHOLDER", image, StringComparison.Ordinal);

        using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody(body));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument actual = JsonDocument.Parse(payload);
        JsonElement root = actual.RootElement;
        Assert.Equal("parux-coding", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.Equal("tool_calls", root.GetProperty("done_reason").GetString());
        Assert.Equal(13, root.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(5, root.GetProperty("eval_count").GetInt32());
        JsonElement returnedCall = root.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("weather", returnedCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Turin", returnedCall.GetProperty("function").GetProperty("arguments").GetProperty("city").GetString());

        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        JsonElement upstream = forwarded.RootElement;
        Assert.Equal("local-default", upstream.GetProperty("model").GetString());
        Assert.Equal(0.2, upstream.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.7, upstream.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(19, upstream.GetProperty("seed").GetInt32());
        Assert.Equal(80, upstream.GetProperty("max_tokens").GetInt32());
        Assert.True(upstream.GetProperty("logprobs").GetBoolean());
        Assert.Equal(2, upstream.GetProperty("top_logprobs").GetInt32());
        Assert.True(upstream.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("weather", upstream.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        JsonElement parts = upstream.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("Inspect this and check weather", parts[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64," + image,
            parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("coding", Assert.Single(router.Calls).Request.Role);
    }

    [Fact]
    public async Task Streaming_chat_returns_valid_ndjson_with_thinking_finish_reason_and_real_usage()
    {
        const string firstEvent = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n";
        const string remainingEvents =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":2}}\n\n" +
            "data: [DONE]\n\n";
        using var provider = new GatedSseProviderHandler(firstEvent, remainingEvents);
        using var providerHttp = new HttpClient(provider);
        var router = new FakeRoleRouter { Succeed = true };
        using var appFactory = CreateConfiguredFactory(router, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);
        const string requestBody = """
        {"model":"parux-reasoning","messages":[{"role":"user","content":"solve"}],"stream":true,"think":true}
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonBody(requestBody) };
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        await using Stream resultStream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(resultStream, Encoding.UTF8);
        var lines = new List<string>();
        string firstFrame = Assert.IsType<string>(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        lines.Add(firstFrame);
        using (JsonDocument first = JsonDocument.Parse(firstFrame))
            Assert.Equal("answer", first.RootElement.GetProperty("message").GetProperty("content").GetString());

        // The provider intentionally cannot finish until the client has observed its first frame.
        // This fails if the adapter buffers the upstream completion before writing HTTP output.
        await provider.UpstreamReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.ReleaseRemainingEvents();
        while (await reader.ReadLineAsync() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);

        Assert.Equal(3, lines.Count);
        JsonDocument[] parsedFrames = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.Contains(parsedFrames, frame => frame.RootElement.TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("thinking", out JsonElement thinking) && thinking.GetString() == "plan");
        foreach (JsonDocument frame in parsedFrames)
            Assert.True(frame.RootElement.TryGetProperty("done", out _), "Every streamed Ollama frame must be valid JSON with a done flag.");

        JsonElement terminal = parsedFrames[^1].RootElement;
        Assert.True(terminal.GetProperty("done").GetBoolean());
        Assert.Equal("stop", terminal.GetProperty("done_reason").GetString());
        Assert.Equal(9, terminal.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(2, terminal.GetProperty("eval_count").GetInt32());
        Assert.False(terminal.TryGetProperty("total_duration", out _));
        foreach (JsonDocument frame in parsedFrames) frame.Dispose();
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        Assert.True(forwarded.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Json_schema_chat_roundtrips_the_exact_schema_and_structured_answer_over_http()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        const string providerResponse = """
        {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"answer\":\"forty-two\"}"}}]}
        """;
        using var provider = new RecordingProviderHandler(providerResponse, "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);
        string requestBody = "{\"model\":\"parux-main\",\"stream\":false,\"format\":" + schema
            + ",\"messages\":[{\"role\":\"user\",\"content\":\"Answer as JSON\"}]}";

        using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody(requestBody));
        using JsonDocument actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string ollamaContent = actual.RootElement.GetProperty("message").GetProperty("content").GetString()!;
        using JsonDocument returnedStructuredAnswer = JsonDocument.Parse(ollamaContent);
        Assert.Equal("forty-two", returnedStructuredAnswer.RootElement.GetProperty("answer").GetString());
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        JsonElement schemaFormat = forwarded.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", schemaFormat.GetProperty("type").GetString());
        JsonElement forwardedSchema = schemaFormat.GetProperty("json_schema").GetProperty("schema");
        Assert.Equal("object", forwardedSchema.GetProperty("type").GetString());
        Assert.Equal("string", forwardedSchema.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
        Assert.Equal("answer", forwardedSchema.GetProperty("required")[0].GetString());
        Assert.False(forwardedSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Buffered_generate_translates_system_and_prompt_and_returns_generate_shape()
    {
        const string providerResponse = """
        {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Hello from the local model."}}],"usage":{"prompt_tokens":6,"completion_tokens":5}}
        """;
        using var provider = new RecordingProviderHandler(providerResponse, "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/generate", JsonBody(
            "{\"model\":\"parux-main\",\"system\":\"Be concise\",\"prompt\":\"Say hello\",\"stream\":false}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument actual = JsonDocument.Parse(payload);
        Assert.Equal("Hello from the local model.", actual.RootElement.GetProperty("response").GetString());
        Assert.Equal("stop", actual.RootElement.GetProperty("done_reason").GetString());
        Assert.Equal(6, actual.RootElement.GetProperty("prompt_eval_count").GetInt32());
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        Assert.Equal("system", forwarded.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Be concise", forwarded.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Say hello", forwarded.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Client_cancellation_is_forwarded_to_the_streaming_provider_request()
    {
        using var provider = new CancellationObservingProviderHandler();
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonBody("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"wait\"}],\"stream\":true}")
        };
        using var cancellation = new CancellationTokenSource();

        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await provider.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"keep_alive\":\"5m\"}", "keep_alive")]
    [InlineData("{\"model\":\"parux-main\",\"stream\":\"false\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", "stream' must be a boolean")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"weather\"},\"vendor_extension\":true}]}", "tool field 'vendor_extension'")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\",\"vendor_extension\":true}]}", "message field 'vendor_extension'")]
    [InlineData("{not-json", "invalid request body")]
    public async Task Unsupported_or_malformed_requests_fail_explicitly_without_calling_provider(string body, string expected)
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody(body));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, payload, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider.LastRequestBody);
    }

    private const string ChatMsg = "\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]";

    [Theory]
    // /api/chat: every dropped-field candidate is rejected explicitly, never ignored.
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"options\":{\"num_ctx\":4096}}", "option 'num_ctx'")]
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"options\":{\"mirostat\":2}}", "option 'mirostat'")]
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"format\":\"yaml\"}", "format 'yaml'")]
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"think\":\"high\"}", "think level")]
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"keep_alive\":0}", "keep_alive")]
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"truncate\":false}", "request field 'truncate'")]
    [InlineData("/api/chat", "{\"model\":\"parux-main\"," + ChatMsg + ",\"template\":\"x\"}", "request field 'template'")]
    // /api/generate
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"suffix\":\"y\"}", "suffix")]
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"raw\":true}", "raw prompt mode")]
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"keep_alive\":\"10m\"}", "keep_alive")]
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"options\":{\"num_gpu\":1}}", "option 'num_gpu'")]
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"tools\":[]}", "request field 'tools'")]
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"context\":[1,2]}", "request field 'context'")]
    [InlineData("/api/generate", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"images\":\"notanarray\"}", "images")]
    // /api/embed and /api/embeddings
    [InlineData("/api/embed", "{\"model\":\"parux-embedding\",\"input\":\"x\",\"truncate\":false}", "truncate")]
    [InlineData("/api/embed", "{\"model\":\"parux-embedding\",\"input\":\"x\",\"keep_alive\":\"5m\"}", "keep_alive")]
    [InlineData("/api/embed", "{\"model\":\"parux-embedding\",\"input\":\"x\",\"options\":{\"num_ctx\":8}}", "options")]
    [InlineData("/api/embed", "{\"model\":\"parux-embedding\",\"input\":\"x\",\"dimensions\":64}", "dimensions")]
    [InlineData("/api/embed", "{\"model\":\"parux-embedding\",\"input\":\"x\",\"vendor\":1}", "request field 'vendor'")]
    [InlineData("/api/embeddings", "{\"model\":\"parux-embedding\",\"prompt\":\"x\",\"keep_alive\":\"5m\"}", "keep_alive")]
    [InlineData("/api/embeddings", "{\"model\":\"parux-embedding\",\"prompt\":\"x\",\"options\":{\"num_ctx\":8}}", "options")]
    public async Task Ollama_surfaces_reject_each_unhonoured_field_with_an_ollama_shaped_400(
        string route, string body, string expected)
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync(route, JsonBody(body));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument error = JsonDocument.Parse(payload);
        // Ollama error shape: {"error": "<string>"}.
        Assert.Equal(JsonValueKind.String, error.RootElement.GetProperty("error").ValueKind);
        Assert.Contains(expected, payload, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider.LastRequestBody);
    }

    [Theory]
    [InlineData("{\"model\":\"parux-main\"," + ChatMsg + ",\"options\":{\"temperature\":0.1,\"top_p\":0.5,\"seed\":7,\"num_predict\":33,\"stop\":[\"END\"]}}", "\"temperature\":0.1", "\"stop\":[\"END\"]")]
    [InlineData("{\"model\":\"parux-main\"," + ChatMsg + ",\"format\":\"json\"}", "response_format", "json_object")]
    [InlineData("{\"model\":\"parux-main\"," + ChatMsg + ",\"think\":false}", "enable_thinking", "false")]
    [InlineData("{\"model\":\"parux-main\"," + ChatMsg + ",\"keep_alive\":null,\"options\":null}", "\"messages\"", "\"messages\"")]
    public async Task Ollama_chat_forwards_each_supported_field_to_the_provider(
        string body, string expectedA, string expectedB)
    {
        using var provider = new RecordingProviderHandler(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}",
            "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody(Fixup(body)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedA, provider.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains(expectedB, provider.LastRequestBody!, StringComparison.Ordinal);
    }

    private static string Fixup(string body)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node["stream"] = false;
        return node.ToJsonString();
    }

    [Fact]
    public async Task Generate_route_translates_prompt_and_streaming_error_is_not_hidden()
    {
        using var provider = new RecordingProviderHandler(
            "{\"error\":{\"message\":\"provider rejected request\"}}", "application/json", HttpStatusCode.ServiceUnavailable);
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);
        const string body = """
        {"model":"parux-main","prompt":"write a greeting","stream":true}
        """;

        using HttpResponseMessage response = await client.PostAsync("/api/generate", JsonBody(body));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("provider rejected request", payload, StringComparison.OrdinalIgnoreCase);
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        Assert.Equal("write a greeting", forwarded.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.True(forwarded.RootElement.GetProperty("stream").GetBoolean());
    }

    private const string ProviderToolStream =
        "data: {\"choices\":[{\"delta\":{\"content\":\"Reading \"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"it.\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"Read\",\"arguments\":\"{\\\"path\\\":\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":9,\"total_tokens\":49}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Anthropic_streaming_is_encoded_live_from_the_provider_stream_over_http()
    {
        using var provider = new RecordingProviderHandler(ProviderToolStream, "text/event-stream");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/v1/messages", JsonBody(
            "{\"model\":\"parux-code\",\"max_tokens\":4000,\"stream\":true,\"thinking\":{\"type\":\"adaptive\"}," +
            "\"tools\":[{\"name\":\"Read\",\"input_schema\":{\"type\":\"object\"}}],\"messages\":[{\"role\":\"user\",\"content\":\"read a.txt\"}]}"));
        string sse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("\"text\":\"Reading \"", sse);                                               // provider deltas, not one buffered block
        Assert.Contains("\"text\":\"it.\"", sse);
        Assert.Contains("\"partial_json\":\"{\\u0022path\\u0022:\\u0022a.txt\\u0022}\"", sse);
        Assert.Contains("\"stop_reason\":\"tool_use\"", sse);
        Assert.Contains("\"input_tokens\":40", sse);
        Assert.EndsWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", sse);
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(provider.LastRequestBody));
        Assert.True(forwarded.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(forwarded.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(forwarded.RootElement.TryGetProperty("chat_template_kwargs", out _));             // thinking requested: kept
    }

    [Fact]
    public async Task Responses_streaming_is_encoded_live_and_continues_with_previous_response_id()
    {
        using var provider = new RecordingProviderHandler(ProviderToolStream, "text/event-stream");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/v1/responses", JsonBody(
            "{\"model\":\"parux-code\",\"stream\":true,\"tools\":[{\"type\":\"function\",\"name\":\"Read\",\"parameters\":{\"type\":\"object\"}}],\"input\":\"read a.txt\"}"));
        string sse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sse, "event: response.output_text.delta").Count);
        Assert.Contains("\"call_id\":\"call_1\"", sse);
        Assert.DoesNotContain("[DONE]", sse);
        string completedLine = sse.Split('\n').Last(line => line.StartsWith("data: {\"type\":\"response.completed\"", StringComparison.Ordinal));
        using JsonDocument completed = JsonDocument.Parse(completedLine[6..]);
        JsonElement final = completed.RootElement.GetProperty("response");
        Assert.Equal(49, final.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        string id = final.GetProperty("id").GetString()!;

        // The streamed turn (text + tool call) is the continuation state for the next request.
        using var next = new RecordingProviderHandler(
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"done\"}}]}", "application/json");
        using var nextHttp = new HttpClient(next);
        using var nextFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, nextHttp);
        using HttpClient nextClient = CreateAuthenticatedClient(nextFactory);
        using HttpResponseMessage followUp = await nextClient.PostAsync("/v1/responses", JsonBody(
            "{\"model\":\"parux-code\",\"previous_response_id\":\"" + id + "\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"hello\"}]}"));
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
        using JsonDocument forwarded = JsonDocument.Parse(Assert.IsType<string>(next.LastRequestBody));
        JsonElement messages = forwarded.RootElement.GetProperty("messages");
        Assert.Contains(messages.EnumerateArray(), m => m.TryGetProperty("tool_calls", out JsonElement calls)
            && calls[0].GetProperty("id").GetString() == "call_1");
    }

    [Fact]
    public async Task Count_tokens_returns_a_labelled_count_over_http()
    {
        using var provider = new RecordingProviderHandler("{}", "application/json");
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/v1/messages/count_tokens", JsonBody(
            "{\"model\":\"claude-sonnet-4-5\",\"messages\":[{\"role\":\"user\",\"content\":\"hello there, how are you today?\"}]}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonDocument.Parse(payload).RootElement.GetProperty("input_tokens").GetInt32() > 0);
        Assert.Contains(response.Headers.GetValues("X-PaRuX-Token-Count").Single(), new[] { "exact", "estimated" });
    }

    [Fact]
    public async Task Upstream_timeout_reports_elapsed_time_and_resolved_model()
    {
        using var provider = new TimingOutProviderHandler();
        using var providerHttp = new HttpClient(provider) { Timeout = TimeSpan.FromMilliseconds(50) };
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"stream\":false}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("timed out", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parux-main", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mid_stream_backend_failure_emits_typed_ndjson_error_frame()
    {
        var stream = new PartialThenFailingStream();
        using var provider = new StreamProviderHandler(stream);
        using var providerHttp = new HttpClient(provider);
        using var appFactory = CreateConfiguredFactory(new FakeRoleRouter { Succeed = true }, providerHttp);
        using HttpClient client = CreateAuthenticatedClient(appFactory);

        using HttpResponseMessage response = await client.PostAsync("/api/chat", JsonBody("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"stream\":true}"));
        string payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"done\":true", payload.Replace(" ", ""));
        Assert.Contains("\"done_reason\":\"error\"", payload.Replace(" ", ""));
    }

    private WebApplicationFactory<global::Program> CreateConfiguredFactory(FakeRoleRouter router, HttpClient providerHttp) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRoleRouter>();
            services.AddSingleton<IRoleRouter>(router);
            services.AddSingleton(providerHttp);
            services.RemoveAll<LocalApiServerService>();
            services.AddSingleton(services => new LocalApiServerService(
                services.GetRequiredService<LocalRuntimeService>(), router, providerHttp,
                ownsRuntime: () => true));
        }));

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<global::Program> factory)
    {
        HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class RecordingProviderHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private readonly string _contentType;
        private readonly HttpStatusCode _statusCode;

        public RecordingProviderHandler(string payload, string contentType, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _payload = payload;
            _contentType = contentType;
            _statusCode = statusCode;
        }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_payload, Encoding.UTF8, _contentType)
            };
        }
    }

    private sealed class CancellationObservingProviderHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The test provider should remain pending until cancellation.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class GatedSseProviderHandler : HttpMessageHandler
    {
        private readonly GatedSseStream _stream;

        public GatedSseProviderHandler(string firstEvent, string remainingEvents) =>
            _stream = new GatedSseStream(firstEvent, remainingEvents);

        public TaskCompletionSource UpstreamReadBlocked => _stream.UpstreamReadBlocked;
        public string? LastRequestBody { get; private set; }
        public void ReleaseRemainingEvents() => _stream.ReleaseRemainingEvents();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var content = new StreamContent(_stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class GatedSseStream : Stream
    {
        private readonly byte[] _firstEvent;
        private readonly byte[] _remainingEvents;
        private int _phase;

        public GatedSseStream(string firstEvent, string remainingEvents)
        {
            _firstEvent = Encoding.UTF8.GetBytes(firstEvent);
            _remainingEvents = Encoding.UTF8.GetBytes(remainingEvents);
        }

        public TaskCompletionSource UpstreamReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRemainingEvents() => Release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_phase == 0)
            {
                _phase = 1;
                return Copy(_firstEvent, buffer);
            }
            if (_phase == 1)
            {
                _phase = 2;
                UpstreamReadBlocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (_phase == 2)
            {
                _phase = 3;
                return Copy(_remainingEvents, buffer);
            }
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private static int Copy(byte[] source, Memory<byte> destination)
        {
            int count = Math.Min(source.Length, destination.Length);
            source.AsMemory(0, count).CopyTo(destination);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous SSE reads.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TimingOutProviderHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StreamProviderHandler : HttpMessageHandler
    {
        private readonly Stream _stream;
        public StreamProviderHandler(Stream stream) => _stream = stream;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(_stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class PartialThenFailingStream : Stream
    {
        private readonly byte[] _initialChunk = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial answer\"}}]}\n\n");
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                int count = Math.Min(_initialChunk.Length, buffer.Length);
                _initialChunk.AsMemory(0, count).CopyTo(buffer);
                return count;
            }
            await Task.Yield();
            throw new IOException("Simulated mid-stream backend termination");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
