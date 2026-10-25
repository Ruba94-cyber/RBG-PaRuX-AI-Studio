using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// A CDP Page.navigate reply can contain an errorText even when the
    /// protocol command itself succeeded. A post-navigation redirect blocked
    /// by Fetch is likewise a failed browsing action, not a successful page.
    /// </summary>
    public static class BrowserDevToolsNavigationResultPolicy
    {
        public static bool IsSuccessful(JsonElement pageNavigateReply, bool interceptedBlockedRequest)
        {
            if (interceptedBlockedRequest || pageNavigateReply.ValueKind != JsonValueKind.Object)
                return false;
            if (pageNavigateReply.TryGetProperty("errorText", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
                return false;
            if (pageNavigateReply.TryGetProperty("isDownload", out JsonElement download)
                && download.ValueKind == JsonValueKind.True)
                return false;
            return true;
        }
    }
}
