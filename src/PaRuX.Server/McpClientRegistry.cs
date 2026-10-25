using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PaRuX.Contracts;
using PaRuX.Server.DesktopIntegration;

namespace PaRuX.Server;

public sealed record McpExternalToolSchema(
    string ServerId,
    string Name,
    string Description,
    JsonElement InputSchema);

/// <summary>
/// Manages user-configured external MCP servers (stdio-launched) and PaRuX's native read-only
/// knowledge tools, then puts their schemas in front of the chat model via real OpenAI-style tool
/// calling. External servers remain entirely user-configured. Native knowledge tools can see only
/// folders the user explicitly enabled as RAG sources and never execute a shell.
/// </summary>
public sealed class McpClientRegistry : IAsyncDisposable
{
    private const int MaxToolSchemasPerTurn = 32;
    private const int MaxToolSchemaCharactersPerTurn = 12_000;
    private const int MaxCodingAgentToolDescriptionCharacters = 2_048;
    private const int MaxCodingAgentInputSchemaCharacters = 8_192;
    private const int MaxCodingAgentArgumentsCharacters = 16_384;
    private const int MaxCodingAgentResultCharacters = 8_000;
    private static readonly TimeSpan CodingAgentToolCallTimeout = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly string _stateDirectory;
    private readonly KnowledgeBrowserTools? _knowledge;
    private readonly DesktopIntegrationTools _desktop;
    private readonly DocumentWriteTools _documents;
    private List<McpServerConfig>? _configCache;
    private readonly Dictionary<string, McpClient> _liveClients = new();

    /// <summary>How long one connection attempt is allowed to take.
    ///
    /// A stdio server is an arbitrary command - <c>npx -y some-package</c> is the common shape -
    /// and on a cold cache that command downloads a package before it says anything. Waiting for it
    /// with no bound is what made GET /api/v1/mcp/servers hang indefinitely instead of answering:
    /// the endpoint's job is to report each server's state, and "did not answer in time" is a state
    /// it can report. Generous enough for a real first launch, finite so nothing built on top of
    /// this can stall forever.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a server that just failed to connect is left alone.
    ///
    /// Without this, every chat turn re-attempts every broken server and pays
    /// <see cref="ConnectTimeout"/> again for each one, so one misconfigured entry taxes all of
    /// them. Cleared as soon as the user touches that server's configuration, so fixing a command
    /// and pressing retry works immediately rather than after a wait the user cannot see.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (DateTime UntilUtc, string Error)> _recentFailures = new(StringComparer.Ordinal);
    private readonly McpCredentialStore _credentials;
    private readonly McpOAuthCoordinator _oauth = new();

    public McpClientRegistry() : this(ServerState.StateDirectory, knowledge: null) { }

    /// <summary>Production DI chooses this constructor explicitly, so the live Core registry is
    /// always connected to RagStore and exposes native knowledge tools when enabled sources exist.
    /// The parameterless overload remains for legacy direct construction/tests.</summary>
    [ActivatorUtilitiesConstructor]
    /// <summary>
    /// Production constructor (DI). The desktop-integration and document services are the same
    /// singletons the Settings endpoints update: each service caches its state in memory, so a
    /// private copy here never saw a source connected after startup, and Chat answered "I can't
    /// see your notes" until Core restarted (found by a live functional test).
    /// </summary>
    public McpClientRegistry(
        RagStore rag,
        PaRuX.Server.DesktopIntegration.DesktopIntegrationService desktop,
        PaRuX.Server.DesktopIntegration.DocumentWriteService documents)
        : this(ServerState.StateDirectory, new KnowledgeBrowserTools(rag), desktop, documents) { }

    /// <summary>Test-only entry point (see BackupServiceTests) so backup/restore round-trip tests
    /// can point this registry at an isolated temp directory instead of the real
    /// ServerState.StateDirectory. Native knowledge tools are deliberately absent unless a test
    /// opts into the internal constructor below.</summary>
    public McpClientRegistry(string stateDirectory) : this(stateDirectory, knowledge: null) { }

    internal McpClientRegistry(string stateDirectory, KnowledgeBrowserTools? knowledge)
        : this(stateDirectory, knowledge, new DesktopIntegrationService(stateDirectory), new DocumentWriteService(stateDirectory)) { }

