using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Proves that the managed llama-server has no request currently processing before media takes the
/// GPU away from it. A media writer has already blocked new PaRuX text inference through
/// MediaExecutionCoordinator when this runs, so the slot count can only drain for PaRuX-owned
/// callers. The slot probe additionally catches loopback tools talking directly to llama-server.
///
/// llama.cpp router mode does not reliably expose /slots on the router itself. When PaRuX's hot
/// model pool is active, the router's /v1/models metadata is therefore used to discover each loaded
/// child port and the child /slots endpoints are probed directly. If trustworthy state cannot be
/// obtained, the swap fails closed instead of truncating an unknown request.
/// </summary>
public static class TextInferenceQuiescence
{
    private static int TextPort => LocalAiPaths.TextPort;
    private static string ProviderEndpoint => LocalRuntimeService.CurrentManagedProviderEndpoint;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        DateTime deadline = DateTime.UtcNow + timeout;
        bool observedBusy = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SlotProbe probe = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            switch (probe.State)
            {
                case SlotState.TextServerAbsent:
                    return;
                case SlotState.Unsupported when probe.ServerResponded:
                    // The server answered but exposes no trustworthy slot table (endpoint disabled,
                    // older/optimised launch profile, router without /slots). PaRuX's own request
                    // leases are the authoritative record of PaRuX-originated work, so absence of
                    // slot data alone is not a reason to refuse. Wait for those leases to drain and
                    // refuse only on positive evidence that something else is generating.
                    if (MediaExecutionCoordinator.Current.Snapshot().ActiveTextReaders > 0)
                    {
                        observedBusy = true;
                        break;
                    }
                    if (await HasPositiveBusyEvidenceAsync(cancellationToken).ConfigureAwait(false))
                    {
                        observedBusy = true;
                        break;
                    }
                    return;
                case SlotState.Unsupported:
                    throw new TextRuntimeStateUnavailableException(
                        "The active managed text runtime does not expose trustworthy slot processing state; refusing to swap it out during media work because an in-flight chat could be truncated.");
                case SlotState.Idle:
                    return;
                case SlotState.Busy:
                    observedBusy = true;
                    break;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(observedBusy
            ? "Timed out waiting for active text-inference slots to drain before media GPU takeover."
            : "Timed out proving text-inference quiescence before media GPU takeover.");
    }

    private static Func<CancellationToken, Task<bool>>? _busyEvidenceOverride;

    internal static IDisposable OverrideBusyEvidenceForTests(Func<CancellationToken, Task<bool>> probe)
    {
        Func<CancellationToken, Task<bool>>? previous = _busyEvidenceOverride;
        _busyEvidenceOverride = probe;
        return new Restore(() => _busyEvidenceOverride = previous);
    }

    /// <summary>
    /// Positive evidence that a text server with no slot table is generating right now: the process
    /// that owns the text port burned at least half a core over a one second window. Unreadable
    /// ownership or CPU is not evidence.
    /// </summary>
    private static async Task<bool> HasPositiveBusyEvidenceAsync(CancellationToken cancellationToken)
    {
        if (_busyEvidenceOverride is { } probe) return await probe(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<int> owners = OperatingSystem.IsWindows()
                ? ManagedListenerOwnership.FindListenerOwners(TextPort).Where(pid => pid > 0).ToList()
                : new List<int>();
            if (owners.Count == 0) return false;
            using var process = System.Diagnostics.Process.GetProcessById(owners[0]);
            TimeSpan before = process.TotalProcessorTime;
            DateTime started = DateTime.UtcNow;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            process.Refresh();
            double wall = (DateTime.UtcNow - started).TotalSeconds;
            return wall > 0 && (process.TotalProcessorTime - before).TotalSeconds / wall >= 0.5;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    internal static async Task<SlotProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        SlotProbe primary = await ProbeSlotsEndpointAsync(
            ProviderEndpoint + "/slots", TextPort, cancellationToken).ConfigureAwait(false);
        if (primary.State != SlotState.Unsupported) return primary;

        // Router mode has shipped builds where /slots on the router is unavailable even though each
        // model child has the normal server endpoint. PaRuX's own hot-pool catalog tells us when it
        // is legitimate to use router discovery; otherwise an unsupported primary endpoint remains
        // a hard failure rather than being guessed away.
        if (!HotModelPoolCatalog.IsActive) return primary;
        SlotProbe router = await ProbeRouterChildrenAsync(cancellationToken).ConfigureAwait(false);
        return router.State == SlotState.Unsupported
            ? router with { ServerResponded = primary.ServerResponded }
            : router;
    }

    private static async Task<SlotProbe> ProbeRouterChildrenAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using HttpResponseMessage response = await Http.GetAsync(
                ProviderEndpoint + "/v1/models", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new SlotProbe(SlotState.Unsupported, 0, 0);

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
                return new SlotProbe(SlotState.Unsupported, 0, 0);

            var ports = new HashSet<int>();
            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object
                    || !model.TryGetProperty("port", out JsonElement portElement)
                    || !portElement.TryGetInt32(out int port)
                    || port is <= 0 or > 65535
                    || port == TextPort)
                    continue;
                ports.Add(port);
            }

