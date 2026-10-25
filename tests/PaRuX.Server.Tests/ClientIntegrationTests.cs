using System.Net;
using System.Text.Json.Nodes;
using PaRuX.Services.Integrations;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Settings > Integrations: config editors, the connect/test/disconnect/restore flow and the
/// guarantee that nothing outside PaRuX's own entries is changed. Clients are replaced by small
/// .cmd scripts that behave like the real CLI's file-creation test.
/// </summary>
public sealed class ClientIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-integrations-" + Guid.NewGuid().ToString("N"));
    private readonly ClientIntegrationEnvironment _environment;
    private readonly IntegrationStore _store;
    private readonly FakeKeys _keys = new();
    private static readonly GatewayEndpoint Endpoint = new("http://127.0.0.1:11435", "parux-sk-test", "parux-auto");

    public ClientIntegrationTests()
    {
        _environment = new ClientIntegrationEnvironment(
            Path.Combine(_root, "home"), Path.Combine(_root, "home", ".codex"), Path.Combine(_root, "home", ".config", "opencode"));
        Directory.CreateDirectory(_environment.CodexHome);
        Directory.CreateDirectory(_environment.OpenCodeConfigDirectory);
        _store = new IntegrationStore(Path.Combine(_root, "parux", "integrations"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Codex_profile_is_standalone_and_refuses_foreign_or_legacy_profiles()
    {
        string profile = CodexConfigEditor.Apply(null, "model = \"gpt-5\"\n", Endpoint, out string? error)!;
        Assert.Null(error);
        Assert.StartsWith(CodexConfigEditor.OwnershipMarker, profile);
        Assert.Contains("model_provider = \"parux\"", profile);
        Assert.Contains("base_url = \"http://127.0.0.1:11435/v1\"", profile);
        Assert.Contains("wire_api = \"responses\"", profile);
        Assert.Equal(profile, CodexConfigEditor.Apply(profile, null, Endpoint, out _));                  // idempotent

        Assert.Null(CodexConfigEditor.Apply("model = \"mine\"\n", null, Endpoint, out error));           // user's own parux.config.toml
        Assert.Contains("not created by PaRuX", error);
        Assert.Null(CodexConfigEditor.Apply(null, "[profiles.parux]\nmodel = \"x\"\n", Endpoint, out error));
        Assert.Contains("legacy", error);

        Assert.Equal("ollama", CodexIntegration.MainProvider("model_provider = \"ollama\"\n[model_providers.ollama]\n"));
        Assert.Null(CodexIntegration.MainProvider("[model_providers.x]\nmodel_provider = \"y\"\n"));
    }

    [Fact]
    public void OpenCode_editor_keeps_other_providers_default_model_and_refuses_comments()
    {
        const string user = """
            {
              "$schema": "https://opencode.ai/config.json",
              "provider": { "local": { "npm": "@ai-sdk/openai-compatible", "options": { "baseURL": "http://127.0.0.1:1234/v1" } } },
              "model": "local/qwen"
            }
            """;
        JsonObject applied = JsonNode.Parse(OpenCodeConfigEditor.Apply(user, Endpoint, out string? error)!)!.AsObject();
        Assert.Null(error);
        Assert.Equal("local/qwen", applied["model"]!.GetValue<string>());
        Assert.NotNull(applied["provider"]!["local"]);
        Assert.True(OpenCodeConfigEditor.IsManaged(applied["provider"]!["parux"]!.AsObject()));
        Assert.Equal("local", OpenCodeConfigEditor.DefaultProvider(user));

        string? removed = OpenCodeConfigEditor.Remove(applied.ToJsonString(), out bool changed);
        Assert.True(changed);
        JsonObject after = JsonNode.Parse(removed!)!.AsObject();
        Assert.Null(after["provider"]!["parux"]);
        Assert.NotNull(after["provider"]!["local"]);
        Assert.Equal("local/qwen", after["model"]!.GetValue<string>());

        Assert.Null(OpenCodeConfigEditor.Apply("{ // mine\n}", Endpoint, out error));
        Assert.Contains("comments", error);
        Assert.False(OpenCodeConfigEditor.HasComments("{ \"u\": \"http://x//y\" }"));                  // "//" inside a string is not a comment
        Assert.Null(OpenCodeConfigEditor.Apply("{\"provider\":{\"parux\":{\"npm\":\"x\"}}}", Endpoint, out error));
        Assert.Contains("will not overwrite", error);
    }

    [Fact]
    public void Launch_command_lines_refuse_cmd_metacharacters()
    {
        Assert.Equal("\"C:\\t\\claude.exe\" --model parux-auto \"a b\"",
            IntegrationProcess.CommandLine("C:\\t\\claude.exe", new[] { "--model", "parux-auto", "a b" }));
        Assert.Throws<ArgumentException>(() => IntegrationProcess.CommandLine("x.exe", new[] { "a & calc" }));
        Assert.Throws<ArgumentException>(() => IntegrationProcess.CommandLine("x.exe", new[] { "%PATH%" }));
        Assert.Equal("0.158.0-alpha.2", ClientLocator.ParseVersion("codex-cli 0.158.0-alpha.2"));
        Assert.True(ClientIntegrationService.CompareVersions("2.1.9", "2.1.283") < 0);
        Assert.Equal(0, ClientIntegrationService.CompareVersions("0.158.0-alpha.2.1", "0.158.0"));
    }

    [Fact]
    public async Task OpenCode_connect_is_idempotent_and_disconnect_removes_only_parux()
    {
        string config = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json");
        const string original = "{\n  \"provider\": { \"local\": { \"npm\": \"x\" } },\n  \"model\": \"local/qwen\"\n}\n";
        File.WriteAllText(config, original);
        var client = new OpenCodeIntegration(_environment, () => new[] { FakeClient("PARUX_OPENCODE_OK", "parux_opencode_test.txt") });
        ClientIntegrationService service = Service(client);

        IntegrationOutcome first = await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None);
        Assert.True(first.Success, first.Message);
        Assert.Equal(ClientIntegrationStatus.Connected, first.Status);
        IntegrationOutcome second = await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-code", null, CancellationToken.None);
        Assert.True(second.Success, second.Message);
        Assert.Equal(1, _keys.Created);                                                                  // key reused
        IntegrationManifest manifest = service.Manifest("opencode")!;
        Assert.Single(manifest.Files);
        Assert.Equal(original, File.ReadAllText(manifest.Files[0].BackupPath!));                        // backup = pre-connect file
        Assert.Single(Directory.GetFiles(Path.Combine(_store.BackupRoot, "opencode")));
        Assert.DoesNotContain("parux-sk-test-1", File.ReadAllText(Path.Combine(_store.Root, "opencode.manifest.json")));
        Assert.Equal(ClientIntegrationStatus.Connected, (await service.DetectAsync(client, CancellationToken.None)).Status);

        IntegrationOutcome disconnected = await service.DisconnectAsync(client, CancellationToken.None);
        Assert.True(disconnected.Success, disconnected.Message);
        JsonObject after = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        Assert.Null(after["provider"]!["parux"]);
        Assert.Equal("local/qwen", after["model"]!.GetValue<string>());
        Assert.Contains("key-1", _keys.Revoked);
        Assert.Null(service.Manifest("opencode"));
        Assert.Equal(ClientIntegrationStatus.UsingAnotherProvider, (await service.DetectAsync(client, CancellationToken.None)).Status);
        Assert.True((await service.DisconnectAsync(client, CancellationToken.None)).Success);            // idempotent
    }

    [Fact]
    public async Task Restore_puts_back_the_exact_pre_connect_file()
    {
        string config = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json");
        const string original = "{ \"model\": \"local/qwen\",   \"theme\": \"x\" }";
        File.WriteAllText(config, original);
        var client = new OpenCodeIntegration(_environment, () => new[] { FakeClient("PARUX_OPENCODE_OK", "parux_opencode_test.txt") });
        ClientIntegrationService service = Service(client);
        Assert.True((await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None)).Success);

        IntegrationOutcome restored = await service.RestoreAsync(client, CancellationToken.None);
        Assert.True(restored.Success, restored.Message);
        Assert.Equal(original, File.ReadAllText(config));
        Assert.Null(service.Manifest("opencode"));
    }

    [Fact]
    public async Task OpenCode_file_created_by_PaRuX_is_removed_on_disconnect()
    {
        string config = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json");
        var client = new OpenCodeIntegration(_environment, () => new[] { FakeClient("PARUX_OPENCODE_OK", "parux_opencode_test.txt") });
        ClientIntegrationService service = Service(client);
        Assert.True((await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None)).Success);
        Assert.True(File.Exists(config));
        Assert.True((await service.DisconnectAsync(client, CancellationToken.None)).Success);
        Assert.False(File.Exists(config));
    }

    [Fact]
    public async Task Codex_connect_never_changes_config_toml_and_refuses_a_foreign_profile()
    {
        string main = Path.Combine(_environment.CodexHome, "config.toml");
        const string mainText = "model = \"gpt-5\"\n[mcp_servers.x]\ncommand = \"y\"\n";
        File.WriteAllText(main, mainText);
        var client = new CodexIntegration(_environment, () => new[] { FakeClient("PARUX_CODEX_OK", "parux_codex_test.txt") });
        ClientIntegrationService service = Service(client);

        IntegrationOutcome connected = await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None);
        Assert.True(connected.Success, connected.Message);
        Assert.Equal(mainText, File.ReadAllText(main));
        Assert.True(CodexConfigEditor.IsManaged(File.ReadAllText(client.ProfilePath)));
        Assert.True((await service.DisconnectAsync(client, CancellationToken.None)).Success);
        Assert.False(File.Exists(client.ProfilePath));
        Assert.Equal(mainText, File.ReadAllText(main));

        File.WriteAllText(client.ProfilePath, "model = \"mine\"\n");
        IntegrationOutcome refused = await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None);
        Assert.False(refused.Success);
        Assert.Equal(ClientIntegrationStatus.ConfigurationError, refused.Status);
        Assert.Equal("model = \"mine\"\n", File.ReadAllText(client.ProfilePath));
        Assert.Contains("key-2", _keys.Revoked);                                                         // no orphan key
        Assert.Null(service.Manifest("codex"));
    }

    [Fact]
    public async Task Claude_profile_is_isolated_and_removed_on_disconnect()
    {
        string userSettings = Path.Combine(_environment.UserHome, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userSettings)!);
        File.WriteAllText(userSettings, "{ \"theme\": \"dark\" }");
        var client = new ClaudeCodeIntegration(_environment, () => new[] { FakeClient("PARUX_CLAUDE_OK", "parux_claude_test.txt") });
        ClientIntegrationService service = Service(client);

        Assert.True((await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None)).Success);
        string profile = ClaudeCodeIntegration.ProfileDirectory(_store);
        string settings = File.ReadAllText(Path.Combine(profile, "settings.json"));
        Assert.Contains("http://127.0.0.1:11435", settings);
        Assert.DoesNotContain("parux-sk-", settings);                                                     // key only injected at launch
        Assert.Equal("{ \"theme\": \"dark\" }", File.ReadAllText(userSettings));

        Assert.True((await service.DisconnectAsync(client, CancellationToken.None)).Success);
        Assert.False(Directory.Exists(profile));
        Assert.Equal("{ \"theme\": \"dark\" }", File.ReadAllText(userSettings));
    }

    [Fact]
    public async Task Claude_disconnect_succeeds_while_an_open_session_holds_profile_files()
    {
        var client = new ClaudeCodeIntegration(_environment, () => new[] { FakeClient("PARUX_CLAUDE_OK", "parux_claude_test.txt") });
        ClientIntegrationService service = Service(client);
        Assert.True((await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None)).Success);
        string profile = ClaudeCodeIntegration.ProfileDirectory(_store);
        string session = Path.Combine(profile, "sessions", "live.key");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        using (new FileStream(session, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            IntegrationOutcome outcome = await service.DisconnectAsync(client, CancellationToken.None);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Contains("still in use", outcome.Message);
        }
        Assert.False(File.Exists(Path.Combine(profile, "settings.json")));                             // gateway config is gone
        Assert.Null(service.Manifest("claude-code"));
        Assert.Contains("key-1", _keys.Revoked);
    }

    [Fact]
    public void Client_processes_never_resolve_programs_from_the_project_folder()
    {
        var shim = IntegrationProcess.Headless("C:\\npm\\opencode.cmd", _root, new[] { "run" });
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), shim.FileName);                // not a bare "cmd.exe"
        Assert.Equal("1", shim.Environment["NoDefaultCurrentDirectoryInExePath"]);
        var console = IntegrationProcess.Console("C:\\npm\\opencode.cmd", _root, new[] { "--model", "parux/parux-auto" });
        Assert.Equal("1", console.Environment["NoDefaultCurrentDirectoryInExePath"]);

        var client = new OpenCodeIntegration(_environment, () => Array.Empty<string>());
        var test = client.CreateTest("C:\\npm\\opencode.cmd", Endpoint, _root, "p", _store);
        Assert.Contains("\"bash\":\"deny\"", test.Environment["OPENCODE_CONFIG_CONTENT"]);                // test run cannot execute commands
    }

    [Fact]
    public void External_clients_inherit_only_the_allowlisted_environment_and_their_own_gateway_key()
    {
        // CF-07: a cloud key in PaRuX's environment must not reach a third-party agent's shell tool, and the
        // gateway key is handed only to the client it was issued for.
        const string secretName = "PARUX_SCRUB_TEST_CLOUD_TOKEN";
        string? previous = Environment.GetEnvironmentVariable(secretName);
        try
        {
            Environment.SetEnvironmentVariable(secretName, "must-not-reach-the-client");
            var claude = new ClaudeCodeIntegration(_environment, () => Array.Empty<string>());
            var codex = new CodexIntegration(_environment, () => Array.Empty<string>());

            var headless = IntegrationProcess.Headless("C:\\tools\\client.exe", _root, new[] { "run" });
            var claudeLaunch = claude.CreateLaunch("C:\\claude\\claude.exe", Endpoint, _root, _store);
            var codexLaunch = codex.CreateLaunch("C:\\codex\\codex.exe", Endpoint, _root, _store);

            Assert.False(headless.Environment.ContainsKey(secretName));
            Assert.False(claudeLaunch.Environment.ContainsKey(secretName));
            Assert.False(codexLaunch.Environment.ContainsKey(secretName));
            Assert.Equal(Endpoint.ApiKey, claudeLaunch.Environment["ANTHROPIC_AUTH_TOKEN"]);
            Assert.False(codexLaunch.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
            Assert.False(headless.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
            string? systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
            if (systemRoot != null) Assert.Equal(systemRoot, headless.Environment["SYSTEMROOT"]);
        }
        finally { Environment.SetEnvironmentVariable(secretName, previous); }
    }

    [Fact]
    public async Task OpenCode_disconnect_refuses_when_the_user_added_comments_after_connect()
    {
        string config = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json");
        File.WriteAllText(config, "{ \"model\": \"local/qwen\" }");
        var client = new OpenCodeIntegration(_environment, () => new[] { FakeClient("PARUX_OPENCODE_OK", "parux_opencode_test.txt") });
        ClientIntegrationService service = Service(client);
        Assert.True((await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None)).Success);
        File.WriteAllText(config, "// my note\n" + File.ReadAllText(config));

        IntegrationOutcome outcome = await service.DisconnectAsync(client, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("comments", outcome.Message);
        Assert.NotNull(service.Manifest("opencode"));                                                     // still recorded, key not revoked
        Assert.Empty(_keys.Revoked);
        Assert.True((await service.RestoreAsync(client, CancellationToken.None)).Success);                // Restore still works
        Assert.Equal("{ \"model\": \"local/qwen\" }", File.ReadAllText(config));
    }

    [Fact]
    public async Task OpenCode_symlinked_config_stays_a_link_through_connect_and_restore()
    {
        string dotfiles = Path.Combine(_root, "dotfiles");
        Directory.CreateDirectory(dotfiles);
        string real = Path.Combine(dotfiles, "opencode.json");
        File.WriteAllText(real, "{ \"model\": \"local/qwen\" }");
        string link = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json");
        try { File.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }         // symlinks need Developer Mode on Windows

        var client = new OpenCodeIntegration(_environment, () => new[] { FakeClient("PARUX_OPENCODE_OK", "parux_opencode_test.txt") });
        ClientIntegrationService service = Service(client);
        Assert.True((await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None)).Success);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Contains("\"parux\"", File.ReadAllText(real));
        Assert.True((await service.RestoreAsync(client, CancellationToken.None)).Success);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("{ \"model\": \"local/qwen\" }", File.ReadAllText(real));
    }

    [Fact]
    public async Task Unreachable_gateway_or_failed_test_is_reported_honestly()
    {
        var client = new OpenCodeIntegration(_environment, () => new[] { FakeClient("WRONG", "parux_opencode_test.txt") });
        var offline = new ClientIntegrationService(_store, _keys, new IClientIntegration[] { client }, new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable)));
        IntegrationOutcome down = await offline.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None);
        Assert.False(down.Success);
        Assert.False(File.Exists(Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json")));
        Assert.Equal(0, _keys.Created);

        ClientIntegrationService service = Service(client);
        IntegrationOutcome failed = await service.ConnectAsync(client, Endpoint.BaseUrl, "parux-auto", null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Contains("instead of PARUX_OPENCODE_OK", failed.Message);
        Assert.False(service.Manifest("opencode")!.LastTestPassed);

        var missing = new OpenCodeIntegration(_environment, () => Array.Empty<string>());
        Assert.Equal(ClientIntegrationStatus.NotInstalled, (await Service(missing).DetectAsync(missing, CancellationToken.None)).Status);
    }

    private ClientIntegrationService Service(IClientIntegration client) =>
        new(_store, _keys, new[] { client }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

    /// <summary>A stand-in CLI: reports a version, and in "test" mode writes the marker file into its working directory.</summary>
    private string FakeClient(string content, string fileName)
    {
        string bin = Path.Combine(_root, "bin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        string path = Path.Combine(bin, "client.cmd");
        File.WriteAllText(path, "@echo off\r\nif \"%~1\"==\"--version\" (echo fake-cli 9.9.9& exit /b 0)\r\n>" + fileName + " echo " + content + "\r\n");
        return path;
    }

    private sealed class FakeKeys : IGatewayKeyIssuer
    {
        private readonly HashSet<string> _live = new();
        public int Created { get; private set; }
        public List<string> Revoked { get; } = new();

        public Task<(string Id, string Key)> CreateAsync(string name, CancellationToken ct)
        {
            Created++;
            string id = "key-" + Created;
            _live.Add(id);
            return Task.FromResult((id, "parux-sk-test-" + Created));
        }

        public Task<bool> ExistsAsync(string id, CancellationToken ct) => Task.FromResult(_live.Contains(id));

        public Task RevokeAsync(string id, CancellationToken ct)
        {
            _live.Remove(id);
            Revoked.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }
}
