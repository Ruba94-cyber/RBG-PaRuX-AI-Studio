using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX is local-only by default and stays that way unless the user deliberately opts out. These
/// tests pin the properties that make the opt-out safe rather than merely present: it is off until
/// switched on, revoking it retires an already-stored remote endpoint instead of grandfathering it,
/// a remote hop must be encrypted, and the structural URL checks are not relaxed along with it.
///
/// The benchmark target that a stored key is bound to is written under the data root, so each test points the data root
/// at its own directory: the accept path of SetProvider runs for real without touching the developer's configuration.
/// </summary>
[Collection("ProcessEnvironmentSerial")]
public sealed class RemoteProviderPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-remote-policy-" + Guid.NewGuid().ToString("N"));
    private readonly RemoteProviderPolicy _policy;
    private readonly RemoteProviderPolicy _original = ExternalTextProviderService.Policy;
    private readonly string _originalDataRoot = PaRuXDataRoot.Path;

    public RemoteProviderPolicyTests()
    {
        _policy = new RemoteProviderPolicy(_directory);
        ExternalTextProviderService.Policy = _policy;
        PaRuXDataRoot.UseForTests(Path.Combine(_directory, "data"));
    }

    public void Dispose()
    {
        ExternalTextProviderService.Policy = _original;
        PaRuXDataRoot.UseForTests(_originalDataRoot);
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Remote_providers_are_disabled_until_the_user_turns_them_on()
    {
        Assert.False(_policy.IsRemoteAllowed());
        Assert.False(ExternalTextProviderService.TryNormalizeEndpoint(
            "https://api.example.com", allowRemote: false, out _));
    }

    [Fact]
    public void Configuring_a_remote_endpoint_is_refused_while_the_opt_in_is_off()
    {
        var service = new ExternalTextProviderService();
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => service.SetProvider("openai", "https://api.example.com"));

        // The message must say *why* it was refused, so the user reaches for the opt-in rather than
        // concluding the URL was malformed.
        Assert.Contains("opt-in", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_remote_endpoint_is_accepted_only_once_the_opt_in_is_enabled()
    {
        Assert.False(ExternalTextProviderService.TryNormalizeEndpoint(
            "https://api.example.com", allowRemote: false, out _));

        _policy.SetRemoteAllowed(true);
        Assert.True(_policy.IsRemoteAllowed());
        Assert.True(ExternalTextProviderService.TryNormalizeEndpoint(
            "https://api.example.com", allowRemote: true, out string normalized));
        Assert.Equal("https://api.example.com", normalized);
    }

    /// <summary>Opting in widens where PaRuX may talk - not how carelessly. Sending a prompt and an
    /// API key to a public host in clear text stays refused.</summary>
    [Theory]
    [InlineData("http://api.example.com")]
    [InlineData("http://8.8.8.8:11434")]
    public void A_remote_endpoint_over_plain_http_stays_refused(string endpoint)
    {
        Assert.False(ExternalTextProviderService.TryNormalizeEndpoint(endpoint, allowRemote: true, out _));
    }

    /// <summary>The structural guards are shared, so enabling remote must not smuggle credentials,
    /// query strings, fragments or paths past them.</summary>
    [Theory]
    [InlineData("https://user:secret@api.example.com")]
    [InlineData("https://api.example.com?key=abc")]
    [InlineData("https://api.example.com/#fragment")]
    [InlineData("https://api.example.com/v1")]
    [InlineData("ftp://api.example.com")]
    [InlineData("not-a-url")]
    public void Structural_url_guards_still_apply_with_remote_enabled(string endpoint)
    {
        Assert.False(ExternalTextProviderService.TryNormalizeEndpoint(endpoint, allowRemote: true, out _));
    }

    [Fact]
    public void Local_endpoints_keep_working_regardless_of_the_remote_setting()
    {
        foreach (bool allowed in new[] { false, true })
        {
            Assert.True(ExternalTextProviderService.TryNormalizeEndpoint(
                "http://127.0.0.1:11434", allowed, out string normalized));
            Assert.Equal("http://127.0.0.1:11434", normalized);
        }
    }

    [Fact]
    public void Revoking_the_opt_in_retires_a_stored_remote_endpoint_rather_than_grandfathering_it()
    {
        _policy.SetRemoteAllowed(true);
        Assert.True(ExternalTextProviderService.TryNormalizeEndpoint(
            "https://api.example.com", _policy.IsRemoteAllowed(), out _));

        _policy.SetRemoteAllowed(false);
        Assert.False(ExternalTextProviderService.TryNormalizeEndpoint(
            "https://api.example.com", _policy.IsRemoteAllowed(), out _));
    }

    [Fact]
    public void Turning_the_opt_in_off_also_drops_the_stored_credential()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        _policy.SetApiKey("sk-test-value");
        Assert.True(_policy.HasApiKey());

        _policy.SetRemoteAllowed(false);
        Assert.False(_policy.HasApiKey());
        Assert.Null(_policy.TryGetApiKey());
    }

    [Fact]
    public void A_stored_key_round_trips_and_is_never_written_in_clear_text()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        _policy.SetApiKey("sk-secret-abc123");

        Assert.Equal("sk-secret-abc123", _policy.TryGetApiKey());

        string onDisk = File.ReadAllText(Path.Combine(_directory, "remote-provider-policy.json"));
        Assert.DoesNotContain("sk-secret-abc123", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_the_key_leaves_the_opt_in_intact()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        _policy.SetApiKey("sk-test-value");
        _policy.SetApiKey(null);

        Assert.False(_policy.HasApiKey());
        Assert.True(_policy.IsRemoteAllowed());
    }

    /// <summary>A corrupt policy file must not fail *open* - a privacy boundary defaults closed.</summary>
    [Fact]
    public void A_corrupt_policy_file_is_treated_as_not_allowed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "remote-provider-policy.json"), "{ not json");

        Assert.False(_policy.IsRemoteAllowed());
        Assert.False(_policy.HasApiKey());
    }

    [Theory]
    [InlineData("https://api.example.com", true)]
    [InlineData("http://127.0.0.1:11434", false)]
    [InlineData("http://192.168.1.50:1234", false)]
    [InlineData("", false)]
    public void Remote_endpoints_are_reported_as_remote_so_the_ui_can_say_so(string endpoint, bool expected)
    {
        Assert.Equal(expected, ExternalTextProviderService.IsRemoteEndpoint(endpoint));
    }

    /// <summary>
    /// The credential-leak boundary. A billable cloud key must reach the remote provider it was
    /// entered for and nothing else - handing it to whatever happens to be listening on a LAN port
    /// would leak it for no benefit at all.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://192.168.1.50:8080")]
    [InlineData("http://10.0.0.20:5001")]
    public void A_stored_key_is_never_sent_to_a_local_or_lan_provider(string endpoint)
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        _policy.SetApiKey("sk-secret-abc123");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(request, endpoint);

        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void A_stored_key_is_sent_as_a_bearer_token_to_a_remote_provider()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        new ExternalTextProviderService().SetProvider("custom", "https://api.example.com");
        _policy.SetApiKey("sk-secret-abc123");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(request, "https://api.example.com");

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-secret-abc123", request.Headers.Authorization?.Parameter);
    }

    /// <summary>
    /// The stored key is for the configured benchmark target. Another remote endpoint, even with the opt-in on, is not
    /// sent it: a bearer token is never attached to a host it was not saved for.
    /// </summary>
    [Fact]
    public void The_stored_key_is_attached_only_to_the_configured_benchmark_target()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        new ExternalTextProviderService().SetProvider("custom", "https://api.target.example");
        _policy.SetApiKey("sk-secret-abc123");

        using var other = new HttpRequestMessage(HttpMethod.Post, "https://api.other.example/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(other, "https://api.other.example");
        Assert.Null(other.Headers.Authorization);

        using var target = new HttpRequestMessage(HttpMethod.Post, "https://api.target.example/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(target, "https://api.target.example");
        Assert.Equal("sk-secret-abc123", target.Headers.Authorization?.Parameter);
    }

    /// <summary>
    /// Repointing the benchmark target to another endpoint drops the stored key, so it cannot follow the target to a host
    /// the user never gave it to. Returning to the first endpoint does not revive it.
    /// </summary>
    [Fact]
    public void Repointing_the_benchmark_target_drops_the_stored_key()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        var provider = new ExternalTextProviderService();
        provider.SetProvider("custom", "https://api.first.example");
        _policy.SetApiKey("sk-secret-abc123");

        provider.SetProvider("custom", "https://api.second.example");
        Assert.False(_policy.HasApiKey());

        using var second = new HttpRequestMessage(HttpMethod.Post, "https://api.second.example/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(second, "https://api.second.example");
        Assert.Null(second.Headers.Authorization);

        provider.SetProvider("custom", "https://api.first.example");
        using var first = new HttpRequestMessage(HttpMethod.Post, "https://api.first.example/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(first, "https://api.first.example");
        Assert.Null(first.Headers.Authorization);
    }

    [Fact]
    public void No_authorization_header_is_invented_when_no_key_is_stored()
    {
        _policy.SetRemoteAllowed(true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(request, "https://api.example.com");

        Assert.Null(request.Headers.Authorization);
    }

    /// <summary>Revoking the opt-in drops the key, so nothing can keep authenticating afterwards.</summary>
    [Fact]
    public void Revoking_the_opt_in_stops_the_key_from_being_sent()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        new ExternalTextProviderService().SetProvider("custom", "https://api.example.com");
        _policy.SetApiKey("sk-secret-abc123");
        _policy.SetRemoteAllowed(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat/completions");
        ExternalTextProviderService.ApplyRemoteAuthorization(request, "https://api.example.com");

        Assert.Null(request.Headers.Authorization);
    }

    /// <summary>An explicit header set by a caller wins; this helper must not overwrite it.</summary>
    [Fact]
    public void An_existing_authorization_header_is_left_alone()
    {
        if (!OperatingSystem.IsWindows()) return;

        _policy.SetRemoteAllowed(true);
        _policy.SetApiKey("sk-secret-abc123");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat/completions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-supplied");
        ExternalTextProviderService.ApplyRemoteAuthorization(request, "https://api.example.com");

        Assert.Equal("caller-supplied", request.Headers.Authorization?.Parameter);
    }
}