    internal McpClientRegistry(
        string stateDirectory, KnowledgeBrowserTools? knowledge,
        DesktopIntegrationService desktop, DocumentWriteService documents)
    {
        _stateDirectory = stateDirectory;
        _knowledge = knowledge;
        _credentials = new McpCredentialStore(stateDirectory);
        _desktop = new DesktopIntegrationTools(desktop);
        _documents = new DocumentWriteTools(documents);
    }

    private string StorePath => Path.Combine(_stateDirectory, "mcp-clients.json");

    public List<McpServerConfig> GetConfigs()
    {
        lock (_gate) { EnsureLoaded(); return new List<McpServerConfig>(_configCache!); }
    }

    /// <summary>Adds a stdio server - the original shape, unchanged for every existing caller.</summary>
    public McpServerConfig AddServer(string name, string command, string args) =>
        AddServer(new AddMcpServerRequest
        {
            Name = name,
            Transport = McpTransports.Stdio,
            Command = command,
            Args = args ?? string.Empty
        });

    /// <summary>
    /// Adds a server of either transport. HTTP servers are validated here rather than at connect
    /// time so a bad URL or a missing credential is refused while the user is looking at the form,
    /// not silently later when a chat turn wanted a tool.
    /// </summary>
    public McpServerConfig AddServer(AddMcpServerRequest request)
    {
        if (request == null) throw new InvalidDataException("A server request is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("A server needs a name.");

        string transport = (request.Transport ?? McpTransports.Stdio).Trim().ToLowerInvariant();
        var config = new McpServerConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            Transport = transport,
            AllowedTools = NormalizeToolNames(request.AllowedTools),
            Enabled = true
        };

        if (transport == McpTransports.Stdio)
        {
            if (string.IsNullOrWhiteSpace(request.Command))
                throw new InvalidDataException("A stdio server needs a launch command.");
            config.Command = request.Command.Trim();
            config.Args = request.Args?.Trim() ?? string.Empty;
        }
        else if (transport == McpTransports.Http)
        {
            if (!McpServerEndpointPolicy.TryNormalize(request.Url, out string url, out string? urlError))
                throw new InvalidDataException(urlError ?? "That MCP server URL is not usable.");
            config.Url = url;

            string authMode = (request.AuthMode ?? McpAuthModes.None).Trim().ToLowerInvariant();
            config.AuthMode = authMode switch
            {
                McpAuthModes.None or McpAuthModes.Bearer or McpAuthModes.OAuth => authMode,
                _ => throw new InvalidDataException("Unknown MCP auth mode \"" + authMode + "\".")
            };

            if (authMode == McpAuthModes.Bearer && string.IsNullOrWhiteSpace(request.Credential))
                throw new InvalidDataException("Bearer authentication needs a token.");

            config.OAuthClientId = request.OAuthClientId?.Trim() ?? string.Empty;
            config.Scopes = request.Scopes?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
                ?? new List<string>();
        }
        else
        {
            throw new InvalidDataException("Unknown MCP transport \"" + transport + "\". Use \"stdio\" or \"http\".");
        }

        lock (_gate)
        {
            EnsureLoaded();
            _configCache!.Add(config);
            Save();
        }

        if (!string.IsNullOrWhiteSpace(request.Credential))
        {
            _credentials.Set(config.Id,
                config.AuthMode == McpAuthModes.OAuth
                    ? McpCredentialStore.OAuthClientSecretKind
                    : McpCredentialStore.BearerKind,
                request.Credential);
        }
        return config;
    }

    /// <summary>
    /// Replaces the exact tool names a server may put in front of the model. Empty restores
    /// "everything it advertises". Applied on the next turn - the live connection is dropped so a
    /// narrowed list cannot be served from a cached tool list.
    /// </summary>
    public async Task SetAllowedToolsAsync(string id, IEnumerable<string>? tools, CancellationToken ct)
    {
        List<string> normalized = NormalizeToolNames(tools);
        lock (_gate)
        {
            EnsureLoaded();
            McpServerConfig? config = _configCache!.FirstOrDefault(c => c.Id == id)
                ?? throw new FileNotFoundException("No MCP server with that id.");
            config.AllowedTools = normalized;
            Save();
        }
        await DisconnectAsync(id);
    }

    private static List<string> NormalizeToolNames(IEnumerable<string>? tools) =>
        tools?.Where(t => !string.IsNullOrWhiteSpace(t))
              .Select(t => t.Trim())
              .Distinct(StringComparer.Ordinal)
              .ToList()
        ?? new List<string>();