            // A PaRuX hot pool is marked active only after a routed child has passed a real
            // completion self-test. No discoverable child port at that point means the router API
            // cannot prove the state required for a safe takeover.
            if (ports.Count == 0)
                return new SlotProbe(SlotState.Unsupported, 0, 0);

            int totalSlots = 0;
            int totalProcessing = 0;
            foreach (int port in ports)
            {
                SlotProbe child = await ProbeSlotsEndpointAsync(
                    "http://127.0.0.1:" + port + "/slots", port, cancellationToken).ConfigureAwait(false);
                if (child.State is SlotState.Unsupported or SlotState.TextServerAbsent)
                    return new SlotProbe(SlotState.Unsupported, totalSlots, totalProcessing);
                totalSlots += child.Slots;
                totalProcessing += child.Processing;
            }

            return new SlotProbe(
                totalProcessing == 0 ? SlotState.Idle : SlotState.Busy,
                totalSlots,
                totalProcessing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new SlotProbe(SlotState.Unsupported, 0, 0); }
        catch (HttpRequestException) { return new SlotProbe(SlotState.Unsupported, 0, 0); }
        catch (JsonException) { return new SlotProbe(SlotState.Unsupported, 0, 0); }
    }

    private static async Task<SlotProbe> ProbeSlotsEndpointAsync(
        string endpoint,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using HttpResponseMessage response = await Http.GetAsync(endpoint, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new SlotProbe(SlotState.Unsupported, 0, 0, ServerResponded: true);

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            SlotProbe parsed = ParseSlots(document.RootElement);
            return parsed.State == SlotState.Unsupported ? parsed with { ServerResponded = true } : parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // A connect attempt to a genuinely closed loopback port does not always fail with an
            // immediate refusal on every machine (network virtualization/VPN/firewall filtering can
            // delay it past this probe's own timeout), so the timeout racing the real refusal is not
            // by itself evidence a live server is unreachable. Fall back to the same OS-level
            // listener-table check the HttpRequestException branch below already trusts.
            return IsLiveListener(port)
                ? new SlotProbe(SlotState.Unsupported, 0, 0)
                : new SlotProbe(SlotState.TextServerAbsent, 0, 0);
        }
        catch (HttpRequestException)
        {
            // A failed HTTP connection is safe evidence of absence only when the OS agrees that no
            // process is listening. Connection reset/overload on a live server is not idleness.
            return IsLiveListener(port)
                ? new SlotProbe(SlotState.Unsupported, 0, 0)
                : new SlotProbe(SlotState.TextServerAbsent, 0, 0);
        }
        catch (JsonException)
        {
            return new SlotProbe(SlotState.Unsupported, 0, 0, ServerResponded: true);
        }
    }

    internal static SlotProbe ParseSlots(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return new SlotProbe(SlotState.Unsupported, 0, 0);

        int slots = 0;
        int processing = 0;
        foreach (JsonElement item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            slots++;
            if (!item.TryGetProperty("is_processing", out JsonElement processingElement)
                || processingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new SlotProbe(SlotState.Unsupported, slots, processing);
            if (processingElement.GetBoolean()) processing++;
        }
        if (slots == 0) return new SlotProbe(SlotState.Unsupported, 0, 0);
        return new SlotProbe(processing == 0 ? SlotState.Idle : SlotState.Busy, slots, processing);
    }

    private static Func<int, IReadOnlyList<int>>? _listenerOwnerReader;

    internal static IDisposable OverrideListenerOwnersForTests(Func<int, IReadOnlyList<int>> reader)
    {
        Func<int, IReadOnlyList<int>>? previous = _listenerOwnerReader;
        _listenerOwnerReader = reader;
        return new Restore(() => _listenerOwnerReader = previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _restore;
        public Restore(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    /// <summary>
    /// A listener that cannot be reached over HTTP only blocks a swap when a live process owns it.
    /// Windows keeps orphaned listener rows with owning PID 0 after a crashed or killed server; no
    /// process can be serving a request on such a socket, so it is "not ours" and cannot hold an
    /// in-flight chat. Unreadable ownership stays fail-closed (treated as live).
    /// </summary>
    private static bool IsLiveListener(int port)
    {
        try
        {
            if (_listenerOwnerReader is { } reader) return reader(port).Any(pid => pid > 0);
            if (!OperatingSystem.IsWindows())
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
            return ManagedListenerOwnership.FindListenerOwners(port).Any(pid => pid > 0);
        }
        catch
        {
            // Failure to inspect the listener table is not evidence of absence.
            return true;
        }
    }

    internal enum SlotState
    {
        TextServerAbsent,
        Idle,
        Busy,
        Unsupported
    }

    internal sealed record SlotProbe(SlotState State, int Slots, int Processing, bool ServerResponded = false);
}

/// <summary>
/// The text runtime's slot state could not be proven idle, so a model/GPU swap was refused. Typed so
/// HTTP callers can answer with a structured error instead of an empty 500.
/// </summary>
public sealed class TextRuntimeStateUnavailableException : InvalidOperationException
{
    public const string ErrorCode = "text_runtime_state_unavailable";
    public TextRuntimeStateUnavailableException(string message) : base(message) { }
}
