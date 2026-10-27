using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class PersonaClient
    {
        public static PersonaClient Current { get; } = new PersonaClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private PersonaClient() { }

        public Task<PersonaListResponse> GetAllAsync(CancellationToken ct) =>
            SendAsync<PersonaListResponse>(HttpMethod.Get, "/api/v1/personas", null, ct);

        public Task<Persona> CreateAsync(string name, string systemPrompt, CancellationToken ct) =>
            SendAsync<Persona>(HttpMethod.Post, "/api/v1/personas",
                new CreatePersonaRequest { Name = name, SystemPrompt = systemPrompt }, ct);

        /// <summary>Imports a SillyTavern/Chub character card (JSON or PNG) as a persona.</summary>
        public Task<Persona> ImportCardAsync(string fileName, byte[] data, CancellationToken ct) =>
            SendAsync<Persona>(HttpMethod.Post, "/api/v1/personas/import-card",
                new ImportPersonaCardRequest { FileName = fileName, DataBase64 = Convert.ToBase64String(data) }, ct);

        public Task DeleteAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/personas/" + Uri.EscapeDataString(id), null, ct);

        public Task<PersonaListResponse> SetActiveAsync(string id, CancellationToken ct) =>
            SendAsync<PersonaListResponse>(HttpMethod.Post, "/api/v1/personas/active",
                new SetActivePersonaRequest { PersonaId = id }, ct);

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
                throw new LocalAiException("Persona request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty persona response.");
            return value;
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
                throw new LocalAiException("Persona request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
