using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Dedicated browser-development surface for the coding agent. It never attaches to the user's
    /// ordinary browser profile: PaRuX launches Edge/Chrome with a private profile under its own data
    /// directory and talks to that process through Chrome DevTools Protocol (CDP).
    ///
    /// When PaRuX Internet is disabled, the browser is launched with external hostname resolution
    /// blocked while localhost/loopback remains available. This gives the coding agent a real
    /// edit -> launch -> inspect DOM/console/network -> repair loop for local web apps without making
    /// the coding AppContainer's no-network boundary meaningless.
    /// </summary>
    public sealed class BrowserDevToolsService : IAsyncDisposable
    {
        public static BrowserDevToolsService Current { get; } = new BrowserDevToolsService();

        private const int MaximumEvents = 250;
        private const int MaximumDomCharacters = 120_000;
        private const int MaximumEvaluationCharacters = 20_000;
        // How often a browser launched with Internet ON checks whether the switch has gone OFF.
        private static readonly TimeSpan InternetSwitchPollInterval = TimeSpan.FromSeconds(1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly JsonElement EmptyObject = CreateEmptyObject();
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly BrowserTaskOwnerLease _taskLease = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentQueue<string> _console = new();
        private readonly ConcurrentQueue<string> _network = new();
        // Document ancestry for Fetch.requestPaused attribution (fed by Page.* events).
        private readonly BrowserFrameLineage _frames = new();
        // Latest URL PaRuX asked the browser to open, so a PaRuX-issued navigation
        // that redirects to loopback is not mistaken for a local, trusted origin.
        private volatile string? _pendingNavigationUrl;

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _readerCts;
        private Task? _readerTask;
        private Process? _browser;
        private string? _sessionProfileDirectory;
        private int _port;
        private int _nextId;
        private bool _internetEnabledAtLaunch;
        private bool _fetchGuardReady;
        private int _pendingPausedRequests;
        private long _blockedRequests;
        private int _uncontrolledChildTargetDetected;
        private int _profileSweepStarted;
        private BrowserOfflineEgressSink? _egressSink;
        // Set while a browser launched with Internet ON runs. Cancelling it ends the switch watch.
        private CancellationTokenSource? _internetWatch;

        private BrowserDevToolsService() { }

        public sealed record BrowserResult(bool Success, string Observation, string? Error = null);

        public bool TryClaimTask(string taskId) => _taskLease.TryClaim(taskId);

        /// <summary>
        /// Hold the lease until the browser is fully stopped. If shutdown
        /// cannot be proved, do not transfer its sensitive state to a new task.
        /// </summary>
        public async Task ReleaseTaskAsync(string taskId)
        {
            // Shutdown is bounded. A failed or unfinished graceful stop is followed by a
            // forced kill, and the lease completes only once the process is proven gone.
            bool owned = _taskLease.IsOwnedBy(taskId);
            bool released = await _taskLease.ReleaseAsync(
                taskId,
                gracefulShutdown: ct => ResetAsync(ct),
                forceStop: ct => ForceStopBrowserAsync(ct),
                isProvenStopped: () => IsBrowserProvenStopped(_browser),
                gracefulTimeout: TimeSpan.FromSeconds(10),
                forceStopTimeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (owned && !released)
                Log.Write("Developer browser lease stays locked: the browser process is not proven stopped.");
        }

        public async Task<BrowserResult> NavigateAsync(string url, CancellationToken ct)
        {
            if (!TryNormalizeUrl(url, out Uri? uri, out string? error))
                return new BrowserResult(false, string.Empty, error);

            bool external = !IsLocal(uri!);
            InternetStatus internet;
            try { internet = await InternetClient.Current.GetStatusAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { return new BrowserResult(false, string.Empty, "Could not read PaRuX Internet policy: " + ex.Message); }
            if (external && !internet.Enabled)
                return new BrowserResult(false, string.Empty, "PaRuX Internet is OFF. The developer browser may navigate only to localhost/loopback.");
            if (!await BrowserRequestAdmissionPolicy.IsAllowedAsync(
                    uri!.AbsoluteUri, internet.Enabled, BrowserResolvedAddressPolicy.SystemResolver, ct).ConfigureAwait(false))
                return new BrowserResult(false, string.Empty,
                    "Developer browser URL is not admitted by the current PaRuX Internet policy.");

            try
            {
                await EnsureSessionAsync(internet.Enabled, ct).ConfigureAwait(false);
                // Explain the common case before the request guard: a public page cannot open a local URL.
                if (BrowserInitiatorPolicy.IsLoopbackDestination(uri)
                    && !BrowserInitiatorPolicy.MayIssueLoopbackRequest(_frames.ChainFrom(_frames.TopFrameId)))
                    return new BrowserResult(false, string.Empty,
                        "The developer browser is on a public page, which may not open a localhost URL. "
                        + "Run browser_reset first, then navigate to the local URL.");
                long blockedBefore = Interlocked.Read(ref _blockedRequests);
                _pendingNavigationUrl = uri!.AbsoluteUri;
                JsonElement result = await SendCommandAsync("Page.navigate", new { url = uri!.AbsoluteUri }, ct).ConfigureAwait(false);
                await Task.Delay(350, ct).ConfigureAwait(false);
                BrowserResult? redirectDenied = await RejectUnsafeCurrentPageAsync(
                    internet.Enabled, ct).ConfigureAwait(false);
                if (redirectDenied != null) return redirectDenied;
                if (!BrowserDevToolsNavigationResultPolicy.IsSuccessful(
                        result, Interlocked.Read(ref _blockedRequests) != blockedBefore))
                    return new BrowserResult(false, string.Empty,
                        "Browser navigation failed, downloaded a file or was blocked by PaRuX Internet policy.");
                string frameId = result.TryGetProperty("frameId", out JsonElement frame) ? frame.GetString() ?? string.Empty : string.Empty;
                return new BrowserResult(true, "Navigated dedicated PaRuX browser to " + BrowserNetworkEvidencePolicy.DisplayUrl(uri.AbsoluteUri)
                    + (frameId.Length == 0 ? string.Empty : " (frame " + frameId + ")."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A CDP protocol failure may include the attempted URL with query
                // credentials. Never expose exception text to model-visible logs.
                Log.Write("Developer browser navigation failed: " + ex.GetType().Name);
                return new BrowserResult(false, string.Empty,
                    "Browser navigation failed; inspect current page state and Internet policy.");
            }
        }

        public async Task<BrowserResult> GetDomAsync(CancellationToken ct)
        {
            try
            {
                bool internetEnabled = await InternetEnabledAsync(ct).ConfigureAwait(false);
                await EnsureSessionAsync(internetEnabled, ct).ConfigureAwait(false);
                BrowserResult? redirectDenied = await RejectUnsafeCurrentPageAsync(
                    internetEnabled, ct).ConfigureAwait(false);
                if (redirectDenied != null) return redirectDenied;
                JsonElement result = await EvaluateRawAsync("document.documentElement ? document.documentElement.outerHTML : ''", returnByValue: true, ct).ConfigureAwait(false);
                if (!BrowserDevToolsEvaluationPolicy.IsSuccessful(result))
                    return new BrowserResult(false, string.Empty,
                        "Browser DOM inspection raised a JavaScript exception; no valid UI evidence was produced.");
                string value = ExtractEvaluationString(result);
                if (value.Length > MaximumDomCharacters) value = value[..MaximumDomCharacters] + "\n… (DOM truncated)";
                return new BrowserResult(true, value.Length == 0 ? "(document has no HTML)" : value);
            }
            catch (Exception ex) { return new BrowserResult(false, string.Empty, ex.Message); }
        }

        public async Task<BrowserResult> EvaluateAsync(string expression, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(expression)) return new BrowserResult(false, string.Empty, "JavaScript expression is empty.");
            if (expression.Length > 8_000) return new BrowserResult(false, string.Empty, "JavaScript expression exceeds the 8,000-character browser-tool limit.");
            try
            {
                bool internetEnabled = await InternetEnabledAsync(ct).ConfigureAwait(false);
                await EnsureSessionAsync(internetEnabled, ct).ConfigureAwait(false);
                if (!await IsCurrentPageMutationAllowedAsync(ct).ConfigureAwait(false))
                    return new BrowserResult(false, string.Empty,
                        "Coding-agent browser mutations are limited to local web apps until explicit per-site authority is available.");
                JsonElement result = await EvaluateRawAsync(expression, returnByValue: true, ct).ConfigureAwait(false);
                BrowserResult? redirectDenied = await RejectUnsafeCurrentPageAsync(
                    internetEnabled, ct).ConfigureAwait(false);
                if (redirectDenied != null) return redirectDenied;
                bool actionSucceeded = BrowserDevToolsEvaluationPolicy.IsSuccessful(result);
                string rendered = result.GetRawText();
                if (rendered.Length > MaximumEvaluationCharacters) rendered = rendered[..MaximumEvaluationCharacters] + "\n… (evaluation truncated)";
                return new BrowserResult(actionSucceeded, rendered,
                    actionSucceeded ? null
                        : "Browser JavaScript raised an exception or the UI action returned ok:false. Inspect the page and try a valid selector.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // An exception can embed the arbitrary JS source passed by the
                // model. Never propagate CDP exception content into task logs.
                return new BrowserResult(false, string.Empty,
                    "Browser JavaScript execution failed without verifiable result.");
            }
        }

        /// <summary>
        /// Unlike developer-directed browser_eval, the agent's built-in click/type
        /// actions must never expose raw CDP replies or values supplied to a form.
        /// The only output is an allowlisted status; proof comes from a later
        /// independent UI observation, not from delivery of this action.
        /// </summary>
        public async Task<BrowserResult> EvaluateUiActionAsync(string expression, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 16_000)
                return new BrowserResult(false, string.Empty, "Invalid or oversized UI action.");
            try
            {
                bool internetEnabled = await InternetEnabledAsync(ct).ConfigureAwait(false);
                await EnsureSessionAsync(internetEnabled, ct).ConfigureAwait(false);
                if (!await IsCurrentPageMutationAllowedAsync(ct).ConfigureAwait(false))
                    return new BrowserResult(false, string.Empty,
                        "Coding-agent browser mutations are limited to local web apps until explicit per-site authority is available.");
                JsonElement result = await EvaluateRawAsync(expression, returnByValue: true, ct).ConfigureAwait(false);
                BrowserResult? redirectDenied = await RejectUnsafeCurrentPageAsync(
                    internetEnabled, ct).ConfigureAwait(false);
                if (redirectDenied != null) return redirectDenied;
                BrowserDomActionResultPolicy.Decision outcome =
                    BrowserDomActionResultPolicy.Classify(result);
                return new BrowserResult(outcome.Success, outcome.Observation, outcome.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The exception may include the JavaScript source containing the
                // user's form input. Never persist it as model-visible evidence.
                return new BrowserResult(false, string.Empty,
                    "Browser UI action failed before the resulting state could be inspected.");
            }
        }

        /// <summary>
        /// Inspect one explicit DOM postcondition, waiting for a bounded number
        /// of asynchronous UI updates. Failure returns a fixed message without
        /// exposing actual DOM content or model-supplied expected values.
        /// A passing assertion is only observed page state, not an independent
        /// build/test/verifier success.
        /// </summary>
        public async Task<BrowserResult> AssertDomAsync(
            string selector, string target, string? expected, CancellationToken ct)
        {
            if (!BrowserDomAssertionPolicy.IsValid(selector, target, expected))
                return new BrowserResult(false, string.Empty, "Invalid browser DOM assertion parameters.");

            string script = BrowserDomAssertionPolicy.BuildScript(selector, target, expected);
            try
            {
                bool internetEnabled = await InternetEnabledAsync(ct).ConfigureAwait(false);
                await EnsureSessionAsync(internetEnabled, ct).ConfigureAwait(false);
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    BrowserResult? deniedBefore = await RejectUnsafeCurrentPageAsync(
                        internetEnabled, ct).ConfigureAwait(false);
                    if (deniedBefore != null) return deniedBefore;
                    JsonElement cdp = await EvaluateRawAsync(script, returnByValue: true, ct)
                        .ConfigureAwait(false);
                    BrowserResult? deniedAfter = await RejectUnsafeCurrentPageAsync(
                        internetEnabled, ct).ConfigureAwait(false);
                    if (deniedAfter != null) return deniedAfter;

                    BrowserDomAssertionResultPolicy.Decision observed =
                        BrowserDomAssertionResultPolicy.Classify(cdp);
                    if (observed.Success)
                        return new BrowserResult(true, observed.Observation);
                    if (!observed.Retryable || attempt == 11)
                        return new BrowserResult(false, string.Empty, observed.Error);
                    await Task.Delay(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
                }
                return new BrowserResult(false, string.Empty,
                    "DOM assertion did not reach the expected state.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // CDP errors may embed the evaluated script and expected content.
                return new BrowserResult(false, string.Empty,
                    "DOM assertion failed before independent browser state could be observed.");
            }
        }

        public async Task<BrowserResult> GetConsoleAsync(CancellationToken ct)
        {
            try
            {
                await EnsureSessionAsync(await InternetEnabledAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                string[] events = _console.ToArray();
                return new BrowserResult(true, events.Length == 0 ? "(no console events captured)" : string.Join("\n", events.TakeLast(120)));
            }
            catch (Exception ex) { return new BrowserResult(false, string.Empty, ex.Message); }
        }

        public async Task<BrowserResult> GetNetworkAsync(CancellationToken ct)
        {
            try
            {
                await EnsureSessionAsync(await InternetEnabledAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                string[] events = _network.ToArray();
                return new BrowserResult(true, events.Length == 0 ? "(no network responses captured)" : string.Join("\n", events.TakeLast(160)));
            }
            catch (Exception ex) { return new BrowserResult(false, string.Empty, ex.Message); }
        }

        public async Task<BrowserResult> InspectScreenshotAsync(string goal, CancellationToken ct)
        {
            string path = string.Empty;
            try
            {
                // Screenshots are page content: keep them under PaRuX's root, and sweep crash leftovers first.
                path = BrowserScreenshotStore.PrepareNewPath(AppPaths.PaRuXRoot, DateTime.UtcNow, TimeSpan.FromHours(1));
                bool internetEnabled = await InternetEnabledAsync(ct).ConfigureAwait(false);
                await EnsureSessionAsync(internetEnabled, ct).ConfigureAwait(false);
                BrowserResult? redirectDenied = await RejectUnsafeCurrentPageAsync(
                    internetEnabled, ct).ConfigureAwait(false);
                if (redirectDenied != null) return redirectDenied;
                JsonElement result = await SendCommandAsync("Page.captureScreenshot", new { format = "png", fromSurface = true }, ct).ConfigureAwait(false);
                if (!result.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.String)
                    return new BrowserResult(false, string.Empty, "CDP did not return screenshot data.");
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(data.GetString()!), ct).ConfigureAwait(false);

                var history = new List<ChatMessage>
                {
                    new ChatMessage
                    {
                        Role = "user",
                        Content = "Inspect this screenshot from PaRuX's isolated developer browser. Goal: "
                            + (string.IsNullOrWhiteSpace(goal) ? "describe visible UI, layout errors and runtime error states" : goal.Trim())
                            + ". Report only visible evidence; do not assume hidden state.",
                        Attachments = { new ChatAttachment { FileName = "browser.png", LocalPath = path, MediaType = "image/png" } }
                    }
                };
                var buffer = new StringBuilder();
                await ChatClient.Current.StreamAsync(
                    history,
                    "You are PaRuX's browser UI verifier. Be precise about visible errors, layout, text, controls and state.",
                    fragment => buffer.Append(fragment), ct).ConfigureAwait(false);
                string observation = buffer.ToString().Trim();
                return new BrowserResult(observation.Length > 0, observation,
                    observation.Length == 0 ? "Vision model returned an empty browser-screenshot observation." : null);
            }
            catch (Exception ex) { return new BrowserResult(false, string.Empty, ex.Message); }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        public async Task ResetAsync(CancellationToken ct)
        {
            await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try { await StopCoreAsync(ct).ConfigureAwait(false); }
            finally { _lifecycleGate.Release(); }
        }

        private async Task EnsureSessionAsync(bool internetEnabled, CancellationToken ct)
        {
            await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_socket is { State: WebSocketState.Open }
                    && _browser is { HasExited: false }
                    && _internetEnabledAtLaunch == internetEnabled
                    && _fetchGuardReady
                    && Volatile.Read(ref _uncontrolledChildTargetDetected) == 0)
                    return;

                await StopCoreAsync(ct).ConfigureAwait(false);
                _internetEnabledAtLaunch = internetEnabled;
                _port = FindFreeLoopbackPort();
                string executable = FindBrowserExecutable()
                    ?? throw new FileNotFoundException("Neither Microsoft Edge nor Google Chrome was found for browser developer tooling.");
                SweepStaleProfilesOnce();
                // A persistent browser profile can transfer cookies and local storage
                // from one unrelated coding task to another. Use a fresh profile for
                // every CDP process and erase it after the process has stopped.
                string profile = Path.Combine(AppPaths.PaRuXRoot, "browser-dev-profile",
                    "session-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(profile);
                _sessionProfileDirectory = profile;

                var info = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
                info.ArgumentList.Add("--remote-debugging-port=" + _port);
                info.ArgumentList.Add("--user-data-dir=" + profile);
                info.ArgumentList.Add("--no-first-run");
                info.ArgumentList.Add("--no-default-browser-check");
                info.ArgumentList.Add("--disable-sync");
                info.ArgumentList.Add("--disable-component-update");
                info.ArgumentList.Add("--disable-background-networking");
                info.ArgumentList.Add("--disable-default-apps");
                info.ArgumentList.Add("--disable-session-crashed-bubble");
                if (!internetEnabled)
                {
                    info.ArgumentList.Add("--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1, EXCLUDE [::1]");
                    // WebSocket and WebRTC traffic does not pass the Fetch guard. Proxied traffic goes to a PaRuX-owned
                    // sink that closes it. WebRTC UDP is not proxied, so only --force-webrtc-ip-handling-policy limits it.
                    // The sink starts before launch, so no browser launched while Internet is OFF starts without it. A
                    // browser launched while ON has no sink, so the switch watch closes it once the switch goes OFF.
                    _egressSink?.Dispose();
                    _egressSink = BrowserOfflineEgressSink.Start();
                    foreach (string switchArgument in BrowserOfflineEgressPolicy.SwitchesFor(_egressSink.Port))
                        info.ArgumentList.Add(switchArgument);
                }
                info.ArgumentList.Add("about:blank");
                _browser = Process.Start(info) ?? throw new InvalidOperationException("Could not start the dedicated developer browser.");
                // The browser joins a job that closes with PaRuX, so a crash cannot leave it running unguarded.
                ChildProcessJob.Assign(_browser);

                Uri websocket = await WaitForPageWebSocketAsync(_port, ct).ConfigureAwait(false);
                _socket = new ClientWebSocket();
                _readerCts = new CancellationTokenSource();
                await _socket.ConnectAsync(websocket, ct).ConfigureAwait(false);
                _readerTask = ReadLoopAsync(_socket, _readerCts.Token);

                // The page-level Fetch guard does not automatically cover
                // popup tabs, service workers or out-of-process iframes.
                // Attach such related targets paused and abort this whole
                // private browser rather than allow an unguarded child.
                // If CDP cannot establish the guard, startup fails closed.
                await SendCommandAsync("Target.setAutoAttach", new {
                    autoAttach = true,
                    waitForDebuggerOnStart = true,
                    flatten = true
                }, ct).ConfigureAwait(false);

                await SendCommandAsync("Page.enable", new { }, ct).ConfigureAwait(false);
                // Deny downloads before any navigation: a download would write outside the private profile.
                (string downloadMethod, object downloadParameters) = BrowserDownloadGuardPolicy.DenyAllDownloads();
                await SendCommandAsync(downloadMethod, downloadParameters, ct).ConfigureAwait(false);
                // Seed the top-level document before any request is admitted: a guard that
                // cannot name the document that issued a request must not allow it.
                _frames.Clear();
                JsonElement frameTree = await SendCommandAsync("Page.getFrameTree", new { }, ct).ConfigureAwait(false);
                _frames.SeedFromFrameTree(frameTree);
                if (_frames.TopFrameId is null)
                    throw new InvalidOperationException("Developer browser did not report a top-level frame; requests cannot be attributed.");
                await SendCommandAsync("Runtime.enable", new { }, ct).ConfigureAwait(false);
                await SendCommandAsync("Log.enable", new { }, ct).ConfigureAwait(false);
                await SendCommandAsync("Network.enable", new { }, ct).ConfigureAwait(false);
                // A local page can redirect or fetch an external URL before the
                // post-navigation location check runs. Intercept EACH request
                // at CDP's request stage, not after the network response.
                // If the browser does not support interception, no browser
                // tool may proceed under a falsely asserted offline boundary.
                await SendCommandAsync("Fetch.enable", new {
                    patterns = new[] { new { urlPattern = "*", requestStage = "Request" } }
                }, ct).ConfigureAwait(false);
                if (Volatile.Read(ref _uncontrolledChildTargetDetected) != 0)
                    throw new InvalidOperationException("Uncontrolled browser target appeared during guard setup.");
                _fetchGuardReady = true;
                // A browser launched while Internet is ON has no egress sink. Watch the switch, and close the browser
                // when it goes OFF, so its WebSocket and WebRTC connections do not outlive the switch.
                if (internetEnabled) StartInternetSwitchWatch();
            }
            finally { _lifecycleGate.Release(); }
        }

        private async Task<JsonElement> EvaluateRawAsync(string expression, bool returnByValue, CancellationToken ct) =>
            await SendCommandAsync("Runtime.evaluate", new
            {
                expression,
                returnByValue,
                awaitPromise = true,
                userGesture = false
            }, ct).ConfigureAwait(false);

        private async Task<JsonElement> SendCommandAsync(string method, object parameters, CancellationToken ct)
        {
            if (Volatile.Read(ref _uncontrolledChildTargetDetected) != 0)
                throw new InvalidOperationException(
                    "Developer browser session was terminated because an unguarded child target appeared.");
            ClientWebSocket socket = _socket ?? throw new InvalidOperationException("Developer browser CDP session is not connected.");
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs)) throw new InvalidOperationException("Duplicate CDP command id.");

            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters }));
            try
            {
                await _sendGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                finally { _sendGate.Release(); }

                using CancellationTokenRegistration registration = ct.Register(
                    () => tcs.TrySetCanceled(ct));
                JsonElement outcome = await tcs.Task.WaitAsync(
                    TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (Volatile.Read(ref _uncontrolledChildTargetDetected) != 0)
                    throw new InvalidOperationException(
                        "Developer browser session was invalidated by an uncontrolled target.");
                return outcome;
            }
            finally
            {
                // A canceled WaitAsync or failed WebSocket.SendAsync used to leak
                // pending completions. Fetch interception increases message
                // volume, so cleanup must cover ALL failures, not just replies.
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            byte[] buffer = new byte[64 * 1024];
            var receiveBuffer = new ArraySegment<byte>(buffer);
            var message = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult receive = await socket.ReceiveAsync(receiveBuffer, ct).ConfigureAwait(false);
                    if (receive.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, receive.Count);
                    if (!receive.EndOfMessage) continue;

                    string json = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("id", out JsonElement idNode) && idNode.TryGetInt32(out int id))
                    {
                        if (_pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? pending))
                        {
                            if (root.TryGetProperty("error", out JsonElement error))
                                pending.TrySetException(new InvalidOperationException("CDP " + error.GetRawText()));
                            else if (root.TryGetProperty("result", out JsonElement result))
                                pending.TrySetResult(result.Clone());
                            else pending.TrySetResult(EmptyObject);
                        }
                        continue;
                    }

                    if (!root.TryGetProperty("method", out JsonElement methodNode) || methodNode.ValueKind != JsonValueKind.String)
                        continue;
                    string method = methodNode.GetString() ?? string.Empty;
                    JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;
                    if (method.StartsWith("Page.", StringComparison.Ordinal))
                        _frames.Apply(method, parameters);
                    if (BrowserAttachedTargetPolicy.MustAbortSession(method, parameters))
                    {
                        // Do not wait for CDP closeTarget: doing so would allow
                        // a worker/page to run while an asynchronous denial
                        // command is pending. Terminate all browser descendants.
                        Interlocked.Exchange(ref _uncontrolledChildTargetDetected, 1);
                        _fetchGuardReady = false;
                        EnqueueBounded(_network,
                            "[blocked] Uncontrolled child browser target; session terminated.");
                        foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
                            pending.TrySetException(new InvalidOperationException(
                                "Uncontrolled browser child target blocked."));
                        try
                        {
                            if (_browser is { HasExited: false })
                                _browser.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            EnqueueBounded(_network,
                                "[child target shutdown failed] " + ex.GetType().Name);
                        }
                        break;
                    }
                    if (method is "Runtime.consoleAPICalled" or "Log.entryAdded")
                        EnqueueBounded(_console, RenderConsoleEvent(method, parameters));
                    else if (method == "Network.responseReceived")
                        EnqueueBounded(_network, RenderNetworkEvent(parameters));
                    else if (method == "Fetch.requestPaused")
                    {
                        // Never await a command from the CDP reader itself:
                        // its response must be processed by this same loop.
                        // Bounded inflight work prevents a hostile page from
                        // spawning an unbounded number of policy lookups.
                        if (Interlocked.Increment(ref _pendingPausedRequests) <= 128)
                            _ = ResolvePausedRequestAsync(parameters.Clone(), ct);
                        else
                        {
                            Interlocked.Decrement(ref _pendingPausedRequests);
                            EnqueueBounded(_network,
                                "[blocked] Too many concurrent intercepted browser requests.");
                            // Fail closed: the unhandled request remains paused.
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
                    pending.TrySetException(new InvalidOperationException("Developer browser CDP reader stopped: " + ex.Message));
            }
            finally { message.Dispose(); }
        }

        private async Task ResolvePausedRequestAsync(JsonElement parameters, CancellationToken ct)
        {
            try
            {
                if (parameters.ValueKind != JsonValueKind.Object
                    || !parameters.TryGetProperty("requestId", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(id.GetString())
                    || !parameters.TryGetProperty("request", out JsonElement request)
                    || request.ValueKind != JsonValueKind.Object
                    || !request.TryGetProperty("url", out JsonElement url)
                    || url.ValueKind != JsonValueKind.String)
                    return; // Invalid CDP event cannot authorize a request.

                string requestId = id.GetString()!;
                string address = url.GetString() ?? string.Empty;
                bool internetEnabled;
                try
                {
                    // Re-read the global toggle for EVERY request: changing it
                    // during navigation must not grant additional authority.
                    // An offline-launched browser never gains wider network
                    // authority mid-session. Avoid a Core HTTP status request
                    // per CSS/JS/image request while that stricter mode is active.
                    // For online sessions, still re-check the switch on EVERY
                    // request so disabling Internet starts denying new egress.
                    internetEnabled = _internetEnabledAtLaunch
                        && await InternetEnabledAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { internetEnabled = false; }

                // Hostnames are resolved here, so a public-looking name that points at
                // loopback, RFC1918 or metadata space is denied before the request is sent.
                // The browser resolves again later: rebinding between the two lookups is
                // a documented limitation of this admission check, not confinement.
                bool allowed = await BrowserRequestAdmissionPolicy.IsAllowedAsync(
                    address, internetEnabled, BrowserResolvedAddressPolicy.SystemResolver, ct).ConfigureAwait(false);
                // A loopback destination also needs a local initiator: the frame chain, the
                // Origin/Referer the browser attached and the latest PaRuX navigation must all
                // be loopback. A public page therefore cannot reach a local port.
                if (allowed && IsLoopbackAddress(address))
                    allowed = BrowserInitiatorPolicy.MayIssueLoopbackRequest(
                        InitiatorEvidence(ReadFrameId(parameters), request));
                if (!allowed)
                {
                    Interlocked.Increment(ref _blockedRequests);
                    EnqueueBounded(_network, "[blocked by PaRuX Internet policy] "
                        + BrowserNetworkEvidencePolicy.DisplayUrl(address));
                }
                object arguments = allowed
                    ? (object)new { requestId }
                    : new { requestId, errorReason = "BlockedByClient" };
                await SendCommandAsync(
                    allowed ? "Fetch.continueRequest" : "Fetch.failRequest",
                    arguments, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // A failed CDP decision does not become implicit allow.
                // The paused request stays paused and the caller times out.
                EnqueueBounded(_network, "[request intercept failed closed] " + ex.GetType().Name);
            }
            finally { Interlocked.Decrement(ref _pendingPausedRequests); }
        }

        private static bool IsLoopbackAddress(string address) =>
            Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            && BrowserInitiatorPolicy.IsLoopbackDestination(uri);

        private static string? ReadFrameId(JsonElement parameters) =>
            parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("frameId", out JsonElement frame)
            && frame.ValueKind == JsonValueKind.String
                ? frame.GetString()
                : null;

        private IReadOnlyList<string?> InitiatorEvidence(string? frameId, JsonElement request)
        {
            var evidence = new List<string?>(_frames.ChainFrom(frameId));
            if (request.ValueKind == JsonValueKind.Object
                && request.TryGetProperty("headers", out JsonElement headers)
                && headers.ValueKind == JsonValueKind.Object)
            {
                // Origin and Referer are attached by the browser and identify the issuing document.
                foreach (JsonProperty header in headers.EnumerateObject())
                {
                    if (header.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                        || header.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                        evidence.Add(header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString() : null);
                }
            }
            string? pending = _pendingNavigationUrl;
            if (!string.IsNullOrEmpty(pending)) evidence.Add(pending);
            return evidence;
        }

        private static string RenderConsoleEvent(string method, JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return "[console] (event parameters unavailable)";
            try
            {
                if (method == "Log.entryAdded" && parameters.TryGetProperty("entry", out JsonElement entry))
                    return "[log " + (entry.TryGetProperty("level", out JsonElement level) ? level.GetString() : "") + "] "
                        + (entry.TryGetProperty("text", out JsonElement text) ? text.GetString() : SafeRaw(entry));
                string type = parameters.TryGetProperty("type", out JsonElement typeNode) ? typeNode.GetString() ?? "console" : "console";
                if (parameters.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array)
                {
                    string rendered = string.Join(" ", args.EnumerateArray().Select(arg =>
                        arg.TryGetProperty("value", out JsonElement value) ? value.ToString()
                        : arg.TryGetProperty("description", out JsonElement description) ? description.GetString() ?? string.Empty
                        : SafeRaw(arg)));
                    return "[console " + type + "] " + rendered;
                }
                return "[console " + type + "] " + SafeRaw(parameters);
            }
            catch { return "[console] " + SafeRaw(parameters); }
        }

        private static string RenderNetworkEvent(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return "[network] (event parameters unavailable)";
            try
            {
                JsonElement response = parameters.GetProperty("response");
                string url = response.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
                double status = response.TryGetProperty("status", out JsonElement s) && s.TryGetDouble(out double code) ? code : 0;
                string mime = response.TryGetProperty("mimeType", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
                return "[network " + status.ToString("0") + "] "
                    + BrowserNetworkEvidencePolicy.DisplayUrl(url)
                    + (mime.Length == 0 ? string.Empty : " (" + mime + ")");
            }
            catch { return "[network] " + SafeRaw(parameters); }
        }

        private static string SafeRaw(JsonElement value)
        {
            try { return value.ValueKind == JsonValueKind.Undefined ? "(undefined)" : value.GetRawText(); }
            catch { return "(unavailable)"; }
        }

        private static JsonElement CreateEmptyObject()
        {
            using JsonDocument document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        private static void EnqueueBounded(ConcurrentQueue<string> queue, string value)
        {
            queue.Enqueue(value.Length <= 2_000 ? value : value[..2_000] + "…");
            while (queue.Count > MaximumEvents) queue.TryDequeue(out _);
        }

        private static string ExtractEvaluationString(JsonElement result)
        {
            if (!result.TryGetProperty("result", out JsonElement remote)) return result.GetRawText();
            if (remote.TryGetProperty("value", out JsonElement value))
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
            if (remote.TryGetProperty("description", out JsonElement description)) return description.GetString() ?? string.Empty;
            return remote.GetRawText();
        }

        private async Task<BrowserResult?> RejectUnsafeCurrentPageAsync(
            bool internetEnabled, CancellationToken ct)
        {
            // Read the page's actual URL AFTER a navigation or JS action.
            // Location can differ from the URL the model requested due to
            // redirects, meta-refresh, in-page navigation and click handlers.
            JsonElement value = await EvaluateRawAsync(
                "window.location.href", returnByValue: true, ct).ConfigureAwait(false);
            string actual = ExtractEvaluationString(value);
            // Recheck the toggle as well as the launch policy. Internet may
            // have been switched OFF while the page was redirecting.
            bool stillAllowedByUser = internetEnabled
                && await InternetEnabledAsync(ct).ConfigureAwait(false);
            if (BrowserRedirectObservationPolicy.IsAllowedPage(actual, stillAllowedByUser))
                return null;

            // Fail closed before collecting another DOM/screenshot or declaring
            // a completed action. about:blank remains inside the agent profile.
            _pendingNavigationUrl = "about:blank";
            await SendCommandAsync("Page.navigate", new { url = "about:blank" }, ct)
                .ConfigureAwait(false);
            return new BrowserResult(false, string.Empty,
                "Browser navigation left the permitted URL scope; returned to about:blank. "
                + "A redirected page never counts as successful tool evidence.");
        }

        private async Task<bool> IsCurrentPageMutationAllowedAsync(CancellationToken ct)
        {
            JsonElement location = await EvaluateRawAsync(
                "window.location.href", returnByValue: true, ct).ConfigureAwait(false);
            return BrowserDevToolsEvaluationPolicy.IsSuccessful(location)
                && BrowserUiMutationPolicy.IsAllowed(ExtractEvaluationString(location));
        }

        private async Task<bool> InternetEnabledAsync(CancellationToken ct) =>
            (await InternetClient.Current.GetStatusAsync(ct).ConfigureAwait(false)).Enabled;

        private static bool TryNormalizeUrl(string value, out Uri? uri, out string? error)
        {
            uri = null;
            error = null;
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                error = "Browser navigation requires an absolute http:// or https:// URL.";
                return false;
            }
            uri = parsed;
            return true;
        }

        internal static bool IsLocal(Uri uri) =>
            uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);

        private static int FindFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string? FindBrowserExecutable()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static async Task<Uri> WaitForPageWebSocketAsync(int port, CancellationToken ct)
        {
            string baseUrl = "http://127.0.0.1:" + port;
            Exception? last = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string json = await Http.GetStringAsync(baseUrl + "/json/list", ct).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out JsonElement type) && type.GetString() == "page"
                            && item.TryGetProperty("webSocketDebuggerUrl", out JsonElement ws)
                            && Uri.TryCreate(ws.GetString(), UriKind.Absolute, out Uri? uri))
                            return uri;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("Dedicated developer browser did not expose a CDP page in time. " + last?.Message);
        }

        private static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Kills the browser process tree without the lifecycle gate, so a stuck
        /// operation cannot keep the browser alive. Bookkeeping is tidied only when the
        /// gate is available within a short bound; otherwise the next session start
        /// finishes it.
        /// </summary>
        private async Task ForceStopBrowserAsync(CancellationToken ct)
        {
            await TerminateBrowserAsync(_browser, ct).ConfigureAwait(false);
            if (!await _lifecycleGate.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)) return;
            try { await FinishBrowserShutdownAsync(ct).ConfigureAwait(false); }
            finally { _lifecycleGate.Release(); }
        }

        /// <summary>
        /// Stops the browser process and removes its profile, but only after the stop is
        /// proven. An unproven stop throws, which keeps the lease locked.
        /// </summary>
        private async Task FinishBrowserShutdownAsync(CancellationToken ct)
        {
            Process? browser = _browser;
            await TerminateBrowserAsync(browser, ct).ConfigureAwait(false);
            browser?.Dispose();
            _browser = null;

            // The next fresh browser may start only after the old process is proven dead.
            Interlocked.Exchange(ref _uncontrolledChildTargetDetected, 0);

            // Delete the profile only after the owning process has exited. A locked
            // file leaves a remnant that the stale-profile sweep removes later.
            string? finishedProfile = _sessionProfileDirectory;
            _sessionProfileDirectory = null;
            if (!string.IsNullOrEmpty(finishedProfile))
            {
                try { if (Directory.Exists(finishedProfile)) Directory.Delete(finishedProfile, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Repeats the kill until the stop is proven, within a bounded wait. The
        /// outcome is decided by the proof, never by the kill call alone.
        /// </summary>
        private async Task TerminateBrowserAsync(Process? browser, CancellationToken ct)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(BrowserExitTimeout);
            try
            {
                while (!IsBrowserProvenStopped(browser))
                {
                    KillBrowserProcessTree(browser);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The bounded wait expired. The proof below decides the outcome.
            }
            if (!IsBrowserProvenStopped(browser))
                throw new InvalidOperationException(
                    "Dedicated developer browser did not exit; isolation lease remains locked.");
        }

        private void KillBrowserProcessTree(Process? browser)
        {
            try { if (browser is { HasExited: false }) browser.Kill(entireProcessTree: true); }
            catch (Exception) { }   // exited between the check and the kill; the proof decides

            // Children can outlive a root that has already exited, so also kill every
            // process that belongs to this session's private profile.
            IReadOnlyList<int> owned;
            try { owned = OwnedBrowserProcessIds(); }
            catch (Exception) { return; }   // the proof decides
            foreach (int processId in owned)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    process.Kill();
                }
                catch (Exception) { }   // already gone, or access denied; the proof decides
            }
        }

        /// <summary>
        /// Proof that no process of this session remains. An exited root is not enough:
        /// a child that still carries the session profile keeps that profile in use.
        /// </summary>
        private bool IsBrowserProvenStopped(Process? browser)
        {
            try
            {
                if (browser is { HasExited: false }) return false;
                return OwnedBrowserProcessIds().Count == 0;
            }
            catch (Exception) { return false; }   // cannot prove the stop: fail closed
        }

        private IReadOnlyList<int> OwnedBrowserProcessIds()
        {
            string? profile = _sessionProfileDirectory;
            if (string.IsNullOrEmpty(profile)) return Array.Empty<int>();
            return BrowserProcessTreePolicy.SelectOwnedProcessIds(
                SnapshotBrowserProcesses(), profile, Environment.ProcessId);
        }

        /// <summary>
        /// Reads the Edge and Chrome processes with their parent ids and command lines.
        /// Ownership is decided by BrowserProcessTreePolicy on this snapshot only.
        /// </summary>
        private static List<BrowserProcessRecord> SnapshotBrowserProcesses()
        {
            var rows = new List<BrowserProcessRecord>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process " +
                "WHERE Name = 'msedge.exe' OR Name = 'chrome.exe'");
            using ManagementObjectCollection found = searcher.Get();
            foreach (ManagementBaseObject item in found)
            {
                rows.Add(new BrowserProcessRecord(
                    Convert.ToInt32(item["ProcessId"]),
                    Convert.ToInt32(item["ParentProcessId"]),
                    item["Name"] as string ?? string.Empty,
                    item["CommandLine"] as string,
                    ReadStartUtc(item["CreationDate"])));
                item.Dispose();
            }
            return rows;
        }

        /// <summary>Converts a WMI CreationDate to UTC. Null when it cannot be read, and the identity check then refuses the row.</summary>
        private static DateTime? ReadStartUtc(object? creationDate)
        {
            try
            {
                return creationDate is string text ? ManagementDateTimeConverter.ToDateTime(text).ToUniversalTime() : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Terminates developer-browser trees that an earlier PaRuX process left running. Only trees whose profile is
        /// a session directory under PaRuX's root are candidates, and only when no live process started them.
        /// </summary>
        private static void ReapOrphanedBrowsers(string profileRoot)
        {
            List<BrowserProcessRecord> snapshot = SnapshotBrowserProcesses();
            IReadOnlyList<int> orphanRoots = BrowserOrphanPolicy.SelectOrphanRoots(snapshot, profileRoot, IsProcessRunning);
            int stopped = 0;
            foreach (int processId in orphanRoots)
            {
                try
                {
                    // A PID can be reused between the snapshot and the kill. Read the processes again just before the kill,
                    // and stop the root only when it is still the selected orphan: same image, command line and start time.
                    BrowserProcessRecord? selected = snapshot.FirstOrDefault(row => row.ProcessId == processId);
                    List<BrowserProcessRecord> fresh = SnapshotBrowserProcesses();
                    BrowserProcessRecord? current = fresh.FirstOrDefault(row => row.ProcessId == processId);
                    if (selected is null || !BrowserOrphanPolicy.IsSameProcess(selected, current)) continue;
                    if (!BrowserOrphanPolicy.SelectOrphanRoots(fresh, profileRoot, IsProcessRunning).Contains(processId)) continue;

                    using Process process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                    stopped++;
                }
                catch (ArgumentException) { }                       // already gone
                catch (InvalidOperationException) { }               // already exited
                catch (System.ComponentModel.Win32Exception) { }    // access denied: a later sweep retries
            }
            if (stopped > 0)
                Log.Write("Developer browser: stopped " + stopped + " browser process(es) left by an earlier run.");
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>
        /// Removes stale per-session profiles once per process, before the first new profile
        /// is created. Only old "session-" directories directly under PaRuX's browser profile
        /// root are candidates, and a profile still referenced by a live browser is kept.
        /// </summary>
        private void SweepStaleProfilesOnce()
        {
            if (Interlocked.Exchange(ref _profileSweepStarted, 1) != 0) return;
            try
            {
                string root = Path.Combine(AppPaths.PaRuXRoot, "browser-dev-profile");
                // Browsers left by an earlier PaRuX process that exited without stopping them are unguarded and may
                // still hold network access. Reap them before any new session starts.
                ReapOrphanedBrowsers(root);
                if (!Directory.Exists(root)) return;
                var entries = Directory.EnumerateDirectories(root, "session-*", SearchOption.TopDirectoryOnly)
                    .Select(dir => new ProfileDirectoryEntry(dir, Directory.GetCreationTimeUtc(dir)))
                    .ToList();
                if (entries.Count == 0) return;

                var live = new List<string>();
                foreach (BrowserProcessRecord row in SnapshotBrowserProcesses())
                {
                    string? profile = BrowserProcessTreePolicy.ProfileDirectoryOf(row.CommandLine);
                    if (!string.IsNullOrEmpty(profile)) live.Add(profile);
                }

                foreach (string stale in BrowserProcessTreePolicy.SelectStaleProfileDirectories(
                    root, entries, DateTime.UtcNow, TimeSpan.FromHours(1), live))
                {
                    try { Directory.Delete(stale, recursive: true); }
                    catch (IOException) { }                 // still in use: a later sweep retries
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Developer browser profile sweep skipped: " + ex.GetType().Name);
            }
        }

        private async Task CloseSocketAsync()
        {
            ClientWebSocket? socket = _socket;
            _socket = null;
            if (socket is null) return;
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reset", bounded.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
            try { socket.Dispose(); } catch (Exception) { }
        }

        private static async Task WaitQuietlyAsync(Task? task, TimeSpan timeout)
        {
            if (task is null) return;
            try { await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false); }
            catch (Exception) { }
        }

        // A browser launched with Internet ON has no egress sink. This watch closes it when the switch goes OFF. Its read
        // uses a server that is already running, so the watch cannot start one, and a failed read counts as OFF.
        private void StartInternetSwitchWatch()
        {
            CancellationTokenSource watch = new();
            _internetWatch = watch;
            _ = BrowserInternetSwitchWatch.RunAsync(
                async ct => (bool?)(await InternetClient.Current.GetStatusIfRunningAsync(ct).ConfigureAwait(false)).Enabled,
                () => CloseForInternetSwitchOffAsync(watch),
                InternetSwitchPollInterval,
                watch.Token,
                failure => Log.Write("Closing the developer browser after an Internet switch-off failed; retrying: " + failure.Message));
        }

        private void CancelInternetSwitchWatch()
        {
            CancellationTokenSource? watch = _internetWatch;
            _internetWatch = null;
            try { watch?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Runs under the lifecycle gate. A stop or a relaunch by another action has already cancelled this watch and closed
        // the browser, so nothing is left to do. A failed close throws, which leaves the watch running for the next poll.
        // The wait for the gate ends with the watch, so a disposed service never leaves this task waiting forever.
        private async Task CloseForInternetSwitchOffAsync(CancellationTokenSource watch)
        {
            await _lifecycleGate.WaitAsync(watch.Token).ConfigureAwait(false);
            try
            {
                if (watch.IsCancellationRequested) return;
                await StopBrowserAsync(CancellationToken.None).ConfigureAwait(false);
                if (ReferenceEquals(_internetWatch, watch)) _internetWatch = null;
                watch.Cancel();
            }
            finally { _lifecycleGate.Release(); }
        }

        private async Task StopCoreAsync(CancellationToken ct)
        {
            CancelInternetSwitchWatch();
            await StopBrowserAsync(ct).ConfigureAwait(false);
        }

        private async Task StopBrowserAsync(CancellationToken ct)
        {
            _fetchGuardReady = false;
            _frames.Clear();
            _pendingNavigationUrl = null;
            Interlocked.Exchange(ref _blockedRequests, 0);
            try { _readerCts?.Cancel(); } catch { }
            await CloseSocketAsync().ConfigureAwait(false);
            await WaitQuietlyAsync(_readerTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            _readerTask = null;
            try { _readerCts?.Dispose(); } catch { }
            _readerCts = null;
            foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
                pending.TrySetCanceled();
            _pending.Clear();
            while (_console.TryDequeue(out _)) { }
            while (_network.TryDequeue(out _)) { }
            // The release lease MUST remain held until the process exit is proven. The sink only refuses, so it is closed
            // only after that proof. When the browser cannot be proven gone, the sink stays up and keeps refusing, and
            // its port is not released while the browser may still be using it.
            await FinishBrowserShutdownAsync(ct).ConfigureAwait(false);
            _egressSink?.Dispose();
            _egressSink = null;
        }

        public async ValueTask DisposeAsync()
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { _lifecycleGate.Release(); }
            _lifecycleGate.Dispose();
            _sendGate.Dispose();
        }
    }
}
