using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Historical cloud-fallback configuration remains readable for migration/diagnostics, but shipped
/// PaRuX Core is local-only. These tests pin both halves of that contract: credentials stay
/// protected and a prompt-free model-list probe can still diagnose configuration, while no valid
/// configuration can arm or invoke cloud inference.
/// </summary>
public sealed class CloudFallbackTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-cloud-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly CloudFallbackPolicy _policy;
    private readonly CloudFallbackService _service;

    public CloudFallbackTests()
    {
        _policy = new CloudFallbackPolicy(_directory, new ReversibleTestProtector());
        _service = new CloudFallbackService(_policy);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void The_fallback_is_off_and_unarmed_until_the_user_configures_it()
    {
        CloudFallbackStatus status = _service.GetStatus();

        Assert.False(status.Enabled);
        Assert.False(status.Armed);
        Assert.False(status.HasApiKey);
        Assert.Equal(0, status.UseCount);
        Assert.Null(status.LastUsedUtc);
        Assert.False(_service.IsArmed(out string? reason));
        Assert.Contains("turned off", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_offered_provider_is_a_usable_https_preset_or_the_custom_entry()
    {
        foreach (CloudFallbackProvider provider in CloudFallbackPolicy.Providers)
        {
            if (string.Equals(provider.Id, "custom", StringComparison.Ordinal))
            {
                Assert.Equal(string.Empty, provider.BaseUrl);
                continue;
            }

            Assert.True(
                CloudFallbackPolicy.TryNormalizeBaseUrl(provider.BaseUrl, out _),
                provider.Id + " ships a base URL the policy itself would reject.");
            Assert.False(string.IsNullOrWhiteSpace(provider.SuggestedModel));
        }
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("https://user:pass@api.example.com")]
    [InlineData("https://api.example.com/v1?key=abc")]
    [InlineData("https://api.example.com/v1#frag")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void Refused_endpoints_are_refused(string endpoint)
    {
        Assert.False(CloudFallbackPolicy.TryNormalizeBaseUrl(endpoint, out _));
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1")]
    [InlineData("  https://api.openai.com/v1  ", "https://api.openai.com/v1")]
    public void Accepted_endpoints_are_normalised(string endpoint, string expected)
    {
        Assert.True(CloudFallbackPolicy.TryNormalizeBaseUrl(endpoint, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Enabling_without_a_model_is_refused()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => _policy.Save(enabled: true, "openrouter", null, model: "  ", apiKey: "sk-test"));
        Assert.Contains("model", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_provider_is_refused()
    {
        Assert.Throws<InvalidDataException>(
            () => _policy.Save(enabled: true, "some-other-cloud", null, "a-model", "sk-test"));
    }

    [Fact]
    public void A_custom_provider_must_still_supply_an_https_endpoint()
    {
        Assert.Throws<InvalidDataException>(
            () => _policy.Save(enabled: true, "custom", "http://192.168.1.5:8080/v1", "a-model", "sk-test"));
    }

    [Fact]
    public void The_real_protector_round_trips_the_key_and_never_writes_it_in_clear_text()
    {
        string directory = Path.Combine(_directory, "real");
        var real = new CloudFallbackPolicy(directory);

        real.Save(enabled: true, "openrouter", null, "openai/gpt-oss-120b", "sk-test");

        Assert.True(real.Get().HasApiKey);
        Assert.Equal("sk-test", real.TryGetApiKey());
        Assert.DoesNotContain("sk-test",
            File.ReadAllText(Path.Combine(directory, "cloud-fallback.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fully_configured_remote_provider_is_still_not_armed_for_inference()
    {
        _policy.Save(enabled: true, "openrouter", null, "openai/gpt-oss-120b", "sk-test-key");
        CloudFallbackStatus status = _service.GetStatus();

        Assert.True(status.Enabled);
        Assert.True(status.HasApiKey);
        Assert.False(status.Armed);
        Assert.Contains("local-only", status.NotArmedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OpenRouter", status.ProviderDisplayName);
        Assert.Equal("https://openrouter.ai/api/v1", status.BaseUrl);
        Assert.False(_service.IsArmed(out string? reason));
        Assert.Contains("cloud inference egress is disabled", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saving_again_without_a_key_keeps_the_stored_one_but_never_arms_inference()
    {
        _policy.Save(enabled: true, "openrouter", null, "openai/gpt-oss-120b", "sk-test-key");
        _policy.Save(enabled: true, "openrouter", null, "another/model", apiKey: null);

        CloudFallbackStatus status = _service.GetStatus();
        Assert.True(status.HasApiKey);
        Assert.Equal("another/model", status.Model);
        Assert.False(status.Armed);
        Assert.Contains("local-only", status.NotArmedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_stream_call_fails_before_network_even_with_complete_restored_credentials()
    {
        _policy.Save(
            enabled: true,
            providerId: "custom",
            baseUrl: "https://127.0.0.1:1/v1",
            model: "restored-cloud-model",
            apiKey: "sk-restored");
        var request = new ChatCompletionRequest
        {
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "user", Content = "private prompt that must remain local" }
            }
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.OpenStreamAsync(request, CancellationToken.None));

        Assert.Contains("local-only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _service.GetStatus().UseCount);
    }

    [Fact]
    public void Turning_the_fallback_off_drops_the_stored_credential()
    {
        _policy.Save(enabled: true, "openrouter", null, "openai/gpt-oss-120b", "sk-test-key");
        Assert.True(_service.GetStatus().HasApiKey);

        _service.Disable();

        CloudFallbackStatus status = _service.GetStatus();
        Assert.False(status.Enabled);
        Assert.False(status.HasApiKey);
        Assert.False(status.Armed);
        Assert.Null(_policy.TryGetApiKey());
    }

    [Fact]
    public void A_corrupt_config_fails_closed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "cloud-fallback.json"), "{ this is not json");

        CloudFallbackStatus status = _service.GetStatus();
        Assert.False(status.Enabled);
        Assert.False(status.Armed);
    }

    [Fact]
    public void Enabled_without_a_key_is_inert_and_says_so()
    {
        _policy.Save(enabled: true, "openrouter", null, "openai/gpt-oss-120b", apiKey: string.Empty);

        Assert.False(_service.IsArmed(out string? reason));
        Assert.Contains("API key", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Historical_use_audit_is_preserved_without_reenabling_the_route()
    {
        _policy.Save(enabled: true, "openrouter", null, "openai/gpt-oss-120b", "sk-test-key");
        _policy.RecordUse("Historical migration evidence.");

        CloudFallbackStatus status = _service.GetStatus();
        Assert.Equal(1, status.UseCount);
        Assert.NotNull(status.LastUsedUtc);
        Assert.Contains("Historical", status.LastUsedReason);
        Assert.False(status.Armed);
    }

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"openai/gpt-oss-120b\"}]}", "openai/gpt-oss-120b", true)]
    [InlineData("{\"data\":[{\"id\":\"something-else\"}]}", "openai/gpt-oss-120b", false)]
    [InlineData("{\"data\":[]}", "x", false)]
    [InlineData("not json", "x", false)]
    [InlineData("{\"models\":[{\"id\":\"x\"}]}", "x", false)]
    public void Model_availability_is_read_from_the_providers_own_list(string body, string model, bool expected)
    {
        Assert.Equal(expected, CloudFallbackService.ModelIsListed(body, model));
    }

    [Fact]
    public void Moving_the_endpoint_to_another_host_drops_the_stored_key()
    {
        _policy.Save(enabled: true, "custom", "https://api.first-provider.example/v1", "a-model", "sk-test-key");
        Assert.Equal("sk-test-key", _policy.TryGetApiKey());

        _policy.Save(enabled: true, "custom", "https://api.second-provider.example/v1", "a-model", apiKey: null);

        Assert.False(_service.GetStatus().HasApiKey);
        Assert.Null(_policy.TryGetApiKey());
    }

    [Fact]
    public void Changing_only_the_path_on_the_same_host_keeps_the_key()
    {
        _policy.Save(enabled: true, "custom", "https://api.same-provider.example/v1", "a-model", "sk-test-key");

        _policy.Save(enabled: true, "custom", "https://api.same-provider.example/v2", "a-model", apiKey: null);

        Assert.Equal("sk-test-key", _policy.TryGetApiKey());
    }

    [Fact]
    public void A_new_host_saved_with_its_own_key_stores_only_that_key()
    {
        _policy.Save(enabled: true, "custom", "https://api.first-provider.example/v1", "a-model", "sk-old-key");

        _policy.Save(enabled: true, "custom", "https://api.second-provider.example/v1", "a-model", "sk-new-key");

        Assert.Equal("sk-new-key", _policy.TryGetApiKey());
    }

    [Fact]
    public void Probe_client_never_follows_redirects_keeps_cookies_or_uses_a_proxy()
    {
        using SocketsHttpHandler handler = CloudFallbackService.CreateProbeHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }

    /// <summary>
    /// The credential probe must check the address it connects to, not only the URL it was given. The injected
    /// resolver stands in for DNS: every answer is inspected before any socket opens, so a private, loopback or
    /// link-local answer - even alongside a public one - refuses the probe and nothing is sent.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("8.8.8.8|10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.1")]
    public async Task Probe_refuses_a_provider_host_with_any_private_dns_answer_before_connecting(string answers)
    {
        IPAddress[] addresses = answers.Split('|').Select(IPAddress.Parse).ToArray();
        var resolvedHosts = new List<string>();
        var service = new CloudFallbackService(_policy, (host, cancellationToken) =>
        {
            resolvedHosts.Add(host);
            return Task.FromResult(addresses);
        });
        _policy.Save(enabled: true, "custom", "https://api.probe-check.example/v1", "a-model", "sk-probe-check");

        CloudFallbackTestResult result = await service.TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new[] { "api.probe-check.example" }, resolvedHosts);
    }

    [Fact]
    public async Task A_small_model_list_is_read_and_its_model_is_found()
    {
        _policy.Save(enabled: true, "custom", "https://api.bounded-check.example/v1", "a-model", "sk-bounded-check");
        var answer = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"a-model\"}]}"));
        var service = new CloudFallbackService(
            _policy, new FixedAnswerHandler(HttpStatusCode.OK, answer), new InFlightRequestRegistry());

        CloudFallbackTestResult result = await service.TestAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ModelAvailable);
    }

    /// <summary>
    /// A complete catalogue has no guaranteed small size: a list well past 256 KiB, with the requested model last, must be
    /// read in full and its model found. The bound refuses a runaway answer, not a large valid one.
    /// </summary>
    [Fact]
    public async Task A_catalogue_larger_than_256_KiB_is_read_in_full_and_its_model_is_found()
    {
        _policy.Save(enabled: true, "custom", "https://api.bounded-check.example/v1", "a-model", "sk-bounded-check");
        var catalogue = new System.Text.StringBuilder("{\"data\":[");
        for (int i = 0; catalogue.Length < 600 * 1024; i++)
            catalogue.Append("{\"id\":\"vendor/model-").Append(i)
                .Append("\",\"description\":\"").Append('d', 200).Append("\"},");
        catalogue.Append("{\"id\":\"a-model\"}]}");
        var answer = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(catalogue.ToString()));
        var service = new CloudFallbackService(
            _policy, new FixedAnswerHandler(HttpStatusCode.OK, answer), new InFlightRequestRegistry());

        CloudFallbackTestResult result = await service.TestAsync(CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.True(result.ModelAvailable);
    }

    /// <summary>
    /// The credential check must not buffer an answer without bound: a provider, or an endpoint the user typed in, could
    /// stream far more than a model list. The read stops at the limit, for a success and for a rejection alike.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_provider_answer_beyond_the_limit_is_refused_without_being_buffered(HttpStatusCode status)
    {
        _policy.Save(enabled: true, "custom", "https://api.bounded-check.example/v1", "a-model", "sk-bounded-check");
        var answer = new CountingStream(8 * 1024 * 1024);
        var service = new CloudFallbackService(
            _policy, new FixedAnswerHandler(status, answer), new InFlightRequestRegistry());

        CloudFallbackTestResult result = await service.TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(answer.BytesRead <= CloudFallbackService.MaxProbeBodyBytes + 1,
            $"The probe read {answer.BytesRead} bytes; the limit is {CloudFallbackService.MaxProbeBodyBytes}.");
    }

    /// <summary>
    /// A Save that is still inside its read-modify-write must not be overtaken by a Disable. The Save is held inside
    /// Protect while the Disable runs; once the Disable returns, the file must show the fallback off with no credential,
    /// whatever the resumed Save would have written.
    /// </summary>
    [Fact]
    public async Task A_disable_that_arrives_during_a_save_is_not_undone_by_that_save()
    {
        var protector = new GatedProtector();
        var policy = new CloudFallbackPolicy(_directory, protector);
        policy.Save(enabled: true, "custom", "https://api.gate-check.example/v1", "a-model", "sk-initial");

        protector.HoldProtect();
        Task save = Task.Run(() =>
            policy.Save(enabled: true, "custom", "https://api.gate-check.example/v1", "a-model", "sk-racing"));
        await protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Task disable = Task.Run(() => policy.Disable());
        // An unguarded Disable finishes here while the save is still held; a guarded one waits for the save.
        await Task.WhenAny(disable, Task.Delay(TimeSpan.FromMilliseconds(300)));

        protector.Release();
        await save.WaitAsync(TimeSpan.FromSeconds(20));
        await disable.WaitAsync(TimeSpan.FromSeconds(20));

        CloudFallbackSettings settings = policy.Get();
        Assert.False(settings.Enabled);
        Assert.False(settings.HasApiKey);
        Assert.Null(policy.TryGetApiKey());
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        _policy.Save(enabled: true, "custom", "https://api.temp-check.example/v1", "a-model", "sk-temp-check");

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal("sk-temp-check", _policy.TryGetApiKey());
    }

    /// <summary>Holds Protect open on demand so a test can interleave another writer deterministically.</summary>
    private sealed class GatedProtector : ISecretProtector
    {
        private readonly ReversibleTestProtector _inner = new();
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private volatile bool _hold;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Description => "test-only gated encoding";

        public void HoldProtect() => _hold = true;

        public void Release() => _release.Set();

        public string Protect(string purpose, string plaintext)
        {
            if (_hold)
            {
                Entered.TrySetResult();
                _release.Wait(TimeSpan.FromSeconds(20));
            }
            return _inner.Protect(purpose, plaintext);
        }

        public string? Unprotect(string purpose, string blob) => _inner.Unprotect(purpose, blob);
    }

    private sealed class FixedAnswerHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Stream _answer;

        public FixedAnswerHandler(HttpStatusCode status, Stream answer)
        {
            _status = status;
            _answer = answer;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StreamContent(_answer) });
    }

    /// <summary>A provider answer of a fixed size that records how many bytes a reader pulled from it.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly long _length;

        public CountingStream(long length) => _length = length;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, _length - BytesRead);
            if (count <= 0) return 0;
            buffer[..count].Fill((byte)'a');
            BytesRead += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ReversibleTestProtector : ISecretProtector
    {
        public string Description => "test-only reversible encoding";

        public string Protect(string purpose, string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(purpose + "\u0000" + plaintext));

        public string? Unprotect(string purpose, string blob)
        {
            try
            {
                string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob));
                string prefix = purpose + "\u0000";
                return decoded.StartsWith(prefix, StringComparison.Ordinal) ? decoded[prefix.Length..] : null;
            }
            catch (FormatException) { return null; }
        }
    }
}
