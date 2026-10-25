using System;
using System.Collections.Generic;

namespace PaRuX.Services.Integrations
{
    /// <summary>User-visible state of an external AI client integration.</summary>
    public enum ClientIntegrationStatus
    {
        NotInstalled,
        Detected,
        Available,
        Connected,
        UsingAnotherProvider,
        ConfigurationError
    }

    public static class ClientIntegrationStatusText
    {
        public static string Label(ClientIntegrationStatus status) => status switch
        {
            ClientIntegrationStatus.NotInstalled => "Not installed",
            ClientIntegrationStatus.Detected => "Detected",
            ClientIntegrationStatus.Available => "Available",
            ClientIntegrationStatus.Connected => "Connected to PaRuX",
            ClientIntegrationStatus.UsingAnotherProvider => "Using another provider",
            ClientIntegrationStatus.ConfigurationError => "Configuration error",
            _ => status.ToString()
        };
    }

    /// <summary>What detection found on this PC. Never throws for a missing client.</summary>
    public sealed record ClientDetection(
        string ClientId,
        string DisplayName,
        bool Installed,
        string? ExecutablePath,
        string? Version,
        ClientIntegrationStatus Status,
        string Detail);

    /// <summary>The PaRuX gateway coordinates a client is configured with.</summary>
    public sealed record GatewayEndpoint(string BaseUrl, string ApiKey, string Model)
    {
        /// <summary>OpenAI-compatible base (".../v1").</summary>
        public string OpenAiBaseUrl => BaseUrl.TrimEnd('/') + "/v1";
    }

    public sealed record IntegrationOutcome(bool Success, ClientIntegrationStatus Status, string Message);

    /// <summary>
    /// Durable record of exactly what PaRuX changed for one client, so Disconnect removes only
    /// PaRuX's own changes and Restore can put the original file back.
    /// </summary>
    public sealed class IntegrationManifest
    {
        public string ClientId { get; set; } = string.Empty;
        public string? ClientVersion { get; set; }
        public DateTime ConnectedUtc { get; set; }
        public string? ApiKeyId { get; set; }
        /// <summary>DPAPI-protected (CurrentUser) API key, base64.</summary>
        public string? ProtectedApiKey { get; set; }
        public string Model { get; set; } = "parux-auto";
        public string GatewayBaseUrl { get; set; } = string.Empty;
        public List<ManagedFileChange> Files { get; set; } = new();
        public DateTime? LastTestUtc { get; set; }
        public bool? LastTestPassed { get; set; }
        public string? LastTestDetail { get; set; }
    }

    public sealed class ManagedFileChange
    {
        public string Path { get; set; } = string.Empty;
        /// <summary>Copy of the file before PaRuX's first change; null when PaRuX created the file.</summary>
        public string? BackupPath { get; set; }
        public bool CreatedByPaRuX { get; set; }
        public string Kind { get; set; } = string.Empty;
    }
}
