namespace PaRuX.Server;

/// <summary>
/// Owns the PaRuX-managed local AI runtime lifecycle for text and media. Alternative PaRuX text
/// engines join the same lifecycle through the process-wide LocalInferenceEngineCoordinator so
/// runtime transitions release the prior GPU resident before another PaRuX backend starts.
///
/// GPU transitions are coordinated with MediaExecutionCoordinator: shared leases are only for
/// inference against a stable resident; media, explicit model swaps and restores are exclusive GPU
/// writers. A queued writer blocks new inference but lets already-admitted responses drain first.
/// </summary>
public sealed class LocalRuntimeService
{
    public const string ManagedProviderEndpoint = "http://127.0.0.1:1234";
    public static string CurrentManagedProviderEndpoint => "http://127.0.0.1:" + LocalAiPaths.TextPort;
    private static int ManagedProviderPort => LocalAiPaths.TextPort;

    private readonly IHostEnvironment _environment;
    private readonly LocalAiProcessManager _process;
    private readonly object _gamingGate = new();
    private CancellationTokenSource _gamingCancellation = new();

    public LocalRuntimeService(
        IHostEnvironment environment,
        LocalAiProcessManager process,
        ExternalTextProviderService externalProvider)
    {
        _environment = environment;
        _process = process;

        // Child llama-server processes inherit Core's environment. Pin slots monitoring on instead
        // of relying on a llama.cpp default that changed across releases. It stays loopback-only in
        // PaRuX and is used solely to prove quiescence before an exclusive GPU residency change.
        Environment.SetEnvironmentVariable("LLAMA_ARG_ENDPOINT_SLOTS", "1");
    }

    public string ProviderEndpoint => CurrentManagedProviderEndpoint;

