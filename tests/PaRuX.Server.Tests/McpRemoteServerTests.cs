using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Until now PaRuX could only launch MCP servers as child processes, which meant every hosted MCP
/// server - that is, every one that requires a login - was unreachable. These tests pin the rules
/// that make reaching one acceptable rather than merely possible: off-device means https, a
/// credential never travels in a URL and never comes back out of the API, the fact that a server is
/// off-device is reported rather than left to be inferred, and an approved tool list is enforced
/// where the model actually gets its tools.
///
/// Every test drives a registry rooted in its own temp directory, so nothing here touches the
/// developer's real MCP configuration or credentials.
/// </summary>
public sealed class McpRemoteServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-mcp-" + Guid.NewGuid().ToString("N"));

    private McpClientRegistry NewRegistry() => new(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    // ---- endpoint rules ----

    [Theory]
    [InlineData("https://mcp.example.com/mcp", "https://mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com/mcp/", "https://mcp.example.com/mcp")]
    [InlineData("  https://mcp.example.com/mcp  ", "https://mcp.example.com/mcp")]
    [InlineData("http://127.0.0.1:3000/mcp", "http://127.0.0.1:3000/mcp")]
    [InlineData("http://localhost:3000/mcp", "http://localhost:3000/mcp")]
    [InlineData("http://192.168.1.20:3000/mcp", "http://192.168.1.20:3000/mcp")]
    public void Usable_endpoints_are_accepted_and_normalised(string url, string expected)
    {
        Assert.True(McpServerEndpointPolicy.TryNormalize(url, out string normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    // Off-device in clear text: the tool arguments are the user's text and the header is their token.
    [InlineData("http://mcp.example.com/mcp")]
    // A token in a URL ends up in logs, history and error messages.
    [InlineData("https://user:token@mcp.example.com/mcp")]
    [InlineData("https://mcp.example.com/mcp?token=abc")]
    [InlineData("https://mcp.example.com/mcp#frag")]
    [InlineData("ftp://mcp.example.com/mcp")]
    [InlineData("mcp.example.com/mcp")]
    [InlineData("")]
    public void Unusable_endpoints_are_refused_with_a_reason(string url)
    {
        Assert.False(McpServerEndpointPolicy.TryNormalize(url, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://mcp.example.com/mcp", true)]
    [InlineData("http://localhost:3000/mcp", false)]
    [InlineData("http://127.0.0.1:3000/mcp", false)]
    [InlineData("http://10.1.2.3:3000/mcp", false)]
    [InlineData("http://192.168.0.5:3000/mcp", false)]
    public void Whether_a_server_is_off_device_is_decided_by_its_address(string url, bool expected)
    {
        Assert.Equal(expected, McpServerEndpointPolicy.IsRemote(url));
    }

    // ---- configuration ----

    [Fact]
    public void A_stdio_server_still_adds_exactly_as_before()
    {
        McpServerConfig config = NewRegistry().AddServer("files", "npx", "-y @modelcontextprotocol/server-filesystem .");

        Assert.Equal(McpTransports.Stdio, config.Transport);
        Assert.Equal("npx", config.Command);
        Assert.Equal(McpAuthModes.None, config.AuthMode);
        Assert.Empty(config.AllowedTools);
    }

    [Fact]
    public void An_http_server_is_stored_with_its_normalised_url()
    {
        McpServerConfig config = NewRegistry().AddServer(new AddMcpServerRequest
        {
            Name = "hosted",
            Transport = McpTransports.Http,
            Url = "https://mcp.example.com/mcp/",
            AuthMode = McpAuthModes.Bearer,
            Credential = "tok-live-123"
        });

        Assert.Equal(McpTransports.Http, config.Transport);
        Assert.Equal("https://mcp.example.com/mcp", config.Url);
        Assert.Equal(McpAuthModes.Bearer, config.AuthMode);
    }

    [Theory]
    [InlineData(McpTransports.Http, "", McpAuthModes.None, null, "URL")]
    [InlineData(McpTransports.Http, "http://mcp.example.com/mcp", McpAuthModes.None, null, "https")]
    [InlineData(McpTransports.Http, "https://mcp.example.com/mcp", "kerberos", null, "auth mode")]
    [InlineData(McpTransports.Http, "https://mcp.example.com/mcp", McpAuthModes.Bearer, null, "token")]
    [InlineData("carrier-pigeon", "", McpAuthModes.None, null, "transport")]
    public void A_server_that_could_not_work_is_refused_while_the_user_is_looking_at_it(
        string transport, string url, string authMode, string? credential, string expectedInMessage)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NewRegistry().AddServer(
            new AddMcpServerRequest
            {
                Name = "hosted",
                Transport = transport,
                Url = url,
                AuthMode = authMode,
                Credential = credential
            }));

        Assert.Contains(expectedInMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_reports_that_a_server_is_off_device_and_holds_a_credential_without_revealing_it()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig config = registry.AddServer(new AddMcpServerRequest
        {
            Name = "hosted",
            Transport = McpTransports.Http,
            Url = "https://mcp.example.com/mcp",
            AuthMode = McpAuthModes.Bearer,
            Credential = "tok-live-123"
        });
        // Disabled before asking for status so this test asserts the reported configuration rather
        // than attempting a real connection to a domain that does not exist.
        await registry.SetEnabledAsync(config.Id, false, CancellationToken.None);

        McpServerStatus status = Assert.Single(await registry.GetStatusesAsync(CancellationToken.None));

        Assert.True(status.IsRemote);
        Assert.True(status.HasCredential);
        Assert.Equal(McpTransports.Http, status.Transport);
        Assert.Equal("https://mcp.example.com/mcp", status.Url);

        // The token is not on the status object, and it is not in the config file either.
        Assert.DoesNotContain("tok-live-123",
            System.Text.Json.JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.DoesNotContain("tok-live-123",
            File.ReadAllText(Path.Combine(_directory, "mcp-clients.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_is_encrypted_at_rest_rather_than_stored_beside_the_config()
    {
        McpClientRegistry registry = NewRegistry();
        registry.AddServer(new AddMcpServerRequest
        {
            Name = "hosted",
            Transport = McpTransports.Http,
            Url = "https://mcp.example.com/mcp",
            AuthMode = McpAuthModes.Bearer,
            Credential = "tok-live-123"
        });

        string credentialFile = Path.Combine(_directory, "mcp-credentials.json");
        Assert.True(File.Exists(credentialFile));
        Assert.DoesNotContain("tok-live-123", File.ReadAllText(credentialFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_server_takes_its_credential_with_it()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig config = registry.AddServer(new AddMcpServerRequest
        {
            Name = "hosted",
            Transport = McpTransports.Http,
            Url = "https://mcp.example.com/mcp",
            AuthMode = McpAuthModes.Bearer,
            Credential = "tok-live-123"
        });

        var store = new McpCredentialStore(_directory);
        Assert.True(store.Has(config.Id, McpCredentialStore.BearerKind));

        await registry.RemoveServerAsync(config.Id, CancellationToken.None);

        Assert.False(new McpCredentialStore(_directory).Has(config.Id, McpCredentialStore.BearerKind));
    }

    // ---- tool gating ----

    [Fact]
    public async Task An_approved_tool_list_is_stored_deduplicated_and_can_be_cleared()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig config = registry.AddServer(new AddMcpServerRequest
        {
            Name = "hosted",
            Transport = McpTransports.Http,
            Url = "https://mcp.example.com/mcp",
            AllowedTools = new List<string> { "search", " search ", "", "fetch" }
        });

        Assert.Equal(new[] { "search", "fetch" }, config.AllowedTools);

        await registry.SetAllowedToolsAsync(config.Id, new[] { "fetch" }, CancellationToken.None);
        Assert.Equal(new[] { "fetch" },
            registry.GetConfigs().Single(c => c.Id == config.Id).AllowedTools);

        // Empty means "everything this server advertises" - the behaviour before allowlists existed.
        await registry.SetAllowedToolsAsync(config.Id, Array.Empty<string>(), CancellationToken.None);
        Assert.Empty(registry.GetConfigs().Single(c => c.Id == config.Id).AllowedTools);
    }

    [Fact]
    public async Task Narrowing_the_tool_list_on_a_server_that_does_not_exist_is_reported()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => NewRegistry().SetAllowedToolsAsync("no-such-id", new[] { "fetch" }, CancellationToken.None));
    }

    // ---- credential store ----

    [Fact]
    public void A_credential_cannot_be_read_back_under_a_different_purpose()
    {
        var store = new McpCredentialStore(_directory);
        store.Set("server-1", McpCredentialStore.BearerKind, "tok-live-123");

        Assert.Equal("tok-live-123", store.Get("server-1", McpCredentialStore.BearerKind));
        Assert.Null(store.Get("server-1", McpCredentialStore.OAuthClientSecretKind));
        Assert.Null(store.Get("server-2", McpCredentialStore.BearerKind));
    }

    [Fact]
    public void Forgetting_a_server_drops_every_kind_of_credential_it_had()
    {
        var store = new McpCredentialStore(_directory);
        store.Set("server-1", McpCredentialStore.BearerKind, "tok-a");
        store.Set("server-1", McpCredentialStore.OAuthTokensKind, "{\"accessToken\":\"tok-b\"}");
        store.Set("server-2", McpCredentialStore.BearerKind, "tok-c");

        store.Forget("server-1");

        Assert.False(store.Has("server-1", McpCredentialStore.BearerKind));
        Assert.False(store.Has("server-1", McpCredentialStore.OAuthTokensKind));
        Assert.True(store.Has("server-2", McpCredentialStore.BearerKind));
    }

    [Fact]
    public void The_oauth_redirect_is_always_loopback()
    {
        Uri redirect = McpOAuthCoordinator.AllocateLoopbackRedirect();

        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.True(redirect.Port > 0);
        Assert.Equal("/parux-mcp-callback/", redirect.AbsolutePath);
    }
}
