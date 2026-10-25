using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Talks to the server-owned Skill registry - the same 15-skill set (and any
    /// user-created/imported skills) Android and the chat pipeline itself see.
    /// Backs the composer's skill selector, slash commands, and the Skill Inspector.
    /// </summary>
    public sealed class SkillsClient
    {
        public static SkillsClient Current { get; } = new SkillsClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private SkillsClient() { }

        public Task<List<SkillManifest>> ListAsync(CancellationToken ct) =>
            SendAsync<List<SkillManifest>>(HttpMethod.Get, "/api/v1/skills", null, ct);

        public Task<SkillManifest> GetAsync(string id, CancellationToken ct) =>
            SendAsync<SkillManifest>(HttpMethod.Get, "/api/v1/skills/" + Uri.EscapeDataString(id), null, ct);

        public Task<SkillManifest> CreateOrUpdateAsync(SkillManifest manifest, CancellationToken ct) =>
            SendAsync<SkillManifest>(HttpMethod.Post, "/api/v1/skills", new SkillCreateRequest { Manifest = manifest }, ct);

        public Task<SkillManifest> ImportAsync(SkillManifest manifest, CancellationToken ct) =>
            SendAsync<SkillManifest>(HttpMethod.Post, "/api/v1/skills/import", manifest, ct);

        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/skills/" + Uri.EscapeDataString(id) + "/enabled",
                new SkillEnabledRequest { Enabled = enabled }, ct);

        public Task<SkillManifest> SetPermissionsAsync(string id, SkillPermissions permissions, CancellationToken ct) =>
            SendAsync<SkillManifest>(HttpMethod.Post, "/api/v1/skills/" + Uri.EscapeDataString(id) + "/permissions",
                permissions, ct);

        public Task DeleteAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/skills/" + Uri.EscapeDataString(id), null, ct);

        public Task<SkillRouteResult> RouteAsync(string message, string forcedSkillId, CancellationToken ct) =>
            SendAsync<SkillRouteResult>(HttpMethod.Post, "/api/v1/skills/route",
                new SkillRouteRequest { Message = message, ForcedSkillId = forcedSkillId }, ct);

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Skills request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Skills request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty skills response.");
            return value;
        }
    }
}