    /// <summary>
    /// Whether the managed endpoint is really PaRuX's own runtime rather than whatever else claimed
    /// the port. This property is a prompt-egress boundary, so Windows ownership is revalidated from
    /// the TCP owner table on every call instead of trusting LocalAiProcessManager's short health
    /// cache. A cached positive result can become unsafe if the managed child exits and another
    /// process claims/reuses the port before the TTL expires.
    ///
    /// A host serving the runtime port from inside Core's own process counts as owned, because it
    /// is: Core never binds that port in production, and the overhead benchmarks legitimately do.
    /// On non-Windows hosts the legacy process-owned check remains the compatibility path because
    /// the Windows TCP-owner API is unavailable there; the shipped Windows reference path is
    /// fail-closed and fresh.
    /// </summary>
    public bool ManagedRuntimeOwned
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return UsesExternalProvider || _process.IsManagedTextRuntimeOwned();
            if (ManagedListenerOwnership.IsOwnedByThisProcess(ManagedProviderPort)) return true;
            int? pid = LocalAiProcessManager.GetVerifiedTextLaunchProcessId();
            return pid.HasValue && ManagedListenerOwnership.IsStrictlyOwnedBy(ManagedProviderPort, pid.Value);
        }
    }

    public bool UsesExternalProvider => false;
    public bool GamingMode => !_environment.IsEnvironment("Testing") && _process.GamingActive;

    /// <summary>The Gaming Mode shutdown retry state, shared with the runtime status.</summary>
    internal GamingShutdownRetry GamingShutdown => _process.GamingShutdown;

    /// <summary>
    /// Shared inference lease for an operation that uses the currently stable text resident without
    /// replacing it. Multiple callers may coexist; an already-queued exclusive GPU transition blocks
    /// new readers. Keep the lease until the provider response/stream has completely finished.
    /// </summary>
    public ValueTask<MediaExecutionCoordinator.Lease> AcquireTextInferenceAsync(
        string owner,
        CancellationToken cancellationToken) =>
        MediaExecutionCoordinator.Current.AcquireTextAsync(owner, cancellationToken);

    public CancellationToken GamingCancellationToken
    {
        get { lock (_gamingGate) return _gamingCancellation.Token; }
    }

    /// <summary>
    /// Runs on every Gaming transition. A game starting or exiting changes the GPU's free memory
    /// immediately, so the cached free-VRAM sample is dropped and the next tuning, admission or launch
    /// decision measures again. Replaced in tests; defaults to the process-wide telemetry cache.
    /// </summary>
    internal Action InvalidateGpuSamples { get; set; } = CachedGpuPerformanceTelemetrySnapshotProvider.InvalidateShared;

    public void SetGamingState(bool active)
    {
        lock (_gamingGate)
        {
            if (active)
            {
                if (!_gamingCancellation.IsCancellationRequested) _gamingCancellation.Cancel();
            }
            else if (_gamingCancellation.IsCancellationRequested)
            {
                _gamingCancellation.Dispose();
                _gamingCancellation = new CancellationTokenSource();
            }
        }
        InvalidateGpuSamples();
    }

    /// <summary>
    /// The counterpart of ForceGamingShutdownAsync: removes the persisted game marker so local AI
    /// may start again. The workload orchestrator's Return to AI never did this, so after one use
    /// of Settings > Enter Gaming every AI request stayed refused, even across restarts.
    /// </summary>
    public async Task EndGamingAsync(CancellationToken cancellationToken)
    {
        await _process.RunActionAsync("GameStop", cancellationToken).ConfigureAwait(false);
        // The marker is gone before the guard's next tick notices; a launch that follows immediately
        // must not read a free-VRAM sample taken while the game still held the card.
        InvalidateGpuSamples();
    }

    /// <returns>True only when every local AI runtime stopped. False means one is still holding resources, so the
    /// caller has to retry; the result is never discarded.</returns>
    public async Task<bool> ForceGamingShutdownAsync(CancellationToken cancellationToken)
    {
        // The marker is written first (it also stops the managed text/media children) so nothing can restart while
        // releasing. Every step is attempted even if another fails, and a step that did not release its resource is
        // reported as not stopped: an alternative-engine failure must not skip the marker, and reporting Gaming Mode
        // as handled while a resident still holds VRAM the game needs would be a fake success.
        var failures = new List<string>();
        try
        {
            var marker = await _process.RunActionAsync("GameStart", cancellationToken).ConfigureAwait(false);
            if (!marker.Success) failures.Add(marker.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failures.Add("local runtime stop failed: " + ex.Message); }

        try { await StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failures.Add("alternative engine stop failed: " + ex.Message); }

        // The AI's own VRAM was just released (or an attempt was made): drop any sample that still shows it.
        InvalidateGpuSamples();
        if (failures.Count == 0) return true;
        System.Diagnostics.Trace.TraceWarning("Gaming hard-off did not release every local AI resource: " + string.Join("; ", failures));
        return false;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        LocalInferenceEngineCoordinator? coordinator = LocalInferenceEngineSharedServices.TryGetCoordinator();
        if (coordinator != null
            && await coordinator.GetActiveAlternativeStatusAsync(cancellationToken).ConfigureAwait(false) is { Healthy: true })
            return true;

        return await _process.IsTextHealthyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly TimeSpan EnsureReadyTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MediaTextDrainTimeout = TimeSpan.FromMinutes(10);

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        // A GPU writer that is already queued must not be starved by a new model load. If a writer
        // arrives while this cold load is in progress, the post-start check waits for that writer to
        // finish and then repairs/revalidates text before returning to the caller.
        while (true)
        {
            await MediaExecutionCoordinator.Current
                .WaitForMediaAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (GamingMode) throw new InvalidOperationException("Gaming Mode is active, so local AI is unavailable.");

            // Routing may have just loaded a non-default role for this very request. Readiness
            // means that PaRuX owns a healthy resident; calling the plain "Ensure" action here
            // would replace that routed model with Standard before inference begins. In particular,
            // every coding-agent step used to alternate coding -> Standard in the same chat call.
            if (await _process.IsTextHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                MediaExecutionCoordinator.Status residentScheduling = MediaExecutionCoordinator.Current.Snapshot();
                if (residentScheduling.ActiveOwner == null && residentScheduling.Waiting == 0) return;

                await MediaExecutionCoordinator.Current
                    .WaitForMediaAvailabilityAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Only a genuinely cold or unhealthy resident needs admission and a default-model
            // start. A writer transition gets priority and forces the loop to recheck afterward.
            await EnforceExecutionResourceAdmissionAsync(cancellationToken).ConfigureAwait(false);

            LocalInferenceEngineCoordinator? coordinator = LocalInferenceEngineSharedServices.TryGetCoordinator();
            if (coordinator != null)
            {
                await coordinator.WaitForAlternativeEnginesIdleAsync(cancellationToken).ConfigureAwait(false);
                await coordinator.StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false);
            }

            Task<LocalAiProcessManager.RuntimeResult> start = _process.RunActionAsync("Ensure", cancellationToken);
            LocalAiProcessManager.RuntimeResult result;
            try
            {
                result = await start.WaitAsync(EnsureReadyTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The PaRuX local model did not become ready within six minutes.");
            }
            if (!result.Success) throw new InvalidOperationException(result.Message);

            MediaExecutionCoordinator.Status scheduling = MediaExecutionCoordinator.Current.Snapshot();
            if (scheduling.ActiveOwner == null && scheduling.Waiting == 0) return;

            await MediaExecutionCoordinator.Current
                .WaitForMediaAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores the user's default text profile as an exclusive GPU transition. This is used by
    /// media debounce/race cleanup and temporary-model features. Merely waiting for "no media" is
    /// insufficient: a Model Compare/Group Chat or ordinary chat may be actively using another
    /// resident. The exclusive lease drains those responses and prevents new ones from entering
    /// until the default model is safely restored.
    /// </summary>
    public async Task<RuntimeResult> RestoreDefaultTextExclusivelyAsync(CancellationToken cancellationToken)
    {
        await using MediaExecutionCoordinator.Lease exclusive = await MediaExecutionCoordinator.Current
            .AcquireAsync("text-exclusive:restore-default", cancellationToken).ConfigureAwait(false);
        if (GamingMode)
            return new RuntimeResult(false, "Gaming Mode is active, so the default text model will not be restored yet.");

        await _process.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        LocalInferenceEngineCoordinator? coordinator = LocalInferenceEngineSharedServices.TryGetCoordinator();
        if (coordinator != null)
        {
            await coordinator.WaitForAlternativeEnginesIdleAsync(cancellationToken).ConfigureAwait(false);
            await coordinator.StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Also respect a loopback client that bypassed PaRuX's HTTP surfaces and talked directly to
        // llama-server. The same fail-closed slot evidence used for media takeover applies here.
        await TextInferenceQuiescence.WaitForIdleAsync(
            MediaTextDrainTimeout, cancellationToken).ConfigureAwait(false);

        LocalAiProcessManager.RuntimeResult result = await _process
            .RestoreTextProfileAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeResult(result.Success, result.Message);
    }

    /// <summary>
    /// Called only after the caller owns MediaExecutionCoordinator's exclusive GPU lease. It drains
    /// admitted text work, then stops alternative residents. The normal llama.cpp resident is left
    /// alive until the media runtime's existing transition code stops it; this method proves doing
    /// so is safe rather than racing an active response.
    /// </summary>
    public async Task PrepareForExclusiveMediaAsync(CancellationToken cancellationToken)
    {
        MediaExecutionCoordinator.Status scheduling = MediaExecutionCoordinator.Current.Snapshot();
        if (scheduling.ActiveOwner == null)
            throw new InvalidOperationException("Exclusive media preparation requires an active GPU execution lease.");
        if (scheduling.ActiveTextReaders != 0)
            throw new InvalidOperationException("Exclusive GPU lease was acquired while text readers were still active.");
        if (GamingMode) throw new InvalidOperationException("Gaming Mode is active, so media execution is unavailable.");

        await _process.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);

        LocalInferenceEngineCoordinator? coordinator = LocalInferenceEngineSharedServices.TryGetCoordinator();
        if (coordinator != null)
        {
            await coordinator.WaitForAlternativeEnginesIdleAsync(cancellationToken).ConfigureAwait(false);
            await coordinator.StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false);
        }

        await TextInferenceQuiescence.WaitForIdleAsync(
            MediaTextDrainTimeout, cancellationToken).ConfigureAwait(false);
        if (GamingMode) throw new InvalidOperationException("Gaming Mode became active while media execution was waiting for text to drain.");
    }

    /// <summary>
    /// Starts the media assistant and ComfyUI before admission so the guard measures the actual
    /// post-transition process working sets instead of crediting the outgoing text model while
    /// ignoring the incoming media runtime. Call only after <see cref="PrepareForExclusiveMediaAsync"/>
    /// has drained active text readers and the caller owns the exclusive media lease.
    /// </summary>
    internal async Task PrepareMediaRuntimeForAdmissionAsync(CancellationToken cancellationToken)
    {
        MediaExecutionCoordinator.Status scheduling = MediaExecutionCoordinator.Current.Snapshot();
        if (scheduling.ActiveOwner == null || scheduling.ActiveTextReaders != 0)
            throw new InvalidOperationException("Starting the media runtime for admission requires the exclusive GPU lease.");
        if (GamingMode) throw new InvalidOperationException("Gaming Mode is active, so media generation is unavailable.");

        PaRuX.Contracts.ModelExecutionProfile profile = ExecutionProfileStore.Current.Get().Effective;
        ExecutionResourceDecision thermalAdmission = await ExecutionResourceGuard.CheckCurrentAsync(
            profile,
            enforceCurrentVram: false,
            cancellationToken,
            enforceCurrentSystemRam: false).ConfigureAwait(false);
        if (!thermalAdmission.Allowed) throw new InvalidOperationException(thermalAdmission.Message);

        LocalAiProcessManager.RuntimeResult started = await _process.StartMediaAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Success)
            throw new InvalidOperationException("Could not start the local media runtime for resource admission: " + started.Message);
    }

    /// <summary>
    /// Checks resources only after the media transition is live. No resident RAM is subtracted:
    /// this includes the media assistant even when it was already warm and reused.
    /// </summary>
    internal static Task<ExecutionResourceDecision> CheckMediaWorkloadAdmissionAsync(CancellationToken cancellationToken)
    {
        PaRuX.Contracts.ModelExecutionProfile profile = ExecutionProfileStore.Current.Get().Effective;
        return ExecutionResourceGuard.CheckCurrentAsync(profile, enforceCurrentVram: false, cancellationToken);
    }

    public async Task<RuntimeResult> RunActionAsync(string action, CancellationToken cancellationToken)
    {
        if (!new[] { "Ensure", "Auto", "On", "Off", "Standard", "Uncensored", "MediaStart", "MediaStop", "GameStart", "GameStop" }
                .Contains(action, StringComparer.OrdinalIgnoreCase))
            return new RuntimeResult(false, "Unsupported runtime action.");

        string normalized = action.Trim().ToLowerInvariant();
        if (IsTextStartAction(normalized))
            await MediaExecutionCoordinator.Current.WaitForMediaAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        bool canReuseHealthyResident = normalized is "ensure" or "auto"
            && await _process.IsTextHealthyAsync(cancellationToken).ConfigureAwait(false);
        if (RequiresResourceAdmission(action) && !canReuseHealthyResident)
        {
            try
            {
                // A profile/media transition replaces the managed text resident. Count the
                // machine as it will look after that process exits, otherwise the old 27B model
                // rejects its own replacement for high RAM before PaRuX gets a chance to stop it.
                // Only the measured working set of PaRuX's tracked llama-server is discounted;
                // memory owned by every other application remains subject to the guardrail.
                await EnforceExecutionResourceAdmissionAsync(
                    cancellationToken,
                    _process.GetTextWorkingSetBytes()).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) { return new RuntimeResult(false, ex.Message); }
        }

        if (normalized == "mediastart")
        {
            await using MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current
                .AcquireAsync("runtime-action:media-start", cancellationToken).ConfigureAwait(false);
            try { await PrepareForExclusiveMediaAsync(cancellationToken).ConfigureAwait(false); }
            catch (TextRuntimeStateUnavailableException) { throw; } // typed 503 at the HTTP edge, not an untyped 409
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                return new RuntimeResult(false, ex.Message);
            }
            await StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false);
            LocalAiProcessManager.RuntimeResult mediaResult = await _process.RunActionAsync(action, cancellationToken).ConfigureAwait(false);
            return new RuntimeResult(mediaResult.Success, mediaResult.Message);
        }

        if (IsTextStartAction(normalized))
        {
            LocalInferenceEngineCoordinator? coordinator = LocalInferenceEngineSharedServices.TryGetCoordinator();
            if (coordinator != null)
            {
                await coordinator.WaitForAlternativeEnginesIdleAsync(cancellationToken).ConfigureAwait(false);
                await coordinator.StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await StopAlternativeEnginesAsync(cancellationToken).ConfigureAwait(false);
        }

        LocalAiProcessManager.RuntimeResult result = await _process.RunActionAsync(action, cancellationToken).ConfigureAwait(false);
        // The marker changed; the request-refusing cancellation must follow it now, not at the guard's next poll,
        // or the first request after GameStop is refused as "Gaming Mode activated" while status says it is off.
        if (result.Success && normalized == "gamestop") SetGamingState(false);
        else if (result.Success && normalized == "gamestart") SetGamingState(true);
        return new RuntimeResult(result.Success, result.Message);
    }

    internal static bool RequiresResourceAdmission(string? action) => (action ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ensure" or "auto" or "on" or "standard" or "uncensored" or "mediastart" => true,
        _ => false
    };

    internal static bool IsTextStartAction(string? action) => (action ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ensure" or "auto" or "on" or "standard" or "uncensored" => true,
        _ => false
    };

    private static async Task EnforceExecutionResourceAdmissionAsync(
        CancellationToken cancellationToken,
        long reclaimableSystemRamBytes = 0)
    {
        PaRuX.Contracts.ModelExecutionProfile profile = ExecutionProfileStore.Current.Get().Effective;
        ExecutionResourceDecision decision = await ExecutionResourceGuard.CheckCurrentAsync(
            profile,
            enforceCurrentVram: false,
            cancellationToken,
            reclaimableSystemRamBytes).ConfigureAwait(false);
        if (!decision.Allowed) throw new InvalidOperationException(decision.Message);
    }

    private static Task StopAlternativeEnginesAsync(CancellationToken cancellationToken)
    {
        LocalInferenceEngineCoordinator? coordinator = LocalInferenceEngineSharedServices.TryGetCoordinator();
        return coordinator?.StopAlternativeEnginesAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public sealed record RuntimeResult(bool Success, string Message);
}
