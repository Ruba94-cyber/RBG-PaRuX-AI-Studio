using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// API keys exist so another application can use this PaRuX as a model provider. The whole point of
/// the design is what a key does <em>not</em> buy: the control plane under /api/v1 can change the
/// LAN boundary, connect folders on this PC, drive Computer Use and read saved conversations, and a
/// key pasted into a third-party program must reach none of it.
/// </summary>
public sealed class ApiKeyServiceTests
{
    private static ApiKeyService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "parux-keys-" + Guid.NewGuid().ToString("N") + ".json");
        return new ApiKeyService(path);
    }

    [Fact]
    public void A_created_key_is_returned_once_and_then_only_ever_as_a_hash()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated created = keys.Create("Open WebUI");

            Assert.StartsWith(ApiKeyService.KeyPrefix, created.Key, StringComparison.Ordinal);
            Assert.True(created.Key.Length > 40, "A key must not be guessable.");

            string stored = File.ReadAllText(path);
            Assert.DoesNotContain(created.Key, stored, StringComparison.Ordinal);

            ApiKeyInfo listed = Assert.Single(keys.List());
            Assert.Equal("Open WebUI", listed.Name);
            Assert.Null(listed.LastUsedUtc);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_key_validates_only_itself()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated first = keys.Create("first");
            keys.Create("second");

            Assert.True(keys.TryValidate(first.Key, out string id));
            Assert.Equal(first.Id, id);
            Assert.False(keys.TryValidate(ApiKeyService.KeyPrefix + "not-a-real-key", out _));
            Assert.False(keys.TryValidate("sk-an-openai-looking-key", out _));
            Assert.False(keys.TryValidate(null, out _));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_revoked_key_stops_working_and_the_others_keep_working()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated doomed = keys.Create("laptop");
            ApiKeyCreated kept = keys.Create("script");

            Assert.True(keys.Revoke(doomed.Id));

            Assert.False(keys.TryValidate(doomed.Key, out _));
            Assert.True(keys.TryValidate(kept.Key, out _));
            Assert.False(keys.Revoke(doomed.Id));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Using_a_key_records_that_it_is_alive_so_a_user_can_tell_which_to_revoke()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated created = keys.Create("in use");
            Assert.Null(Assert.Single(keys.List()).LastUsedUtc);

            Assert.True(keys.TryValidate(created.Key, out _));

            Assert.NotNull(Assert.Single(keys.List()).LastUsedUtc);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Keys_survive_a_restart_because_a_provider_credential_that_did_not_would_be_useless()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated created = keys.Create("persistent");

            var reopened = new ApiKeyService(path);

            Assert.True(reopened.TryValidate(created.Key, out _));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_failed_revocation_write_keeps_the_key_issued_and_a_retry_removes_it()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated doomed = keys.Create("laptop");
            ApiKeyCreated kept = keys.Create("script");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Windows reports a refused replace as either exception, depending on the sharing mode.
                Exception failure = Assert.ThrowsAny<Exception>(() => keys.Revoke(doomed.Id));
                Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
                // The failed write changed nothing: the key still works while the store cannot be updated.
                Assert.True(keys.TryValidate(doomed.Key, out _));
            }

            Assert.True(keys.Revoke(doomed.Id));
            Assert.False(keys.TryValidate(doomed.Key, out _));
            Assert.False(new ApiKeyService(path).TryValidate(doomed.Key, out _));
            Assert.True(new ApiKeyService(path).TryValidate(kept.Key, out _));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_corrupt_key_store_is_reported_and_never_overwritten_by_a_new_key()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated existing = keys.Create("script");
            byte[] original = File.ReadAllBytes(path);
            File.WriteAllBytes(path, original[..(original.Length / 2)]);
            byte[] torn = File.ReadAllBytes(path);

            var reopened = new ApiKeyService(path);

            Assert.False(reopened.TryValidate(existing.Key, out _));
            Assert.Throws<InvalidDataException>(() => reopened.List());
            Assert.Throws<InvalidDataException>(() => reopened.Create("new"));
            Assert.Throws<InvalidDataException>(() => reopened.Revoke(existing.Id));
            Assert.Equal(torn, File.ReadAllBytes(path));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData("{\"Keys\": null}")]
    [InlineData("{\"Keys\": [null]}")]
    [InlineData("{\"Keys\": [{\"Id\": \"k1\", \"Name\": \"script\", \"Hash\": null, \"CreatedUtc\": \"2026-01-01T00:00:00Z\"}]}")]
    public void A_key_store_with_a_null_member_is_reported_and_never_overwritten(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-keys-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, contents);
            byte[] before = File.ReadAllBytes(path);

            var keys = new ApiKeyService(path);

            Assert.False(keys.TryValidate("pxk_not-issued-here", out _));
            Assert.Throws<InvalidDataException>(() => keys.List());
            Assert.Throws<InvalidDataException>(() => keys.Create("new"));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_key_store_locked_at_startup_refuses_new_keys_instead_of_replacing_the_file()
    {
        ApiKeyService first = NewService(out string path);
        try
        {
            ApiKeyCreated existing = first.Create("script");
            byte[] original = File.ReadAllBytes(path);

            ApiKeyService reopened;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                reopened = new ApiKeyService(path);
                Assert.Throws<InvalidDataException>(() => reopened.Create("new"));
            }

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(new ApiKeyService(path).TryValidate(existing.Key, out _));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_failed_last_used_write_does_not_turn_a_valid_key_into_an_error()
    {
        ApiKeyService keys = NewService(out string path);
        try
        {
            ApiKeyCreated created = keys.Create("in use");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.True(keys.TryValidate(created.Key, out string id));
                Assert.Equal(created.Id, id);
            }

            Assert.NotNull(Assert.Single(keys.List()).LastUsedUtc);
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/models", true)]
    [InlineData("/v1/embeddings", true)]
    [InlineData("/api/chat", true)]
    [InlineData("/api/generate", true)]
    [InlineData("/api/tags", true)]
    [InlineData("/api/version", true)]
    [InlineData("/api/ps", true)]
    [InlineData("/api/embeddings", true)]
    [InlineData("/api/embed", true)]
    [InlineData("/api/v1/remote/config", false)]
    [InlineData("/api/v1/conversations", false)]
    [InlineData("/api/v1/desktop/integrations", false)]
    [InlineData("/api/v1/apikeys", false)]
    [InlineData("/api/v1/web/access", false)]
    [InlineData("/app", false)]
    public void An_api_key_reaches_the_provider_surface_and_nothing_else(string path, bool allowed)
    {
        Assert.Equal(allowed, ProviderSurfaceProbe.Covers(path));
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>Reaches the internal allow-list the middleware consults.</summary>
internal static class ProviderSurfaceProbe
{
    internal static bool Covers(string path) =>
        (bool)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.ProviderSurface")!
            .GetMethod("Covers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { new Microsoft.AspNetCore.Http.PathString(path) })!;
}

/// <summary>
/// The Ollama dialect. PaRuX does not pretend to be Ollama - it answers in Ollama's shape so tools
/// written against it work unchanged, while every request is served by the same routing and the
/// same model as the OpenAI path.
/// </summary>
public sealed class OllamaCompatibilityTests
{
    private static string Translate(string body, bool generate) =>
        (string)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.OllamaCompatibilityEndpoints")!
            .GetMethod("ToOpenAiRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { body, "qwen", generate })!;

    private static string Extract(string body) =>
        (string)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.OllamaCompatibilityEndpoints")!
            .GetMethod("ExtractAnswer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { body })!;

    [Fact]
    public void An_ollama_chat_becomes_the_same_openai_request_paRuX_already_serves()
    {
        string translated = Translate(
            """{"model":"qwen","messages":[{"role":"user","content":"hello"}],"stream":true}""", generate: false);

        using JsonDocument document = JsonDocument.Parse(translated);
        JsonElement messages = document.RootElement.GetProperty("messages");

        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hello", messages[0].GetProperty("content").GetString());
        Assert.Equal("qwen", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void An_ollama_generate_prompt_becomes_a_user_message_and_its_system_leads()
    {
        string translated = Translate(
            """{"model":"qwen","system":"be terse","prompt":"why is the sky blue"}""", generate: true);

        using JsonDocument document = JsonDocument.Parse(translated);
        JsonElement messages = document.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("be terse", messages[0].GetProperty("content").GetString());
        Assert.Equal("why is the sky blue", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void Ollama_nests_sampling_under_options_and_it_is_lifted_where_openai_expects_it()
    {
        string translated = Translate(
            """{"model":"qwen","prompt":"hi","options":{"temperature":0.2,"num_predict":64}}""", generate: true);

        using JsonDocument document = JsonDocument.Parse(translated);

        Assert.Equal(0.2, document.RootElement.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(64, document.RootElement.GetProperty("max_tokens").GetInt32());
    }

    private static string? Delta(string line) =>
        (string?)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.OllamaStreamTranslator")!
            .GetMethod("ExtractDelta", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { line });

    /// <summary>
    /// The first version of this path buffered the whole answer and emitted two frames. It produced
    /// the right bytes and passed a round-trip test, and it was still wrong: a client watching
    /// /api/chat saw nothing until the model had finished, which is the one thing streaming exists
    /// to prevent. These pin the translation that replaced it.
    /// </summary>
    [Fact]
    public void Each_content_token_becomes_one_ollama_frame_as_it_arrives()
    {
        Assert.Equal("Hel", Delta("""data: {"choices":[{"delta":{"content":"Hel"}}]}"""));
        Assert.Equal("lo", Delta("""data: {"choices":[{"delta":{"content":"lo"}}]}"""));
    }

    [Fact]
    public void The_terminator_and_the_keep_alives_are_not_mistaken_for_answer_text()
    {
        Assert.Null(Delta("data: [DONE]"));
        Assert.Null(Delta("data:"));
        Assert.Null(Delta(": keep-alive"));
        Assert.Null(Delta(string.Empty));
        Assert.Null(Delta("data: not json"));
    }

    /// <summary>
    /// Ollama's shape has no field for a model's private reasoning, and PaRuX's own interface keeps
    /// it folded away. Passing it off as "content" would put thinking a user never asked to see
    /// straight into another application's chat window.
    /// </summary>
    [Fact]
    public void A_reasoning_delta_is_not_shown_to_a_client_as_the_answer()
    {
        Assert.Null(Delta("""data: {"choices":[{"delta":{"reasoning_content":"the user wants..."}}]}"""));
    }

    [Fact]
    public void The_answer_is_read_back_out_of_the_openai_shape()
    {
        Assert.Equal("BLUEBIRD", Extract(
            """{"choices":[{"message":{"role":"assistant","content":"BLUEBIRD"}}]}"""));
        Assert.Equal(string.Empty, Extract("""{"choices":[]}"""));
        Assert.Equal(string.Empty, Extract("not json at all"));
    }
}

/// <summary>The same boundary seen from the wire.</summary>
public sealed class ApiKeyEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ApiKeyEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private ApiKeyService Keys => _factory.Services.GetRequiredService<ApiKeyService>();

    private HttpClient DesktopClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Fact]
    public async Task An_api_key_is_refused_by_the_control_plane_it_must_never_reach()
    {
        ApiKeyCreated created = Keys.Create("third-party app");
        try
        {
            HttpClient client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.Key);

            HttpResponseMessage conversations = await client.GetAsync("/api/v1/conversations");
            HttpResponseMessage remote = await client.GetAsync("/api/v1/remote/status");
            HttpResponseMessage mintAnother = await client.PostAsJsonAsync(
                "/api/v1/apikeys", new ApiKeyCreateRequest { Name = "escalated" });

            Assert.Equal(HttpStatusCode.Forbidden, conversations.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, mintAnother.StatusCode);
        }
        finally { Keys.Revoke(created.Id); }
    }

    [Fact]
    public async Task An_api_key_cannot_make_the_desktop_probe_other_backends()
    {
        ApiKeyCreated created = Keys.Create("third-party app");
        try
        {
            HttpClient client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.Key);

            HttpResponseMessage detect = await client.GetAsync("/api/v1/ai/backends/detect");
            HttpResponseMessage external = await client.GetAsync("/api/v1/ai/backends/external");

            Assert.Equal(HttpStatusCode.Forbidden, detect.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, external.StatusCode);
        }
        finally { Keys.Revoke(created.Id); }
    }

    [Fact]
    public async Task An_api_key_reaches_the_provider_surface()
    {
        ApiKeyCreated created = Keys.Create("third-party app");
        try
        {
            HttpClient client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.Key);

            HttpResponseMessage models = await client.GetAsync("/v1/models");
            HttpResponseMessage tags = await client.GetAsync("/api/tags");
            HttpResponseMessage version = await client.GetAsync("/api/version");

            Assert.NotEqual(HttpStatusCode.Unauthorized, models.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, models.StatusCode);
            Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        }
        finally { Keys.Revoke(created.Id); }
    }

    [Fact]
    public async Task A_revoked_key_is_refused_on_the_provider_surface_at_once()
    {
        ApiKeyCreated created = Keys.Create("third-party app");
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.Key);

        HttpResponseMessage before = await client.GetAsync("/api/tags");
        Keys.Revoke(created.Id);
        HttpResponseMessage after = await client.GetAsync("/api/tags");

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task An_invalid_key_is_rejected_before_it_reaches_anything()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKeyService.KeyPrefix + "made-up");

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_desktop_mints_and_revokes_keys()
    {
        HttpClient desktop = DesktopClient();

        HttpResponseMessage created = await desktop.PostAsJsonAsync(
            "/api/v1/apikeys", new ApiKeyCreateRequest { Name = "from the desktop" });
        ApiKeyCreated? key = await created.Content.ReadFromJsonAsync<ApiKeyCreated>();

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.StartsWith(ApiKeyService.KeyPrefix, key!.Key, StringComparison.Ordinal);

        HttpResponseMessage revoked = await desktop.DeleteAsync("/api/v1/apikeys/" + key.Id);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.False(Keys.TryValidate(key.Key, out _));
    }
}
