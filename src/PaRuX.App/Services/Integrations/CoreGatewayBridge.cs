using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services.Integrations
{
    /// <summary>Gateway state reported by PaRuX Core (GET /api/v1/gateway/status).</summary>
    public sealed class GatewayStatusInfo
    {
        public bool Listening { get; set; }
        public int Port { get; set; }
        public int ConfiguredPort { get; set; }
        public bool RestartRequired { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string VendorModelDefault { get; set; } = "parux-auto";
        public bool PromptLogging { get; set; }
        public List<GatewayModelInfo> Models { get; set; } = new();
        public GatewayRoutingInfo? LastRouting { get; set; }
    }

    public sealed class GatewayModelInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public sealed class GatewayRoutingInfo
    {
        public string RequestedModel { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? ModelFile { get; set; }
        public DateTime Utc { get; set; }
    }

    public sealed class GatewaySettingsUpdate
    {
        public bool Enabled { get; set; } = true;
        public int Port { get; set; } = 11435;
        public string VendorModelDefault { get; set; } = "parux-auto";
    }

    /// <summary>Issues integration keys through Core's desktop-only API-key routes.</summary>
    public sealed class CoreGatewayKeyIssuer : IGatewayKeyIssuer
    {
        public async Task<(string Id, string Key)> CreateAsync(string name, CancellationToken ct)
        {
            var created = await RemoteAccessClient.Current.CreateApiKeyAsync(name, ct).ConfigureAwait(false);
            return (created.Id, created.Key);
        }

        public async Task<bool> ExistsAsync(string id, CancellationToken ct) =>
            (await RemoteAccessClient.Current.ListApiKeysAsync(ct).ConfigureAwait(false)).Any(k => k.Id == id);

        public Task RevokeAsync(string id, CancellationToken ct) => RemoteAccessClient.Current.RevokeApiKeyAsync(id, ct);
    }

    public static class ClientIntegrationRuntime
    {
        private static readonly Lazy<ClientIntegrationService> Instance = new(() =>
        {
            ClientIntegrationEnvironment environment = ClientIntegrationEnvironment.Current();
            var store = new IntegrationStore(System.IO.Path.Combine(AppPaths.PaRuXRoot, "integrations"));
            return new ClientIntegrationService(store, new CoreGatewayKeyIssuer(), new IClientIntegration[]
            {
                new ClaudeCodeIntegration(environment),
                new CodexIntegration(environment),
                new OpenCodeIntegration(environment)
            });
        });

        public static ClientIntegrationService Current => Instance.Value;
    }
}
