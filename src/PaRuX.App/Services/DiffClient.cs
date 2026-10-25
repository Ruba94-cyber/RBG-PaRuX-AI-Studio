using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Thin Windows client for the server-owned Universal Diff primitive. The comparison stays
    /// local and uses the same authenticated PaRuX Core process as every other settings surface.
    /// </summary>
    public sealed class DiffClient
    {
        public static DiffClient Current { get; } = new DiffClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private DiffClient() { }

        public async Task<DiffResult> CompareAsync(string oldText, string newText, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, bootstrap.Endpoint.TrimEnd('/') + "/api/v1/diff/text");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            request.Content = JsonContent.Create(new DiffRequest { OldText = oldText ?? string.Empty, NewText = newText ?? string.Empty });

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Universal Diff request failed (" + (int)response.StatusCode + "): " + detail);
            }

            DiffResult? value = await response.Content.ReadFromJsonAsync<DiffResult>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Universal Diff response.");
            return value;
        }
    }
}
