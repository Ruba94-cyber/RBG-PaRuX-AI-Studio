using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Services.Integrations
{
    /// <summary>
    /// Codex: PaRuX owns one standalone profile file, CODEX_HOME\parux.config.toml, selected with
    /// "codex --profile parux". Verified on Codex CLI 0.158: profiles are separate
    /// "&lt;name&gt;.config.toml" files and the legacy [profiles.x] table in config.toml is refused
    /// alongside --profile. The user's config.toml is therefore never edited and the OpenAI provider
    /// is never overridden.
    /// </summary>
    public static class CodexConfigEditor
    {
        public const string ProfileName = "parux";
        public const string ProfileFileName = "parux.config.toml";
        public const string OwnershipMarker = "# Created and managed by PaRuX (PaRuX Disconnect removes this file).";
        private static readonly Regex LegacyParuxProfile = new(
            @"^\s*(\[\s*profiles\.parux\s*\]|profile\s*=\s*""parux"")\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static string BuildProfile(GatewayEndpoint endpoint) =>
            OwnershipMarker + "\n"
            + "model_provider = \"parux\"\n"
            + "model = " + TomlString(endpoint.Model) + "\n"
            + "\n"
            + "[model_providers.parux]\n"
            + "name = \"PaRuX Local\"\n"
            + "base_url = " + TomlString(endpoint.OpenAiBaseUrl) + "\n"
            + "wire_api = \"responses\"\n"
            + "http_headers = { \"Authorization\" = " + TomlString("Bearer " + endpoint.ApiKey) + " }\n";

        public static bool IsManaged(string? profileText) =>
            profileText != null && profileText.StartsWith(OwnershipMarker, StringComparison.Ordinal);

        /// <summary>
        /// New profile text, or null with an error when a "parux" profile exists that PaRuX did not
        /// create, or when config.toml still has a legacy parux profile Codex would refuse.
        /// Idempotent: PaRuX's own profile is refreshed in place.
        /// </summary>
        public static string? Apply(string? existingProfile, string? mainConfig, GatewayEndpoint endpoint, out string? error)
        {
            if (existingProfile != null && !IsManaged(existingProfile))
            {
                error = ProfileFileName + " already exists and was not created by PaRuX; it will not be overwritten.";
                return null;
            }
            if (mainConfig != null && LegacyParuxProfile.IsMatch(mainConfig))
            {
                error = "config.toml contains a legacy [profiles.parux] or profile = \"parux\"; Codex refuses --profile parux while it is there.";
                return null;
            }
            error = null;
            return BuildProfile(endpoint);
        }

        private static string TomlString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => c < 0x20 ? "\\u" + ((int)c).ToString("X4") : c.ToString()
                });
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>
    /// OpenCode opencode.json: PaRuX adds (or refreshes) only provider "parux". Other providers and
    /// the user's default model stay as they are. Files with comments (JSONC) are not rewritten,
    /// because a JSON round-trip would silently drop the user's comments.
    /// </summary>
    public static class OpenCodeConfigEditor
    {
        public const string ProviderId = "parux";
        public const string MarkerOption = "parux-managed";

        public static JsonObject BuildProvider(GatewayEndpoint endpoint) => new()
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = "PaRuX Local",
            ["options"] = new JsonObject
            {
                ["baseURL"] = endpoint.OpenAiBaseUrl,
                ["apiKey"] = endpoint.ApiKey,
                ["headers"] = new JsonObject { ["X-PaRuX-Client"] = "opencode" }
            },
            ["models"] = new JsonObject
            {
                ["parux-auto"] = new JsonObject { ["name"] = "PaRuX Auto", ["tool_call"] = true },
                ["parux-code"] = new JsonObject { ["name"] = "PaRuX Code", ["tool_call"] = true },
                ["parux-fast"] = new JsonObject { ["name"] = "PaRuX Fast", ["tool_call"] = true },
                ["parux-reasoning"] = new JsonObject { ["name"] = "PaRuX Reasoning", ["tool_call"] = true }
            }
        };

        public static bool HasComments(string text) =>
            Regex.IsMatch(StripStrings(text), @"//|/\*", RegexOptions.CultureInvariant);

        public static string? Apply(string? existing, GatewayEndpoint endpoint, out string? error)
        {
            JsonObject root;
            if (string.IsNullOrWhiteSpace(existing))
            {
                root = new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
            }
            else
            {
                if (HasComments(existing))
                {
                    error = "opencode.json contains comments; PaRuX will not rewrite it (use Launch OpenCode with PaRuX instead).";
                    return null;
                }
                try { root = JsonNode.Parse(existing) as JsonObject ?? throw new JsonException("not an object"); }
                catch (JsonException ex) { error = "opencode.json is not valid JSON: " + ex.Message; return null; }
            }
            JsonObject providers = root["provider"] as JsonObject ?? new JsonObject();
            if (providers[ProviderId] is JsonObject foreign && !IsManaged(foreign))
            {
                error = "opencode.json already defines its own provider \"parux\"; PaRuX will not overwrite it.";
                return null;
            }
            JsonObject provider = BuildProvider(endpoint);
            ((JsonObject)provider["options"]!)["headers"]!.AsObject()[MarkerOption] = "true";
            providers[ProviderId] = provider;
            root["provider"] = providers;
            error = null;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        }

        public static string? Remove(string? existing, out bool changed)
        {
            changed = false;
            if (string.IsNullOrWhiteSpace(existing) || HasComments(existing)) return existing;
            JsonObject? root;
            try { root = JsonNode.Parse(existing) as JsonObject; }
            catch (JsonException) { return existing; }
            if (root?["provider"] is not JsonObject providers || providers[ProviderId] is not JsonObject provider || !IsManaged(provider))
                return existing;
            providers.Remove(ProviderId);
            if (providers.Count == 0) root.Remove("provider");
            foreach (string key in new[] { "model", "small_model" })
                if (root[key] is JsonValue value && value.TryGetValue(out string? model) && model.StartsWith(ProviderId + "/", StringComparison.Ordinal))
                    root.Remove(key);
            changed = true;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        }

        public static bool IsManaged(JsonObject provider) =>
            provider["options"]?["headers"]?[MarkerOption]?.GetValue<string>() == "true";

        /// <summary>The provider OpenCode currently uses by default ("provider/model"), or null.</summary>
        public static string? DefaultProvider(string? existing)
        {
            if (string.IsNullOrWhiteSpace(existing) || HasComments(existing)) return null;
            try
            {
                string? model = (JsonNode.Parse(existing) as JsonObject)?["model"]?.GetValue<string>();
                int slash = model?.IndexOf('/') ?? -1;
                return slash > 0 ? model![..slash] : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
        }

        private static string StripStrings(string text) =>
            Regex.Replace(text, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"", RegexOptions.CultureInvariant);
    }
}