    /// <summary>
    /// Whether this server is permitted to expose a tool. Enforced when schemas are collected and
    /// again at call time: a server that starts advertising a new tool after the user approved a
    /// list must not be able to reach the model through the second path.
    /// </summary>
    private static bool ServerAllowsTool(McpServerConfig config, string toolName) =>
        config.AllowedTools.Count == 0
        || config.AllowedTools.Contains(toolName, StringComparer.Ordinal);

    public async Task RemoveServerAsync(string id, CancellationToken ct)
    {
        await DisconnectAsync(id);
        // A removed server must not leave its token on disk.
        _credentials.Forget(id);
        _oauth.Forget(id);
        lock (_gate)
        {
            EnsureLoaded();
            _configCache!.RemoveAll(c => c.Id == id);
            Save();
        }
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        if (!enabled) await DisconnectAsync(id);
        lock (_gate)
        {
            EnsureLoaded();
            McpServerConfig? config = _configCache!.FirstOrDefault(c => c.Id == id);
            if (config == null) throw new InvalidDataException("Unknown MCP server id.");
            config.Enabled = enabled;
            Save();
        }
    }

    /// <summary>Real connection attempt (or reuses an already-live one) plus the actual external
    /// MCP tool list. Native knowledge tools are not represented as fake external servers in
    /// Settings; they appear automatically only when enabled RAG sources exist.</summary>
    /// <summary>
    /// Every configured server's state, probing the enabled ones concurrently.
    ///
    /// Serially, the endpoint cost the sum of every server's connect time, so one slow entry
    /// delayed the report on all the healthy ones behind it. Each probe is independent - a
    /// connection and a tool list against a different process - so the whole call now costs about
    /// the slowest single server instead, bounded by ConnectTimeout.
    /// </summary>
    public async Task<List<McpServerStatus>> GetStatusesAsync(CancellationToken ct)
    {
        var results = new List<McpServerStatus>();
        var probes = new List<Task>();
        foreach (McpServerConfig config in GetConfigs())
        {
            var status = new McpServerStatus
            {
                Id = config.Id,
                Name = config.Name,
                Transport = config.Transport,
                Command = config.Command,
                Args = config.Args,
                Url = config.Url,
                AuthMode = config.AuthMode,
                AllowedTools = new List<string>(config.AllowedTools),
                IsRemote = config.Transport == McpTransports.Http && McpServerEndpointPolicy.IsRemote(config.Url),
                HasCredential = HasCredential(config),
                PendingAuthorizationUrl = _oauth.PendingAuthorizationUrl(config.Id),
                Enabled = config.Enabled
            };
            results.Add(status);
            if (!config.Enabled) continue;

            McpServerConfig probed = config;
            McpServerStatus probedStatus = status;
            probes.Add(Task.Run(async () =>
            {
                try
                {
                    McpClient client = await GetOrConnectAsync(probed, ct);
                    IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
                    probedStatus.Connected = true;
                    // Reported as the model sees it: what is actually offered, and what the allowlist
                    // is holding back. A gated tool that simply vanished from the list would look like
                    // the server had stopped offering it.
                    List<string> offered = tools.Where(t => ServerAllowsTool(probed, t.Name)).Select(t => t.Name).ToList();
                    probedStatus.ToolCount = offered.Count;
                    probedStatus.ToolNames = offered;
                    probedStatus.WithheldToolNames = tools.Select(t => t.Name).Except(offered, StringComparer.Ordinal).ToList();
                    probedStatus.PendingAuthorizationUrl = _oauth.PendingAuthorizationUrl(probed.Id);
                }
                catch (Exception ex)
                {
                    probedStatus.Connected = false;
                    probedStatus.LastError = ex.Message;
                }
            }, ct));
        }
        // Never rethrows: each probe already turned its own failure into that server's LastError,
        // and one unreachable server must not deny the caller the report on the rest.
        try { await Task.WhenAll(probes); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { }
        return results;
    }

    /// <summary>OpenAI-format tool definitions for native knowledge tools plus every allowed tool
    /// across enabled, reachable MCP servers. Skill allowlists are applied before prompt-aware
    /// McpToolSchemaRouter narrowing in ContextEngineService.</summary>
    public async Task<JsonArray> GetToolSchemasAsync(
        CancellationToken ct, IReadOnlyCollection<string>? allowedTools = null)
    {
        var schemas = new JsonArray();
        int schemaCharacters = 2; // serialized array brackets
        ct.ThrowIfCancellationRequested();
        if (allowedTools is { Count: 0 }) return schemas;

        bool TryAddSchema(JsonNode schema)
        {
            int length = schema.ToJsonString().Length + (schemas.Count > 0 ? 1 : 0);
            if (schemas.Count >= MaxToolSchemasPerTurn) return false;
            if (schemaCharacters + length > MaxToolSchemaCharactersPerTurn) return true;
            schemas.Add(schema.DeepClone());
            schemaCharacters += length;
            return true;
        }

        // Desktop integrations come first: when someone has connected their notes or mail, a
        // question about them is almost always the reason they asked, and the per-turn schema
        // budget is finite.
        foreach (JsonNode? schema in _desktop.GetToolSchemas(allowedTools))
        {
            if (schema == null) continue;
            if (!TryAddSchema(schema)) return schemas;
        }

        // Always offered, unlike the desktop reading tools: writing goes to PaRuX's own output
        // folder, creates a new file every time and can destroy nothing, so there is no access for
        // the user to have granted first.
        foreach (JsonNode? schema in _documents.GetToolSchemas(allowedTools))
        {
            if (schema == null) continue;
            if (!TryAddSchema(schema)) return schemas;
        }

        if (_knowledge != null)
        {
            foreach (JsonNode? schema in _knowledge.GetToolSchemas(allowedTools))
            {
                if (schema == null) continue;
                if (!TryAddSchema(schema)) return schemas;
            }
        }

        foreach (McpServerConfig config in GetConfigs())
        {
            if (!config.Enabled) continue;
            try
            {
                McpClient client = await GetOrConnectAsync(config, ct);
                IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
                foreach (McpClientTool tool in tools)
                {
                    if (!ServerAllowsTool(config, tool.Name)) continue;
                    if (!ContextEngineService.ToolNameAllowed(tool.Name, allowedTools)) continue;
                    JsonNode? parameters = JsonNode.Parse(tool.JsonSchema.GetRawText());
                    var schema = new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description ?? string.Empty,
                            ["parameters"] = parameters
                        }
                    };
                    if (!TryAddSchema(schema)) return schemas;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Best-effort: this server just contributes no tools this turn.
            }
        }
        return schemas;
    }

