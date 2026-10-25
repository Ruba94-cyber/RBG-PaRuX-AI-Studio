using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The single integration surface onto the local AI runtime. PaRuX Server owns the actual
    /// llama-server/ExLlamaV3/ComfyUI lifecycle. Readiness is therefore obtained from PaRuX Core,
    /// never from one engine's private port; state files remain useful for fast mode/profile/game
    /// diagnostics and backwards-compatible labels.
    /// </summary>
    public sealed class LocalAiController
    {
        private static readonly HttpClient Http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public static LocalAiController Current { get; } = new LocalAiController();

        private FileSystemWatcher? _watcher;

        /// <summary>Raised (on a background thread) whenever a LocalAI state file changes.</summary>
        public event EventHandler? StateChanged;

        private LocalAiController() { }

        public async Task<string?> GetManagedProviderEndpointAsync(CancellationToken ct)
        {
            try
            {
                RuntimeManagerStatus status = await ModelRolesClient.Current.GetRuntimeStatusAsync(ct).ConfigureAwait(false);
                return ManagedRuntimeEndpoint.FromStatus(status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        // State files
        // ------------------------------------------------------------------

        private static string ReadTrimmed(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return string.Empty;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        return sr.ReadToEnd().Trim();
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(30);
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        public bool IsGameActive()
        {
            try { return File.Exists(AppPaths.GameMarkerFile); }
            catch { return false; }
        }

        public string GetMode()
        {
            string v = ReadTrimmed(AppPaths.ModeFile).ToUpperInvariant();
            return (v == "ON" || v == "OFF" || v == "AUTO") ? v : "AUTO";
        }

        public string GetTextProfile()
        {
            string v = ReadTrimmed(AppPaths.TextProfileFile).ToUpperInvariant();
            return (v == "STANDARD" || v == "UNCENSORED") ? v : "STANDARD";
        }

        public string GetActiveProfile()
        {
            return ReadTrimmed(AppPaths.ActiveProfileFile);
        }

        private bool ServerProcessPresent()
        {
            try
            {
                string raw = ReadTrimmed(AppPaths.LlamaPidFile);
                int id;
                if (!int.TryParse(raw, out id)) return false;
                Process.GetProcessById(id);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Reads everything the diagnostics panel needs in one go.</summary>
        public async Task<LocalAiState> GetStateAsync(CancellationToken ct)
        {
            bool processPresent = ServerProcessPresent();
            bool healthy = await IsHealthyAsync(ct).ConfigureAwait(false);
            var state = new LocalAiState
            {
                Mode = GetMode(),
                TextProfile = GetTextProfile(),
                ActiveProfile = healthy ? GetActiveProfile() : string.Empty,
                GameActive = IsGameActive(),
                // Legacy diagnostic means "llama process present". Product readiness is Healthy;
                // an ExLlamaV3 AUTO winner can legitimately be healthy with this value false.
                ServerProcessPresent = processPresent,
                Healthy = healthy
            };
            return state;
        }

        // ------------------------------------------------------------------
        // Watching
        // ------------------------------------------------------------------

        public void StartWatching()
        {
            if (_watcher != null) return;
            try
            {
                Directory.CreateDirectory(AppPaths.LocalAiStateDir);
                _watcher = new FileSystemWatcher(AppPaths.LocalAiStateDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onChange = (s, e) => RaiseStateChanged();
                _watcher.Changed += onChange;
                _watcher.Created += onChange;
                _watcher.Deleted += onChange;
                _watcher.Renamed += (s, e) => RaiseStateChanged();
            }
            catch (Exception ex)
            {
                Log.Write("LocalAiController.StartWatching failed: " + ex.Message);
            }
        }

        private void RaiseStateChanged()
        {
            EventHandler? h = StateChanged;
            if (h != null)
            {
                foreach (Delegate d in h.GetInvocationList())
                {
                    try { ((EventHandler)d)(this, EventArgs.Empty); }
                    catch (Exception ex) { Log.Write("StateChanged handler threw: " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
        }

        // ------------------------------------------------------------------
        // Health
        // ------------------------------------------------------------------

        public async Task<bool> IsHealthyAsync(CancellationToken ct)
        {
            try
            {
                ServerBootstrap bootstrap = PaRuXServerController.Current.GetCurrentBootstrap();
                using (HttpResponseMessage resp = await Http.GetAsync(
                    bootstrap.Endpoint.TrimEnd('/') + "/api/v1/health", ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        JsonElement providerReady;
                        return doc.RootElement.TryGetProperty("providerReady", out providerReady)
                            && providerReady.ValueKind == JsonValueKind.True;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Driving the runtime via PaRuX Server's HTTP API
        // ------------------------------------------------------------------

        private static readonly HttpClient ActionHttp = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(6));
        private static readonly JsonSerializerOptions ActionJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public class ActionResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        public async Task<ActionResult> RunActionAsync(string action, CancellationToken ct)
        {
            var result = new ActionResult();
            try
            {
                ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, bootstrap.Endpoint.TrimEnd('/') + "/api/v1/runtime/actions");
                request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                request.Content = JsonContent.Create(new RuntimeActionRequest { Action = action });

                using HttpResponseMessage response = await ActionHttp.SendAsync(request, ct).ConfigureAwait(false);
                RuntimeActionResponse? parsed = await response.Content.ReadFromJsonAsync<RuntimeActionResponse>(ActionJsonOptions, ct)
                    .ConfigureAwait(false) ?? new RuntimeActionResponse { Success = false, Message = "Empty response from PaRuX Core." };
                result.Success = parsed.Success;
                result.Message = parsed.Message;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            Log.Write("Runtime action " + action + " -> success=" + result.Success + " : " + result.Message);
            RaiseStateChanged();
            return result;
        }

        public async Task EnsureReadyAsync(IProgress<string?>? progress, CancellationToken ct)
        {
            if (IsGameActive())
            {
                throw new LocalAiException(
                    "Gaming Mode is active — a game is running, so the AI stays unloaded to leave the GPU alone. "
                  + "Close the game and try again.");
            }

            // Engine-neutral PaRuX readiness means an already-running ExLlamaV3 AUTO winner is a
            // valid ready state. Never issue Ensure merely because llama.cpp:1234 is intentionally off.
            if (await IsHealthyAsync(ct).ConfigureAwait(false)) return;

            if (GetMode() == "OFF")
            {
                throw new LocalAiException(
                    "Local AI is switched off. Turn it back on from the PaRuX tray menu (AI Auto or AI On) and try again.");
            }

            if (progress != null) progress.Report("Waking up PaRuX…");
            Task<ActionResult> ensure = RunActionAsync("Ensure", ct);

            DateTime deadline = DateTime.UtcNow.AddMinutes(5.5);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (await IsHealthyAsync(ct).ConfigureAwait(false))
                {
                    if (progress != null) progress.Report(null);
                    return;
                }

                if (ensure.IsCompleted)
                {
                    ActionResult r = await ensure.ConfigureAwait(false);
                    if (r.Success)
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            await Task.Delay(500, ct).ConfigureAwait(false);
                            if (await IsHealthyAsync(ct).ConfigureAwait(false))
                            {
                                if (progress != null) progress.Report(null);
                                return;
                            }
                        }
                    }

                    if (progress != null) progress.Report(null);
                    throw new LocalAiException(FriendlyScriptError(r));
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            if (progress != null) progress.Report(null);
            throw new LocalAiException(
                "The model is taking longer than expected to load. It may still be starting — give it a moment and try again.");
        }

        public static string FriendlyScriptError(ActionResult r)
        {
            string raw = r == null ? string.Empty : (r.Message ?? string.Empty);

            if (raw.IndexOf("game is currently active", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Gaming Mode is active — a game is running, so the AI stays unloaded. "
                     + "Close the game and try again.";
            }
            if (raw.IndexOf("OFF mode", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Local AI is switched off. Turn it back on from the PaRuX tray menu and try again.";
            }
            if (raw.IndexOf("No sufficiently powerful GPU", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "The configured discrete GPU isn't attached right now, and this profile needs it. "
                     + "Connect the accelerator, or choose an available profile.";
            }
            if (raw.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("bind", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Port 1234 is already in use by another process, so the model server can't start. " + raw;
            }
            if (raw.IndexOf("Timed out", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "The model timed out while loading. It may still be starting — give it a moment and try again.";
            }

            return string.IsNullOrWhiteSpace(raw) ? "The local AI controller reported a failure." : raw;
        }
    }
}
