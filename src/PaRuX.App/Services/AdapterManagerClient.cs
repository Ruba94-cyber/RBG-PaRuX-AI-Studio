using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class AdapterManagerClient
    {
        public static AdapterManagerClient Current { get; } = new AdapterManagerClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));
        private AdapterManagerClient() { }

        public Task<List<AdapterDto>> ListAsync(CancellationToken ct) =>
            SendAsync<List<AdapterDto>>(HttpMethod.Get, "/api/v1/adapters", null, ct);

        public Task<AdapterDto> GetAsync(string id, CancellationToken ct) =>
            SendAsync<AdapterDto>(HttpMethod.Get, "/api/v1/adapters/" + Uri.EscapeDataString(id), null, ct);

        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/adapters/" + Uri.EscapeDataString(id) + "/enabled",
                new SetAdapterEnabledRequest { Enabled = enabled }, ct);

        public Task RenameAsync(string id, string name, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/adapters/" + Uri.EscapeDataString(id) + "/rename",
                new RenameAdapterRequest { Name = name }, ct);

        public Task<LoraConversionEngineStatus> GetConversionStatusAsync(CancellationToken ct) =>
            SendAsync<LoraConversionEngineStatus>(HttpMethod.Get, "/api/v1/adapters/conversion/status", null, ct);

        public Task<AdapterDto> ConvertAsync(string id, string outtype, CancellationToken ct) =>
            SendAsync<AdapterDto>(HttpMethod.Post, "/api/v1/adapters/" + Uri.EscapeDataString(id) + "/convert",
                new ConvertAdapterRequest { Outtype = outtype }, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Adapter Manager request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Adapter Manager response.");
        }

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Adapter Manager request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
