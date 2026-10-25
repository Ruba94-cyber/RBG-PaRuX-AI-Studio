using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class ResearchClient
    {
        public static ResearchClient Current { get; } = new ResearchClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(6));
        private ResearchClient() { }

        public async Task<ResearchResult> AskAsync(string question, string mode, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/research");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(new ResearchRequest { Question = question, Mode = mode });
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Research request failed (" + (int)response.StatusCode + "): " + detail);
            }
            ResearchResult? result = await response.Content.ReadFromJsonAsync<ResearchResult>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null) throw new LocalAiException("PaRuX Core returned an empty research result.");
            return result;
        }
    }
}
