using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>What the server's chat-export import reported. Summary is the honest one-line result.</summary>
    public sealed class ConversationImportOutcome
    {
        public bool Ok;
        public string Summary = string.Empty;
        public int Imported;
    }

    /// <summary>
    /// Posts a ChatGPT / Claude / Gemini export to the server importer, then pulls the imported
    /// conversations into local history through the normal ConversationSyncClient path.
    /// </summary>
    public static class ConversationImportClient
    {
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(5));

        public static async Task<ConversationImportOutcome> ImportAsync(byte[] fileBytes, string fileName, CancellationToken ct)
        {
            try
            {
                ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
                string url = server.Endpoint.TrimEnd('/') + "/api/v1/conversations/import?fileName=" + Uri.EscapeDataString(fileName ?? string.Empty);
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(fileBytes) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.Add("X-PaRuX-Token", server.Token);
                using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                ConversationImportOutcome outcome = Parse(body, (int)response.StatusCode);
                if (outcome.Ok && outcome.Imported > 0)
                    await ConversationSyncClient.Current.PullNewerAsync(ct).ConfigureAwait(false);
                return outcome;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("ConversationImportClient failed: " + ex.Message);
                return new ConversationImportOutcome { Summary = "Import failed: the PaRuX server could not be reached (" + ex.Message + ")." };
            }
        }

        /// <summary>Pure: turns the server reply into the text shown to the user.</summary>
        public static ConversationImportOutcome Parse(string body, int statusCode)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("summary", out JsonElement summary) && summary.GetString() is { } text)
                    return new ConversationImportOutcome
                    {
                        Ok = statusCode is >= 200 and < 300,
                        Summary = text,
                        Imported = root.TryGetProperty("imported", out JsonElement n) && n.TryGetInt32(out int i) ? i : 0
                    };
                if (root.TryGetProperty("error", out JsonElement error))
                    return new ConversationImportOutcome { Summary = "Import failed: " + error.GetString() };
            }
            catch (JsonException) { }
            return new ConversationImportOutcome { Summary = "Import failed (server returned " + statusCode + ")." };
        }
    }
}
