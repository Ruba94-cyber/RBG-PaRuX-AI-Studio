using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Talks to PaRuX.Server's Backup &amp; Restore endpoints. Export/import move a whole .zip
    /// archive rather than JSON, so this follows MemoryClient/RagClient's token-header pattern but
    /// with raw byte bodies instead of JsonContent. V2 preserves the legacy archive plus durable
    /// Execution Profiles; format-v1 backups remain import-compatible.
    /// </summary>
    public sealed class BackupClient
    {
        public static BackupClient Current { get; } = new BackupClient();

        // Backups are routinely hundreds of MB (context graph, artifacts, history) and Core fully
        // decompresses them to validate before restoring, so two minutes was not enough.
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));

        private BackupClient() { }

        /// <summary>Downloads the full backup archive as bytes. The caller is responsible for
        /// writing it to wherever the user picked (see SettingsView.ExportBackup_Click).</summary>
        public async Task<byte[]> ExportAsync(CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, bootstrap.Endpoint.TrimEnd('/') + "/api/v2/backup/export");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Backup export failed (" + (int)response.StatusCode + "): " + detail);
            }
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Deletes the automatic pre-restore safety snapshots. Returns how many were removed.</summary>
        public async Task<int> ClearSafetySnapshotsAsync(CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete, bootstrap.Endpoint.TrimEnd('/') + "/api/v1/backup/safety-snapshots");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Clearing safety snapshots failed (" + (int)response.StatusCode + "): " + detail);
            }
            System.Text.Json.JsonElement body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            return body.TryGetProperty("removed", out System.Text.Json.JsonElement removed) ? removed.GetInt32() : 0;
        }

        /// <summary>Uploads a backup archive and restores it. Destructive on the server/desktop
        /// side - Core validates the complete archive and the optional Execution Profiles sidecar
        /// before applying the existing atomic per-store swaps.</summary>
        public async Task<BackupImportResult> ImportAsync(byte[] zipBytes, CancellationToken ct, bool allowIncompleteRollbackSnapshot = false)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, bootstrap.Endpoint.TrimEnd('/') + "/api/v2/backup/import"
                + (allowIncompleteRollbackSnapshot ? "?allowIncompleteRollbackSnapshot=true" : string.Empty));
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            request.Content = new ByteArrayContent(zipBytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                BackupImportRefusal? refusal = null;
                try { refusal = await response.Content.ReadFromJsonAsync<BackupImportRefusal>(cancellationToken: ct).ConfigureAwait(false); }
                catch (System.Text.Json.JsonException) { }
                if (refusal is { Code: BackupImportRefusal.IncompleteRollbackSnapshotCode })
                    throw new IncompleteRollbackSnapshotException(refusal.Error, refusal.UnsnapshottableStores);
            }
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Backup import failed (" + (int)response.StatusCode + "): " + detail);
            }
            BackupImportResult? result = await response.Content.ReadFromJsonAsync<BackupImportResult>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null) throw new LocalAiException("PaRuX Core returned an empty backup-import response.");
            return result;
        }
    }

    /// <summary>Core refused a restore because some live stores cannot be snapshotted for rollback; nothing was
    /// changed. The user may confirm explicitly and retry with the opt-in.</summary>
    public sealed class IncompleteRollbackSnapshotException : LocalAiException
    {
        public System.Collections.Generic.IReadOnlyList<string> UnsnapshottableStores { get; }

        public IncompleteRollbackSnapshotException(string message, System.Collections.Generic.IReadOnlyList<string> stores) : base(message)
        {
            UnsnapshottableStores = stores;
        }
    }
}
