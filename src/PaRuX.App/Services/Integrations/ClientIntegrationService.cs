using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services.Integrations
{
    /// <summary>Issues and revokes the per-client gateway keys (PaRuX Core API keys).</summary>
    public interface IGatewayKeyIssuer
    {
        Task<(string Id, string Key)> CreateAsync(string name, CancellationToken ct);
        Task<bool> ExistsAsync(string id, CancellationToken ct);
        Task RevokeAsync(string id, CancellationToken ct);
    }

    /// <summary>
    /// Connect → detect client, check the gateway, issue a key, write the client's config (with
    /// backup), run a real file-creation test, record everything in a manifest. Disconnect removes
    /// only PaRuX's changes and revokes the key; Restore puts the pre-connect backup back.
    /// Connect is idempotent: a second Connect refreshes PaRuX's own entries in place.
    /// </summary>
    public sealed class ClientIntegrationService
    {
        public const string DefaultModel = "parux-auto";
        private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);
        // Per stream: enough for the client's final error lines, far below what a runaway client can print (CF-08).
        private const int MaxDiagnosticChars = 64 * 1024;

        private readonly IntegrationStore _store;
        private readonly IGatewayKeyIssuer _keys;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ClientIntegrationService(IntegrationStore store, IGatewayKeyIssuer keys, IReadOnlyList<IClientIntegration> clients, HttpClient? http = null)
        {
            _store = store;
            _keys = keys;
            Clients = clients;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public IReadOnlyList<IClientIntegration> Clients { get; }
        public IntegrationStore Store => _store;

        public IClientIntegration Get(string clientId) =>
            Clients.FirstOrDefault(c => c.ClientId == clientId) ?? throw new ArgumentException("Unknown client " + clientId);

        public IntegrationManifest? Manifest(string clientId) => _store.Load(clientId);

        public async Task<ClientDetection> DetectAsync(IClientIntegration client, CancellationToken ct)
        {
            string? executable = client.Locate().FirstOrDefault();
            if (executable == null)
                return new ClientDetection(client.ClientId, client.DisplayName, false, null, null, ClientIntegrationStatus.NotInstalled,
                    client.DisplayName + " was not found on this PC.");
            string? version = await ClientLocator.ReadVersionAsync(executable, ct).ConfigureAwait(false);
            ClientIntegrationStatus status = client.ReadConfiguration(_store.Load(client.ClientId), out string detail);
            if (version == null && status is ClientIntegrationStatus.Available)
            {
                status = ClientIntegrationStatus.Detected;
                detail = "Found at " + executable + " but it did not report a version.";
            }
            else if (version != null && CompareVersions(version, client.TestedVersion) < 0)
            {
                detail += " Version " + version + " is older than the tested " + client.TestedVersion + "; update it if Connect fails.";
            }
            return new ClientDetection(client.ClientId, client.DisplayName, true, executable, version, status, detail);
        }

        public async Task<IntegrationOutcome> ConnectAsync(IClientIntegration client, string gatewayBaseUrl, string model, IProgress<string>? progress, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                progress?.Report("Detecting " + client.DisplayName + "…");
                ClientDetection detection = await DetectAsync(client, ct).ConfigureAwait(false);
                if (!detection.Installed)
                    return new IntegrationOutcome(false, ClientIntegrationStatus.NotInstalled, detection.Detail);

                progress?.Report("Checking the PaRuX gateway…");
                string? healthError = await CheckGatewayAsync(gatewayBaseUrl, null, ct).ConfigureAwait(false);
                if (healthError != null) return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, healthError);

                IntegrationManifest manifest = _store.Load(client.ClientId) ?? new IntegrationManifest { ClientId = client.ClientId };
                progress?.Report("Setting up the local access key…");
                string? key = await EnsureKeyAsync(client, manifest, ct).ConfigureAwait(false);
                if (key == null) return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, "PaRuX could not issue a local access key.");

                string? authError = await CheckGatewayAsync(gatewayBaseUrl, key, ct).ConfigureAwait(false);
                if (authError != null) return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, authError);

                var endpoint = new GatewayEndpoint(gatewayBaseUrl.TrimEnd('/'), key, model);
                progress?.Report("Writing the " + client.DisplayName + " configuration (with backup)…");
                IntegrationOutcome applied = client.Apply(endpoint, manifest, _store);
                if (!applied.Success)
                {
                    // Nothing was written; a key issued only for this attempt is not left behind.
                    if (_store.Load(client.ClientId) == null && manifest.ApiKeyId != null)
                        await TryRevokeAsync(manifest.ApiKeyId, ct).ConfigureAwait(false);
                    return applied;
                }
                manifest.ClientVersion = detection.Version;
                manifest.ConnectedUtc = DateTime.UtcNow;
                manifest.Model = model;
                manifest.GatewayBaseUrl = endpoint.BaseUrl;
                _store.Save(manifest);

                progress?.Report("Running a real test with " + client.DisplayName + "…");
                IntegrationOutcome tested = await RunTestAsync(client, detection.ExecutablePath!, endpoint, manifest, ct).ConfigureAwait(false);
                return tested.Success
                    ? new IntegrationOutcome(true, ClientIntegrationStatus.Connected, "Connected. " + tested.Message)
                    : new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError,
                        "Configured, but the test failed: " + tested.Message + " Use Test to retry or Disconnect to undo.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IntegrationOutcome> TestAsync(IClientIntegration client, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IntegrationManifest? manifest = _store.Load(client.ClientId);
                GatewayEndpoint? endpoint = EndpointFor(manifest);
                if (manifest == null || endpoint == null)
                    return new IntegrationOutcome(false, ClientIntegrationStatus.Available, client.DisplayName + " is not connected to PaRuX.");
                string? executable = client.Locate().FirstOrDefault();
                if (executable == null) return new IntegrationOutcome(false, ClientIntegrationStatus.NotInstalled, client.DisplayName + " was not found.");
                string? healthError = await CheckGatewayAsync(endpoint.BaseUrl, endpoint.ApiKey, ct).ConfigureAwait(false);
                if (healthError != null) return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, healthError);
                return await RunTestAsync(client, executable, endpoint, manifest, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public IntegrationOutcome Launch(IClientIntegration client, string workingDirectory)
        {
            IntegrationManifest? manifest = _store.Load(client.ClientId);
            GatewayEndpoint? endpoint = EndpointFor(manifest);
            if (endpoint == null) return new IntegrationOutcome(false, ClientIntegrationStatus.Available, "Connect " + client.DisplayName + " first.");
            string? executable = client.Locate().FirstOrDefault();
            if (executable == null) return new IntegrationOutcome(false, ClientIntegrationStatus.NotInstalled, client.DisplayName + " was not found.");
            if (!Directory.Exists(workingDirectory)) return new IntegrationOutcome(false, ClientIntegrationStatus.Connected, "Folder not found: " + workingDirectory);
            try
            {
                using Process? process = Process.Start(client.CreateLaunch(executable, endpoint, workingDirectory, _store));
                return process == null
                    ? new IntegrationOutcome(false, ClientIntegrationStatus.Connected, client.DisplayName + " did not start.")
                    : new IntegrationOutcome(true, ClientIntegrationStatus.Connected, client.DisplayName + " started with PaRuX in " + workingDirectory + ".");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                return new IntegrationOutcome(false, ClientIntegrationStatus.Connected, "Could not start " + client.DisplayName + ": " + ex.Message);
            }
        }

        public async Task<IntegrationOutcome> DisconnectAsync(IClientIntegration client, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IntegrationManifest? manifest = _store.Load(client.ClientId);
                if (manifest == null) return new IntegrationOutcome(true, ClientIntegrationStatus.Available, client.DisplayName + " is not connected.");
                IntegrationOutcome removed = client.Remove(manifest, _store);
                if (!removed.Success) return removed;
                if (manifest.ApiKeyId != null) await TryRevokeAsync(manifest.ApiKeyId, ct).ConfigureAwait(false);
                _store.Delete(client.ClientId);
                return new IntegrationOutcome(true, ClientIntegrationStatus.Available, removed.Message + " Access key revoked.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Puts every file PaRuX changed back exactly as it was before the first Connect (edits made
        /// since then to those files are lost, which the UI states before calling this). Files PaRuX
        /// created are removed.
        /// </summary>
        public async Task<IntegrationOutcome> RestoreAsync(IClientIntegration client, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IntegrationManifest? manifest = _store.Load(client.ClientId);
                if (manifest == null) return new IntegrationOutcome(false, ClientIntegrationStatus.Available, "Nothing to restore: " + client.DisplayName + " is not connected.");
                IntegrationOutcome removed = client.Remove(manifest, _store);
                foreach (ManagedFileChange change in manifest.Files.Where(f => f.BackupPath != null))
                {
                    if (!File.Exists(change.BackupPath))
                        return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, "Backup missing: " + change.BackupPath);
                    File.Copy(change.BackupPath!, change.Path, overwrite: true);
                }
                if (manifest.ApiKeyId != null) await TryRevokeAsync(manifest.ApiKeyId, ct).ConfigureAwait(false);
                _store.Delete(client.ClientId);
                int restored = manifest.Files.Count(f => f.BackupPath != null);
                return new IntegrationOutcome(true, ClientIntegrationStatus.Available,
                    (restored > 0 ? "Restored " + restored + " file(s) from the pre-connect backup." : "Removed PaRuX's configuration (nothing needed restoring).")
                    + (removed.Success ? string.Empty : " Note: " + removed.Message));
            }
            finally
            {
                _gate.Release();
            }
        }

        public GatewayEndpoint? EndpointFor(IntegrationManifest? manifest)
        {
            string? key = IntegrationStore.Unprotect(manifest?.ProtectedApiKey);
            return manifest == null || key == null || string.IsNullOrWhiteSpace(manifest.GatewayBaseUrl)
                ? null
                : new GatewayEndpoint(manifest.GatewayBaseUrl, key, manifest.Model);
        }

        private async Task<string?> EnsureKeyAsync(IClientIntegration client, IntegrationManifest manifest, CancellationToken ct)
        {
            string? existing = IntegrationStore.Unprotect(manifest.ProtectedApiKey);
            if (existing != null && manifest.ApiKeyId != null && await _keys.ExistsAsync(manifest.ApiKeyId, ct).ConfigureAwait(false))
                return existing;
            (string id, string key) = await _keys.CreateAsync("Integration: " + client.DisplayName, ct).ConfigureAwait(false);
            if (manifest.ApiKeyId != null && manifest.ApiKeyId != id) await TryRevokeAsync(manifest.ApiKeyId, ct).ConfigureAwait(false);
            manifest.ApiKeyId = id;
            manifest.ProtectedApiKey = IntegrationStore.Protect(key);
            return key;
        }

        private async Task TryRevokeAsync(string id, CancellationToken ct)
        {
            try { await _keys.RevokeAsync(id, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* already gone or Core offline: harmless */ }
        }

        /// <summary>null when the gateway answers (and, with a key, accepts it); otherwise a user-facing reason.</summary>
        private async Task<string?> CheckGatewayAsync(string baseUrl, string? key, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + (key == null ? "/health" : "/v1/models"));
                if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return null;
                return key == null
                    ? "The PaRuX gateway at " + baseUrl + " answered " + (int)response.StatusCode + "."
                    : "The PaRuX gateway rejected the access key (" + (int)response.StatusCode + ").";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return "The PaRuX gateway is not reachable at " + baseUrl + ". Check Settings > Integrations > Gateway.";
            }
        }

        /// <summary>
        /// The acceptance test from the integration contract: the client itself must create
        /// &lt;client&gt;_test file with exactly the marker, through PaRuX, in a scratch folder.
        /// </summary>
        private async Task<IntegrationOutcome> RunTestAsync(IClientIntegration client, string executable, GatewayEndpoint endpoint, IntegrationManifest manifest, CancellationToken ct)
        {
            string folder = Path.Combine(_store.Root, "tests", client.ClientId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(folder);
            string prompt = "Create a file named " + client.TestFileName + " containing exactly " + client.TestMarker;
            var watch = Stopwatch.StartNew();
            string output = string.Empty;
            bool timedOut = false;
            try
            {
                using Process process = Process.Start(client.CreateTest(executable, endpoint, folder, prompt, _store))
                    ?? throw new InvalidOperationException(client.DisplayName + " did not start.");
                process.StandardInput.Close();
                Task<string> stdout = ReadBoundedTailAsync(process.StandardOutput, MaxDiagnosticChars, ct);
                Task<string> stderr = ReadBoundedTailAsync(process.StandardError, MaxDiagnosticChars, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TestTimeout);
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    if (ct.IsCancellationRequested) throw;
                    timedOut = true;
                }
                // Helper processes a client leaves behind (Codex's Windows sandbox, for one) can inherit
                // and hold the pipes after the client itself exited; the output is diagnostic only.
                if (!timedOut) output = await DrainAsync(stdout).ConfigureAwait(false) + await DrainAsync(stderr).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                output = ex.Message;
            }
            watch.Stop();

            string file = Path.Combine(folder, client.TestFileName);
            string? content = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            bool passed = content == client.TestMarker;
            string detail = passed
                ? client.DisplayName + " created " + client.TestFileName + " through PaRuX in " + (int)watch.Elapsed.TotalSeconds + " s."
                : timedOut ? "No result within " + (int)TestTimeout.TotalMinutes + " minutes."
                : content == null ? "The file was not created. " + Tail(output)
                : "The file content was \"" + Truncate(content, 80) + "\" instead of " + client.TestMarker + ".";
            manifest.LastTestUtc = DateTime.UtcNow;
            manifest.LastTestPassed = passed;
            manifest.LastTestDetail = detail;
            _store.Save(manifest);
            if (passed) TryDeleteDirectory(folder);
            return new IntegrationOutcome(passed, passed ? ClientIntegrationStatus.Connected : ClientIntegrationStatus.ConfigurationError, detail);
        }

        internal static async Task<string> DrainAsync(Task<string> read, int waitMs = 3000)
        {
            Task finished = await Task.WhenAny(read, Task.Delay(waitMs)).ConfigureAwait(false);
            return finished == read && read.IsCompletedSuccessfully ? read.Result : string.Empty;
        }

        /// <summary>
        /// Reads a client's output to its end but keeps only the last <paramref name="maxChars"/> characters, so a
        /// chatty client cannot grow PaRuX's memory for the whole test timeout (CF-08). The end of the stream carries
        /// the client's final error, which is the diagnostic that matters.
        /// </summary>
        internal static async Task<string> ReadBoundedTailAsync(TextReader reader, int maxChars, CancellationToken ct = default)
        {
            var tail = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) break;
                tail.Append(buffer, 0, read);
                // Trim in batches so the work stays linear; the retained text never exceeds twice the limit.
                if (tail.Length > 2 * maxChars) tail.Remove(0, tail.Length - maxChars);
            }
            if (tail.Length > maxChars) tail.Remove(0, tail.Length - maxChars);
            return tail.ToString();
        }

        private static string Tail(string text)
        {
            text = text.Trim();
            return text.Length == 0 ? string.Empty : "Output: " + (text.Length > 400 ? "…" + text[^400..] : text);
        }

        private static string Truncate(string text, int max) => text.Length > max ? text[..max] + "…" : text;

        private static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Numeric comparison of the leading x.y.z; unparsable versions compare as equal.</summary>
        public static int CompareVersions(string left, string right)
        {
            static int[] Parts(string v) => v.Split('-', '+')[0].Split('.').Take(3)
                .Select(p => int.TryParse(p, out int n) ? n : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
            int[] a = Parts(left), b = Parts(right);
            for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }
    }
}
