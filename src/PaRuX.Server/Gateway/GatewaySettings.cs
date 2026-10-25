using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PaRuX.Server.Gateway;

/// <summary>
/// The Universal Gateway listener: a loopback-only port that serves exactly the model APIs
/// (Anthropic Messages, OpenAI Responses, OpenAI Chat Completions, models) and nothing of
/// PaRuX's control plane. External agents (Claude Code, Codex, OpenCode, generic clients) get a
/// clean base URL; the Core port keeps serving the desktop app. LAN exposure is never automatic.
/// </summary>
public sealed class GatewaySettings
{
    public const int DefaultPort = 11435;

    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = DefaultPort;
    /// <summary>Model id used for vendor ids (claude-*, gpt-*) sent by clients: parux-auto by default.</summary>
    public string VendorModelDefault { get; set; } = GatewayModelAliases.Auto;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string FilePath(string stateDirectory) => Path.Combine(stateDirectory, "gateway.json");

    public static GatewaySettings Load(string stateDirectory)
    {
        try
        {
            string path = FilePath(stateDirectory);
            if (!File.Exists(path)) return new GatewaySettings();
            GatewaySettings? loaded = JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllText(path), JsonOptions);
            return loaded == null ? new GatewaySettings() : loaded.Normalized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new GatewaySettings();
        }
    }

    public void Save(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = FilePath(stateDirectory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Normalized(), JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public GatewaySettings Normalized()
    {
        if (Port is < 1024 or > 65535 || Port == 51123) Port = DefaultPort;
        if (GatewayModelAliases.Resolve(VendorModelDefault, GatewayModelAliases.Auto) == null
            || GatewayModelAliases.IsVendorModelId(VendorModelDefault))
            VendorModelDefault = GatewayModelAliases.Auto;
        return this;
    }

    /// <summary>True when nothing else is listening on the loopback port, so Kestrel can bind it.</summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .All(endpoint => endpoint.Port != port);
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Paths reachable on the gateway port. Everything else, including the whole /api control plane
    /// and the legacy Ollama emulation, is answered with 404 so a client key or a mis-pointed tool
    /// can never reach PaRuX's settings, folders, LAN or Computer Use surfaces through this port.
    /// </summary>
    public static bool IsGatewayPath(PathString path) =>
        path.StartsWithSegments("/v1") || path == "/health";
}
