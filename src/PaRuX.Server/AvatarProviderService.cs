using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Avatar Studio's public/private boundary, enforced structurally rather
/// than by convention: this class contains zero Duix-specific code, no
/// bundled binaries, and no default configuration. It only ever looks for a
/// provider config file that lives outside this repository entirely
/// (%LOCALAPPDATA%\PaRuX\local-providers\avatar.local.json - same tree as
/// the Training Lab/3D Studio runtimes, and still covered by .gitignore's
/// "local-providers/"/"*.local.json" patterns as a second layer of defense).
/// On a machine where that file doesn't exist - including every fresh clone
/// of the public repository - Avatar Studio reports "not configured" and
/// nothing more happens. Configuring a real engine (Duix or otherwise) is
/// entirely the operator's own local, private action; publishing this repo
/// never publishes that configuration.
/// </summary>
public sealed class AvatarProviderService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AvatarEngineService _engine;
    private readonly string _configPath;

    public AvatarProviderService(AvatarEngineService engine, string? configPath = null)
    {
        _engine = engine;
        _configPath = configPath ?? DefaultConfigPath;
    }

    private static string DefaultConfigPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "local-providers", "avatar.local.json");

    /// <summary>The installable local engine (LatentSync) takes priority over an external
    /// provider config when both a checkpoint set and a reference photo are present - it's a
    /// real, working default, not a fallback, matching how Speech/3D Studio/Training Lab don't
    /// need any external config to be useful.</summary>
    public async Task<AvatarProviderStatus> GetStatusAsync(CancellationToken ct)
    {
        AvatarEngineStatus engineStatus = await _engine.GetStatusAsync(ct);
        if (engineStatus.Installed && engineStatus.HasDefaultPhoto)
            return new AvatarProviderStatus { Configured = true, ProviderName = "latentsync (local)", Reachable = true };

        LocalProviderConfig? config = TryReadConfig();
        if (config == null || string.IsNullOrWhiteSpace(config.Endpoint))
            return new AvatarProviderStatus { Configured = false, ProviderName = null, Reachable = false };

        bool reachable = await ProbeAsync(config.Endpoint, ct);
        return new AvatarProviderStatus
        {
            Configured = true,
            ProviderName = string.IsNullOrWhiteSpace(config.ProviderName) ? "custom" : config.ProviderName,
            Reachable = reachable
        };
    }

    public async Task<AvatarGenerateResponse> GenerateAsync(AvatarGenerateRequest request, CancellationToken ct)
    {
        AvatarEngineStatus engineStatus = await _engine.GetStatusAsync(ct);
        if (engineStatus.Installed && engineStatus.HasDefaultPhoto)
        {
            byte[] videoBytes = await _engine.GenerateAsync(request.Text, ct);
            return new AvatarGenerateResponse
            {
                FileName = "avatar_" + Guid.NewGuid().ToString("N")[..8] + ".mp4",
                MediaType = "video/mp4",
                Base64Data = Convert.ToBase64String(videoBytes)
            };
        }

        LocalProviderConfig? config = TryReadConfig();
        if (config == null || string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException("No local Avatar provider is configured on this machine.");

        var payload = new JsonObject
        {
            ["text"] = request.Text,
            ["avatar_id"] = string.IsNullOrWhiteSpace(request.AvatarId) ? config.DefaultAvatarId : request.AvatarId
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, config.Endpoint.TrimEnd('/') + "/generate")
        {
            Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await Http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);

        return new AvatarGenerateResponse
        {
            FileName = "avatar_" + Guid.NewGuid().ToString("N")[..8] + ".mp4",
            MediaType = response.Content.Headers.ContentType?.MediaType ?? "video/mp4",
            Base64Data = Convert.ToBase64String(bytes)
        };
    }

    private static async Task<bool> ProbeAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(endpoint.TrimEnd('/') + "/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private LocalProviderConfig? TryReadConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            return JsonSerializer.Deserialize<LocalProviderConfig>(File.ReadAllText(_configPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class LocalProviderConfig
    {
        public string ProviderName { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string DefaultAvatarId { get; set; } = string.Empty;
    }
}
