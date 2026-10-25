using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Best-effort sync between the local SQLite conversation store and PaRuX
    /// Server's conversation store, so Windows and any paired Android device
    /// converge on the same logical history. Every call is defensive by design:
    /// HistoryStore's local SQLite remains the reliable, always-available path,
    /// and this is a layer synced on top of it, never a replacement for it. A
    /// server that is unreachable, restarting, or never started must never
    /// block or fail a local save/delete/load.
    /// </summary>
    public sealed class ConversationSyncClient
    {
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(5));
        public static ConversationSyncClient Current { get; } = new ConversationSyncClient();
        private ConversationSyncClient() { }

        public async Task PushAsync(Conversation conversation)
        {
            if (conversation == null || string.IsNullOrEmpty(conversation.Id) || conversation.IsIncognito) return;

            // The local HistoryStore write already succeeded before PushAsync is scheduled. Any
            // assistant-owned files in that persisted conversation are therefore safe to catalog
            // as final Artifacts now. ArtifactCaptureService is independently best-effort and
            // deduplicates by real path across restarts; user uploads and Incognito are excluded.
            try
            {
                await ArtifactCaptureService.Current
                    .CaptureConversationAttachmentsAsync(conversation, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("ConversationSyncClient artifact capture skipped (non-fatal): " + ex.Message);
            }

            try
            {
                ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
                var request = new ConversationUpsertRequest
                {
                    Title = conversation.Title ?? "New chat",
                    CreatedUtc = conversation.CreatedUtc,
                    UpdatedUtc = conversation.UpdatedUtc,
                    DocumentJson = HistoryStore.SerializeDocument(conversation)
                };
                using var req = new HttpRequestMessage(HttpMethod.Put,
                    server.Endpoint.TrimEnd('/') + "/api/v1/conversations/" + conversation.Id)
                {
                    Content = JsonContent.Create(request)
                };
                req.Headers.Add("X-PaRuX-Token", server.Token);
                using HttpResponseMessage response = await Http.SendAsync(req).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    Log.Write("ConversationSyncClient.PushAsync: server rejected the push (" + (int)response.StatusCode + ").");
            }
            catch (Exception ex)
            {
                Log.Write("ConversationSyncClient.PushAsync skipped (non-fatal, local save still stands): " + ex.Message);
            }
        }

        public async Task DeleteAsync(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
                using var req = new HttpRequestMessage(HttpMethod.Delete,
                    server.Endpoint.TrimEnd('/') + "/api/v1/conversations/" + id);
                req.Headers.Add("X-PaRuX-Token", server.Token);
                using HttpResponseMessage response = await Http.SendAsync(req).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
                    Log.Write("ConversationSyncClient.DeleteAsync: server rejected the delete (" + (int)response.StatusCode + ").");
            }
            catch (Exception ex)
            {
                Log.Write("ConversationSyncClient.DeleteAsync skipped (non-fatal): " + ex.Message);
            }
        }

        /// <summary>
        /// Pulls anything from the server that is missing locally or newer than the
        /// local copy, and persists it through the normal local HistoryStore path.
        /// Intended to run once at startup, off the UI thread. Never throws; returns
        /// the number of conversations pulled so the caller can decide whether to
        /// refresh the sidebar.
        /// </summary>
        public async Task<int> PullNewerAsync(CancellationToken ct)
        {
            try
            {
                ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
                string root = server.Endpoint.TrimEnd('/');

                using var listReq = new HttpRequestMessage(HttpMethod.Get, root + "/api/v1/conversations");
                listReq.Headers.Add("X-PaRuX-Token", server.Token);
                using HttpResponseMessage listResp = await Http.SendAsync(listReq, ct).ConfigureAwait(false);
                if (!listResp.IsSuccessStatusCode) return 0;

                List<ConversationSummary> summaries = await listResp.Content
                    .ReadFromJsonAsync<List<ConversationSummary>>(cancellationToken: ct).ConfigureAwait(false)
                    ?? new List<ConversationSummary>();

                Dictionary<string, DateTime> local = HistoryStore.LoadAll()
                    .Where(c => !string.IsNullOrEmpty(c.Id))
                    .ToDictionary(c => c.Id, c => c.UpdatedUtc);

                int pulled = 0;
                foreach (ConversationSummary summary in summaries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (local.TryGetValue(summary.Id, out DateTime existingUpdated)
                        && existingUpdated >= summary.UpdatedUtc)
                        continue;

                    using var getReq = new HttpRequestMessage(HttpMethod.Get, root + "/api/v1/conversations/" + summary.Id);
                    getReq.Headers.Add("X-PaRuX-Token", server.Token);
                    using HttpResponseMessage getResp = await Http.SendAsync(getReq, ct).ConfigureAwait(false);
                    if (!getResp.IsSuccessStatusCode) continue;

                    ConversationDocument? document = await getResp.Content
                        .ReadFromJsonAsync<ConversationDocument>(cancellationToken: ct).ConfigureAwait(false);
                    if (document == null) continue;

                    Conversation? conversation = HistoryStore.DeserializeDocument(document.DocumentJson);
                    if (conversation == null || string.IsNullOrEmpty(conversation.Id)) continue;

                    HistoryStore.Save(conversation);
                    pulled++;
                }
                return pulled;
            }
            catch (Exception ex)
            {
                Log.Write("ConversationSyncClient.PullNewerAsync skipped (non-fatal): " + ex.Message);
                return 0;
            }
        }
    }
}