    /// <summary>
    /// Returns only configured external tools with their owning server id. Coding-agent routing
    /// must retain this identity; the chat route's bare-name lookup can be ambiguous when two
    /// servers advertise the same tool name.
    /// </summary>
    public async Task<IReadOnlyList<McpExternalToolSchema>> GetCodingAgentToolSchemasAsync(
        CancellationToken ct, string? serverId = null)
    {
        var schemas = new List<McpExternalToolSchema>(MaxToolSchemasPerTurn);
        int schemaCharacters = 2;

        foreach (McpServerConfig config in GetConfigs())
        {
            ct.ThrowIfCancellationRequested();
            if (!config.Enabled) continue;
            if (!string.IsNullOrWhiteSpace(serverId)
                && !string.Equals(config.Id, serverId, StringComparison.Ordinal))
                continue;

            try
            {
                McpClient client = await GetOrConnectAsync(config, ct).ConfigureAwait(false);
                IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                foreach (McpClientTool tool in tools)
                {
                    if (!ServerAllowsTool(config, tool.Name)
                        || string.IsNullOrWhiteSpace(tool.Name)
                        || tool.Name.Length > 128
                        || tool.Name.Any(char.IsControl))
                        continue;

                    string description = tool.Description ?? string.Empty;
                    string rawSchema = tool.JsonSchema.GetRawText();
                    if (description.Length > MaxCodingAgentToolDescriptionCharacters
                        || rawSchema.Length > MaxCodingAgentInputSchemaCharacters)
                        continue;

                    JsonElement inputSchema;
                    try
                    {
                        using JsonDocument schemaDocument = JsonDocument.Parse(rawSchema);
                        if (schemaDocument.RootElement.ValueKind != JsonValueKind.Object) continue;
                        inputSchema = schemaDocument.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    var candidate = new McpExternalToolSchema(config.Id, tool.Name, description, inputSchema);
                    int length = JsonSerializer.Serialize(candidate).Length + (schemas.Count > 0 ? 1 : 0);
                    if (schemas.Count >= MaxToolSchemasPerTurn
                        || schemaCharacters + length > MaxToolSchemaCharactersPerTurn)
                        return schemas;

                    schemas.Add(candidate);
                    schemaCharacters += length;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // An unavailable server contributes no coding-agent tools this time.
            }
        }

        return schemas;
    }

    /// <summary>Calls one exact, currently enabled server/tool pair after rechecking its current
    /// allowlist. Arguments and results are bounded; telemetry excludes both payloads.</summary>
    public async Task<string> CallExternalToolAsync(
        string serverId, string toolName, string argumentsJson, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        bool success = false;
        string outcome = "exception";
        try
        {
            if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > 64 || serverId.Any(char.IsControl))
                throw new InvalidDataException("An MCP server id is required.");
            if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > 128 || toolName.Any(char.IsControl))
                throw new InvalidDataException("An exact MCP tool name is required.");
            if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Length > MaxCodingAgentArgumentsCharacters)
                throw new InvalidDataException("MCP tool arguments are empty or exceed the request limit.");

            using JsonDocument argumentsDocument = JsonDocument.Parse(argumentsJson);
            if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("MCP tool arguments must be a JSON object.");

            McpServerConfig? config = GetConfigs().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
            if (config == null) throw new FileNotFoundException("No MCP server with that id.");
            if (!config.Enabled || !ServerAllowsTool(config, toolName))
                throw new UnauthorizedAccessException("That MCP server or tool is not enabled for the coding agent.");

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsonProperty property in argumentsDocument.RootElement.EnumerateObject())
                arguments[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CodingAgentToolCallTimeout);
            McpClient client = await GetOrConnectAsync(config, timeout.Token).ConfigureAwait(false);
            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            List<McpClientTool> matches = tools.Where(candidate =>
                string.Equals(candidate.Name, toolName, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count != 1)
                throw new InvalidDataException(matches.Count == 0
                    ? "That exact MCP tool is no longer advertised by the selected server."
                    : "The selected server advertises the exact MCP tool name more than once.");

            McpServerConfig? current = GetConfigs().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
            if (current == null || !current.Enabled || !ServerAllowsTool(current, toolName))
                throw new UnauthorizedAccessException("That MCP server or tool is no longer enabled for the coding agent.");

            CallToolResult result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            if (text.Length == 0) text = "(tool returned no text content)";
            if (text.Length > MaxCodingAgentResultCharacters)
                text = text[..MaxCodingAgentResultCharacters] + "\n[external tool result truncated]";

            if (result.IsError == true)
            {
                outcome = "tool-error";
                return "Tool reported an error: " + text;
            }

            success = true;
            outcome = "success";
            return text;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            McpToolUseTelemetry.RecordExecution(
                toolName,
                1,
                success,
                outcome,
                false,
                (long)Math.Max(0d, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
    }

    /// <summary>Calls a native PaRuX knowledge tool or finds whichever connected external server
    /// owns the requested MCP name. Skill permission checks happen before either path. Execution
    /// telemetry records only the tool name, bounded outcome metadata and latency; arguments and
    /// results are never retained.</summary>
    public async Task<string> CallToolAsync(
        string toolName, string argumentsJson, CancellationToken ct,
        IReadOnlyCollection<string>? allowedTools = null)
    {
        long started = Stopwatch.GetTimestamp();
        bool success = false;
        bool argumentParseFallback = false;
        string outcome = "exception";
        int advertisedToolCount = allowedTools?.Count ?? 0;
        try
        {
            if (!ContextEngineService.ToolNameAllowed(toolName, allowedTools))
            {
                outcome = "permission-denied";
                throw new UnauthorizedAccessException("The selected skill does not allow MCP tool \"" + toolName + "\".");
            }
            ct.ThrowIfCancellationRequested();

            if (DocumentWriteTools.IsBuiltInTool(toolName))
            {
                string documentResult = await _documents.CallAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
                success = !documentResult.StartsWith("Document tool failed:", StringComparison.OrdinalIgnoreCase);
                outcome = success ? "success" : "tool-error";
                return documentResult;
            }

            if (DesktopIntegrationTools.IsBuiltInTool(toolName))
            {
                string desktopResult = await _desktop.CallAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
                success = !desktopResult.StartsWith("Desktop tool failed:", StringComparison.OrdinalIgnoreCase);
                outcome = success ? "success" : "tool-error";
                return desktopResult;
            }

            if (_knowledge != null && KnowledgeBrowserTools.IsBuiltInTool(toolName))
            {
                string knowledgeResult = await _knowledge.CallAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
                success = !knowledgeResult.StartsWith("Knowledge tool failed:", StringComparison.OrdinalIgnoreCase);
                outcome = success ? "success" : "tool-error";
                return knowledgeResult;
            }

            Dictionary<string, object?> arguments = new();
            try
            {
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    using JsonDocument doc = JsonDocument.Parse(argumentsJson);
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                        arguments[prop.Name] = JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
                }
            }
            catch (JsonException)
            {
                // Keep the historical fail-soft behavior, but make the repair burden visible.
                argumentParseFallback = true;
            }

            foreach (McpServerConfig config in GetConfigs())
            {
                if (!config.Enabled) continue;
                McpClient client;
                try { client = await GetOrConnectAsync(config, ct); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                IList<McpClientTool> tools;
                try { tools = await client.ListToolsAsync(cancellationToken: ct); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                if (!tools.Any(t => t.Name == toolName)) continue;
                // Checked again here, not only where schemas were built: a server that begins
                // advertising a tool after the user approved a list must not reach the model
                // through a call that was resolved by name.
                if (!ServerAllowsTool(config, toolName)) continue;

                CallToolResult result = await client.CallToolAsync(toolName, arguments, cancellationToken: ct);
                string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                if (result.IsError == true)
                {
                    outcome = "tool-error";
                    return "Tool reported an error: " + (text.Length > 0 ? text : "(no details)");
                }

                success = true;
                outcome = argumentParseFallback ? "success-with-argument-fallback" : "success";
                return text.Length > 0 ? text : "(tool returned no text content)";
            }

            outcome = "not-found";
            throw new InvalidOperationException("No connected MCP server exposes a tool named \"" + toolName + "\".");
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            McpToolUseTelemetry.RecordExecution(
                toolName,
                advertisedToolCount,
                success,
                outcome,
                argumentParseFallback,
                (long)Math.Max(0d, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
    }

    private async Task<McpClient> GetOrConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        McpClient? existing;
        lock (_gate) { _liveClients.TryGetValue(config.Id, out existing); }
        if (existing != null) return existing;

        lock (_gate)
        {
            if (_recentFailures.TryGetValue(config.Id, out (DateTime UntilUtc, string Error) failure))
            {
                if (failure.UntilUtc > DateTime.UtcNow) throw new InvalidOperationException(failure.Error);
                _recentFailures.Remove(config.Id);
            }
        }

        IClientTransport transport = config.Transport == McpTransports.Http
            ? BuildHttpTransport(config)
            : BuildStdioTransport(config);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ConnectTimeout);
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(transport, null, null, attempt.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own deadline, not the caller's cancellation - those must stay distinguishable so
            // a user who navigated away is never reported as a broken server.
            string message = "\"" + config.Name + "\" did not respond within "
                + ConnectTimeout.TotalSeconds.ToString("0") + " seconds.";
            RememberFailure(config.Id, message);
            throw new TimeoutException(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RememberFailure(config.Id, ex.Message);
            throw;
        }

        lock (_gate) { _liveClients[config.Id] = client; _recentFailures.Remove(config.Id); }
        return client;
    }

    private void RememberFailure(string id, string error)
    {
        lock (_gate) { _recentFailures[id] = (DateTime.UtcNow + FailureBackoff, error); }
    }

    private static IClientTransport BuildStdioTransport(McpServerConfig config)
    {
        string[] args = string.IsNullOrWhiteSpace(config.Args)
            ? Array.Empty<string>()
            : config.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command,
            Arguments = args
        });
    }

    /// <summary>
    /// Streamable HTTP, falling back to the legacy SSE transport when a server only speaks that -
    /// AutoDetect is the whole reason a single configuration works against both generations of
    /// hosted MCP server without the user having to know which one they are looking at.
    ///
    /// The URL was already validated when the server was added; it is re-validated here because a
    /// stored config can be hand-edited, and "fails closed on a config someone changed by hand" is
    /// the behaviour every other stored endpoint in PaRuX has.
    /// </summary>
    private IClientTransport BuildHttpTransport(McpServerConfig config)
    {
        if (!McpServerEndpointPolicy.TryNormalize(config.Url, out string url, out string? error))
            throw new InvalidDataException(error ?? "The stored MCP server URL is not usable.");

        var options = new HttpClientTransportOptions
        {
            Name = config.Name,
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.AutoDetect
        };

        if (config.AuthMode == McpAuthModes.Bearer)
        {
            string? token = _credentials.Get(config.Id, McpCredentialStore.BearerKind);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException(
                    "This server is configured for bearer authentication but no token could be read. Enter it again.");
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        }
        else if (config.AuthMode == McpAuthModes.OAuth)
        {
            options.OAuth = new ClientOAuthOptions
            {
                RedirectUri = McpOAuthCoordinator.AllocateLoopbackRedirect(),
                ClientId = string.IsNullOrWhiteSpace(config.OAuthClientId) ? null : config.OAuthClientId,
                ClientSecret = _credentials.Get(config.Id, McpCredentialStore.OAuthClientSecretKind),
                Scopes = config.Scopes.Count > 0 ? config.Scopes : null,
                // Tokens are cached encrypted, so approving a server in a browser is something the
                // user does once rather than after every restart of Core.
                TokenCache = new McpProtectedTokenCache(_credentials, config.Id),
                AuthorizationRedirectDelegate = _oauth.CreateRedirectHandler(config.Id),
                // With no pre-registered client id, ask the authorization server to register one.
                // This is what the MCP spec expects of a client that ships to many users and cannot
                // hold a per-user registration.
                DynamicClientRegistration = string.IsNullOrWhiteSpace(config.OAuthClientId)
                    ? new DynamicClientRegistrationOptions { ClientName = "RBG PaRuX AI Studio" }
                    : null
            };
        }

        return new HttpClientTransport(options);
    }

    private bool HasCredential(McpServerConfig config) => config.AuthMode switch
    {
        McpAuthModes.Bearer => _credentials.Has(config.Id, McpCredentialStore.BearerKind),
        McpAuthModes.OAuth => _credentials.Has(config.Id, McpCredentialStore.OAuthTokensKind)
            || _credentials.Has(config.Id, McpCredentialStore.OAuthClientSecretKind),
        _ => false
    };

    private async Task DisconnectAsync(string id)
    {
        McpClient? client;
        lock (_gate)
        {
            // Whatever the caller changed, the previous failure is no longer evidence about the
            // server as it is now configured.
            _recentFailures.Remove(id);
            if (!_liveClients.TryGetValue(id, out client)) return;
            _liveClients.Remove(id);
        }
        try { await client!.DisposeAsync(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClient> clients;
        lock (_gate) { clients = _liveClients.Values.ToList(); _liveClients.Clear(); }
        foreach (McpClient client in clients)
        {
            try { await client.DisposeAsync(); } catch { }
        }
    }

    /// <summary>Live snapshot for backup - null if no server has ever been configured, matching
    /// every other backup-config method's "don't fabricate an empty entry" convention.</summary>
    public List<McpServerConfig>? ExportForBackup()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _configCache!.Count == 0 ? null : new List<McpServerConfig>(_configCache!);
        }
    }

    /// <summary>Restores mcp-clients.json from a backup. Disconnects every currently-live MCP
    /// connection first (reusing DisposeAsync's own disconnect loop) - an already-open connection
    /// was made against the pre-restore config and may point at a server the restore is about to
    /// change or remove entirely, so it must not silently keep serving tools under the old
    /// configuration. Then updates the live in-memory config cache immediately (so an
    /// already-running server reflects the restored servers without a restart) and persists to
    /// disk.</summary>
    public async Task ImportForBackupAsync(List<McpServerConfig> configs, CancellationToken ct)
    {
        List<McpClient> clients;
        lock (_gate) { clients = _liveClients.Values.ToList(); _liveClients.Clear(); }
        foreach (McpClient client in clients)
        {
            try { await client.DisposeAsync(); } catch { }
        }
        lock (_gate)
        {
            _configCache = new List<McpServerConfig>(configs);
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_configCache != null) return;
        _configCache = new List<McpServerConfig>();
        try
        {
            if (File.Exists(StorePath))
            {
                List<McpServerConfig>? loaded = JsonSerializer.Deserialize<List<McpServerConfig>>(File.ReadAllText(StorePath));
                if (loaded != null) _configCache = loaded;
            }
        }
        catch
        {
            // A corrupt store falls back to an empty list.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            string temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_configCache));
            File.Move(temp, StorePath, true);
        }
        catch
        {
            // Best-effort persistence; the in-memory cache still reflects the request for this session.
        }
    }
}
