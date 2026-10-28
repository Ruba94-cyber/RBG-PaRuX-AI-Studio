using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Keeps every native AI child tied to Core lifetime.</summary>
internal static class ChildProcessJob
{
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private static readonly IntPtr Handle = Create();

    private static IntPtr Create()
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
            };
            uint size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size) ? job : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// True only when the child is really bound to the kill-on-close job. A missing job or a failed
    /// assignment is reported (never silently ignored): the child could then outlive Core, which is
    /// why ownership never trusts a runtime this session did not itself launch and record.
    /// </summary>
    public static bool Assign(Process process)
    {
        if (Handle == IntPtr.Zero)
        {
            Trace.TraceWarning("PaRuX child-process job object is unavailable; child processes are not bound to Core lifetime.");
            return false;
        }
        try
        {
            if (AssignProcessToJobObject(Handle, process.Handle)) return true;
            Trace.TraceWarning("PaRuX could not assign pid " + process.Id + " to the child-process job (Win32 error "
                + Marshal.GetLastWin32Error() + ").");
            return false;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("PaRuX child-process job assignment failed: " + ex.Message);
            return false;
        }
    }
}

/// <summary>
/// Native local-AI lifecycle. Text/media transitions remain serialized. Arbitrary managed text
/// loads may use llama.cpp router mode when a useful VRAM-safe pool exists; router mode is accepted
/// only after CLI capability detection and a real one-token forwarding self-test. Any failure falls
/// back to the established single-model path in the same request.
/// </summary>
public sealed class LocalAiProcessManager
{
    private int _vramMibOverrideForTests = -1;

    public LocalAiProcessManager() { }

    internal LocalAiProcessManager(int vramMibOverrideForTests)
    {
        if (vramMibOverrideForTests < 0) throw new ArgumentOutOfRangeException(nameof(vramMibOverrideForTests));
        _vramMibOverrideForTests = vramMibOverrideForTests;
    }

    /// <summary>The adapter inventory behind the CPU-only launch decision. Production reads DXGI; tests inject a fixed one.</summary>
    internal Func<GpuInventoryStatus> InventoryProbe { get; set; } = () => new GpuInventoryService().GetStatus();

    /// <summary>Test seam: when set, a launch reports this result instead of starting a process, so the arguments the
    /// planner chose can be asserted. Production code never sets it.</summary>
    internal Func<string, IReadOnlyList<string>, RuntimeResult>? LaunchInterceptForTests { get; set; }

    /// <summary>Gaming Mode shutdown retry state. The Gaming guard drives it and the runtime status reports it.</summary>
    internal GamingShutdownRetry GamingShutdown { get; } = new();

    /// <summary>Test seam: replaces the text health probe for this instance. The canonical ports belong to whatever else runs
    /// on the host, so a test of another behaviour can fix the precondition. Production code never sets it.</summary>
    internal Func<CancellationToken, Task<bool>>? TextHealthProbeForTests { get; set; }

    /// <summary>Test seam: replaces the media health probe for this instance. See <see cref="TextHealthProbeForTests"/>.</summary>
    internal Func<CancellationToken, Task<bool>>? MediaHealthProbeForTests { get; set; }

    /// <summary>Test seam: replaces the post-health output sanity probe for this instance. Production code never sets it.</summary>
    internal Func<CancellationToken, Task<RuntimeOutputSanity.Verdict>>? TextOutputSanityProbeForTests { get; set; }

    /// <summary>Test seam: substitutes router capability detection while keeping the pool launch itself real.</summary>
    internal Func<string, string, CancellationToken, Task<bool>>? HotModelRouterSupportProbeForTests { get; set; }

    /// <summary>Test seam: substitutes the post-launch route check while keeping the pool launch itself real.</summary>
    internal Func<string, CancellationToken, Task<bool>>? HotModelRouteValidationProbeForTests { get; set; }

    /// <summary>One entry per attempt of the most recent text start (the launch ladder), empty before any start.</summary>
    public IReadOnlyList<string> LastTextLaunchTrace => _lastTextLaunchTrace;
    private IReadOnlyList<string> _lastTextLaunchTrace = [];

    /// <summary>
    /// Why the most recent text launch used the flags it did (one line per decision, with the reason), or the reason no plan
    /// was applied. Empty before any start. Every speed statement in it is labelled unmeasured.
    /// </summary>
    public string LastLaunchPlanExplanation => _lastLaunchPlanExplanation;
    private string _lastLaunchPlanExplanation = string.Empty;

    /// <summary>How the current text runtime was launched, kept so a switch that fails can restore it. Cleared on every stop.</summary>
    private TextLaunchSnapshot? _lastTextLaunch;

    private sealed record TextLaunchSnapshot(string ModelPath, string Executable, string RuntimeDirectory,
        IReadOnlyList<string> Arguments, string ProfileLabel, LlamaPerformanceProfileDto? LaunchProfile);

    /// <summary>Client for the output sanity probe. It has no timeout of its own because the probe bounds itself.</summary>
    private static readonly HttpClient OutputSanityHttp = new(ManagedRuntimePeers.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    internal readonly record struct TextProcessIdentity(int ProcessId, long StartTimeUtcTicks);

    private sealed record LaunchIdentityBox(ExpectedPeerIdentity Identity, TextLaunchDescriptor? Descriptor);
    private static LaunchIdentityBox? _textLaunchIdentity;

    /// <summary>
    /// The identity (PID + creation time + image path) of the text runtime PaRuX itself launched, captured
    /// right after Process.Start. This - not the PID file, which only checks the process NAME and so
    /// accepts any recycled PID that happens to be a llama-server - is what connected-peer attestation
    /// trusts. If the capture fails the record is cleared, so attestation refuses rather than guesses.
    /// </summary>
    internal static void RecordTextLaunchIdentity(int processId) => RecordTextLaunchIdentity(processId, null);

    /// <summary>
    /// Same as <see cref="RecordTextLaunchIdentity(int)"/>, additionally binding the versioned launch
    /// descriptor derived from the exact argv/environment handed to that process. Identity and
    /// descriptor are published as one immutable record, so a reader can never pair one launch's
    /// identity with another launch's arguments.
    /// </summary>
    internal static void RecordTextLaunchIdentity(int processId, TextLaunchDescriptor? descriptor) =>
        Volatile.Write(
            ref _textLaunchIdentity,
            ConnectedPeerAttestation.TryCaptureIdentity(processId) is { } captured
                ? new LaunchIdentityBox(captured, descriptor) : null);

    /// <summary>
    /// The launch descriptor of the text runtime THIS Core session launched, returned only while that
    /// exact process (PID + creation time + image) is still alive. Null when there is no record, the
    /// process was replaced/recycled, the launch was a router/unsupported argv, or Core restarted -
    /// every consumer must treat null as "actual launch unknown" and fail closed.
    /// </summary>
    internal static TextLaunchDescriptor? GetVerifiedTextLaunchDescriptor(IPeerProcessInspector? inspector = null)
    {
        LaunchIdentityBox? recorded = Volatile.Read(ref _textLaunchIdentity);
        if (recorded?.Descriptor is null) return null;
        ExpectedPeerIdentity? live = ConnectedPeerAttestation.TryCaptureIdentity(recorded.Identity.ProcessId, inspector);
        return live is { } now
            && now.StartTimeUtcTicks == recorded.Identity.StartTimeUtcTicks
            && ConnectedPeerAttestation.SameImage(recorded.Identity.ImagePath, now.ImagePath)
            ? recorded.Descriptor
            : null;
    }

    /// <summary>
    /// Whether an executable image lives under PaRuX's own runtime root. On Windows both sides are compared as final
    /// (symlink, junction and subst resolved) paths, so a link cannot make an outside image look like PaRuX's runtime.
    /// Failing to resolve either side means "not under the root", and therefore never a kill.
    /// </summary>
    private static bool IsImageUnderRuntimeRoot(string image)
    {
        if (!OperatingSystem.IsWindows()) return IsUnderDirectory(image, LocalAiPaths.RuntimeRoot);
        try
        {
            string? root = ResolveFinalPath(Path.GetFullPath(LocalAiPaths.RuntimeRoot));
            string? resolved = ResolveFinalPath(Path.GetFullPath(image));
            if (root is null || resolved is null) return false;
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Final on-disk path of an existing file or directory (GetFinalPathNameByHandle), or null.</summary>
    internal static string? ResolveFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const uint FileShareAll = 0x7;           // read | write | delete
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000; // required to open directories
        IntPtr handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return null;
            }
            string resolved = buffer.ToString();
            if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) return "\\\\" + resolved[8..];
            return resolved.StartsWith("\\\\?\\", StringComparison.Ordinal) ? resolved[4..] : resolved;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static void ClearTextLaunchIdentity() => Volatile.Write(ref _textLaunchIdentity, null);

    private static string? _lastTextLaunchDescriptorRefusal;

    /// <summary>Bounded refusal reason of the most recent text launch descriptor (null when describable).</summary>
    internal static string? LastTextLaunchDescriptorRefusal => Volatile.Read(ref _lastTextLaunchDescriptorRefusal);

    /// <summary>
    /// PID of the text runtime THIS Core session launched, re-verified against the live process
    /// (creation time + image path), or null. A PID file alone proves nothing: it checks only the
    /// process name, so a recycled PID owned by any llama-server would pass. Null means "not
    /// verified", which every ownership decision treats as not owned.
    /// </summary>
    internal static int? GetVerifiedTextLaunchProcessId(IPeerProcessInspector? inspector = null)
    {
        if (!TryGetTextLaunchIdentity(out ExpectedPeerIdentity recorded)) return null;
        ExpectedPeerIdentity? live = ConnectedPeerAttestation.TryCaptureIdentity(recorded.ProcessId, inspector);
        return live is { } now
            && now.StartTimeUtcTicks == recorded.StartTimeUtcTicks
            && ConnectedPeerAttestation.SameImage(recorded.ImagePath, now.ImagePath)
            ? recorded.ProcessId
            : null;
    }

    internal static bool TryGetTextLaunchIdentity(out ExpectedPeerIdentity identity)
    {
        LaunchIdentityBox? recorded = Volatile.Read(ref _textLaunchIdentity);
        identity = recorded?.Identity ?? default;
        return recorded is not null;
    }

    private sealed record ExactTextModelProof(string Sha256, int ProcessId, long StartTimeUtcTicks);

    private static int TextPort => LocalAiPaths.TextPort;
    private const int MediaPort = 8188;
    // A sleeping llama-server keeps the process alive but unloads the model. On a 27B profile
    // that turns the next message into another minute-scale cold load while the UI misleadingly
    // looks ready. Keep the selected text model resident until an explicit media/game/OFF
    // transition releases it; those paths already stop text before claiming the GPU.
    private const int WarmTextKeepAliveSeconds = -1;
    private static string TextEndpoint => "http://127.0.0.1:" + TextPort;
    /// <summary>Loopback URL of the resident text runtime (read-only; used by the Model Arena runner).</summary>
    public string TextEndpointUrl => TextEndpoint;
    private static readonly string MediaEndpointUrl = "http://127.0.0.1:" + MediaPort;
    internal const string MediaAssistantProfileLabel = "Qwen3.5-4B Uncensored media assistant";
    // A live llama-server under heavy prompt-processing load (large context, GPU offload short of
    // ideal) can leave its HTTP listener thread starved for several seconds before it answers even
    // a trivial /health GET. Confirmed live: a real coding-agent task on a borderline-VRAM 27B
    // profile pinned every CPU core, and /health calls at the old 5s timeout intermittently timed
    // out mid-request - IsTextHealthyAsync then reported the process unhealthy, EnsureReadyAsync
    // stopped and relaunched it, and the in-flight prompt processing was thrown away and restarted
    // from scratch. 20s gives a genuinely busy-but-alive process room to answer without weakening
    // the check: a process that is truly hung or dead still fails immediately (connection refused),
    // and the outer six-minute EnsureReadyTimeout still bounds the whole operation.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public sealed record RuntimeResult(bool Success, string Message);

    /// <summary>
    /// Smallest NVIDIA VRAM, in MiB, that qualifies for the CUDA flagship profile. nvidia-smi reports
    /// MiB and a nominal 12 GB card can report slightly under 12288 MiB, so the bar is 12000.
    /// </summary>
    internal const int HeavyGpuVramThresholdMib = 12000;

    /// <summary>
    /// The same 12 GB bar for DXGI, which reports dedicated video memory in bytes: a full 12 GiB. The two
    /// are deliberately different numbers: the MiB bar admits cards that the byte bar would refuse.
    /// </summary>
    internal const long HeavyGpuDedicatedVramThresholdBytes = 12L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether the heavy tier applies. It is decided from the same inventory the launch reads (see <see cref="TextLaunchTierSelector"/>),
    /// so the status line and the launch cannot disagree about the GPU.
    /// </summary>
    public async Task<bool> HasHeavyGpuAsync(CancellationToken ct)
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(InventoryProbe(), await GetVramMibAsync(ct).ConfigureAwait(false));
        if (tier.HeavyGpu) return true;
        if (tier.VramMib > 0) LogGpuDetection("Found GPU(s) but the largest reports only " + tier.VramMib + " MiB VRAM from " + tier.VramSource + "; falling back to the small model.");
        return false;
    }

    /// <summary>
    /// Whether an NVIDIA device has the VRAM for the CUDA build, judged by nvidia-smi alone. Every decision that selects the CUDA build or a
    /// text-cuda backend key uses this. <see cref="HasHeavyGpuAsync"/> also admits a DXGI-judged AMD or Intel card for the Vulkan build,
    /// which must never be read as CUDA.
    /// </summary>
    public async Task<bool> HasCudaHeavyGpuAsync(CancellationToken ct) =>
        await GetVramMibAsync(ct).ConfigureAwait(false) >= HeavyGpuVramThresholdMib;

    /// <summary>
    /// How long a VRAM probe is reused. Installed VRAM cannot change between two requests to the
    /// same process except when a user physically attaches or removes an external GPU, so the TTL
    /// is short enough that an eGPU hot-plug is still noticed on its own without a restart, and
    /// long enough that the probe leaves the per-request path entirely.
    /// </summary>
    internal static readonly TimeSpan VramProbeTtl = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim VramProbeGate = new(1, 1);
    private static int _cachedVramMib;
    private static long _cachedVramProbedTicks;   // 0 = never probed
    internal IDisposable OverrideVramMibForTests(int mib)
    {
        if (mib < 0) throw new ArgumentOutOfRangeException(nameof(mib));
        return new VramOverrideScope(this, mib);
    }

    private sealed class VramOverrideScope : IDisposable
    {
        private readonly LocalAiProcessManager _process;
        private readonly int _previous;
        private bool _disposed;

        internal VramOverrideScope(LocalAiProcessManager process, int mib)
        {
            _process = process;
            _previous = Interlocked.Exchange(ref process._vramMibOverrideForTests, mib);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Exchange(ref _process._vramMibOverrideForTests, _previous);
        }
    }

    /// <summary>Forces the next call to probe again - for an explicit "re-detect my hardware"
    /// action, and so tests never inherit another test's cached probe.</summary>
    internal static void InvalidateVramCache() => Volatile.Write(ref _cachedVramProbedTicks, 0);

    /// <summary>
    /// Cached in front of <see cref="ProbeVramMibAsync"/> because this sits on the routed chat
    /// path: SmartRouterService resolves a role on every request, that calls
    /// RuntimeManagerService.GetStatusAsync, and this was underneath it. On a machine with no
    /// NVIDIA device all three nvidia-smi candidates fail, and each failure also appended to
    /// gpu-detect.log - measured at ~109 ms of subprocess spawning plus a disk write on every
    /// single chat request, for a value that cannot change between two requests.
    /// </summary>
    public async Task<int> GetVramMibAsync(CancellationToken ct)
    {
        int scopedVram = Volatile.Read(ref _vramMibOverrideForTests);
        if (scopedVram >= 0) return scopedVram;
        if (TryGetCachedVramMib(out int cached)) return cached;

        await VramProbeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another request may have probed while this one waited for the gate.
            if (TryGetCachedVramMib(out cached)) return cached;

            int probed = await ProbeVramMibAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _cachedVramMib, probed);
            // Written last so a concurrent reader can never see a fresh timestamp against a stale
            // value; a reader that observes the old timestamp simply probes again.
            Volatile.Write(ref _cachedVramProbedTicks, DateTime.UtcNow.Ticks);
            return probed;
        }
        finally { VramProbeGate.Release(); }
    }

    private static bool TryGetCachedVramMib(out int mib)
    {
        long probedTicks = Volatile.Read(ref _cachedVramProbedTicks);
        mib = Volatile.Read(ref _cachedVramMib);
        return probedTicks != 0
            && DateTime.UtcNow - new DateTime(probedTicks, DateTimeKind.Utc) < VramProbeTtl;
    }

    private async Task<int> ProbeVramMibAsync(CancellationToken ct)
    {
        // One bounded query. A card that answers is authoritative. A probe that hangs or fails reports 0,
        // and the caller caches that for VramProbeTtl, so the failure is logged once per TTL rather than on
        // every request. Caller cancellation propagates and is never cached as "no GPU".
        NvidiaSmiProbe.NvidiaSmiQuery query = await NvidiaSmiProbe.QueryAsync(
            NvidiaSmiProbe.RunProcessAsync, NvidiaSmiProbe.Candidates, NvidiaSmiProbe.TotalMemoryArguments,
            NvidiaSmiProbe.Timeout, ct).ConfigureAwait(false);
        if (query.Stdout != null) return NvidiaSmiProbe.ParseTotalMemoryMib(query.Stdout);

        LogGpuDetection((query.TimedOut
                ? "nvidia-smi did not answer within the probe timeout"
                : "Could not run nvidia-smi from any known location")
            + "; falling back to the small model. Tried:\n" + string.Join('\n', query.Failures));
        return 0;
    }

    private static void LogGpuDetection(string message)
    {
        try
        {
            Directory.CreateDirectory(LocalAiPaths.LogsPath);
            File.AppendAllText(Path.Combine(LocalAiPaths.LogsPath, "gpu-detect.log"),
                DateTime.UtcNow.ToString("u") + " " + message + Environment.NewLine + Environment.NewLine);
        }
        catch { }
    }

    public bool GamingActive => File.Exists(LocalAiPaths.GameMarkerFile);
    public string GetMode() => ReadTrimmedUpper(LocalAiPaths.ModeFile, "AUTO", "ON", "OFF", "AUTO");
    public void SetMode(string value) => WriteState(LocalAiPaths.ModeFile, value.ToUpperInvariant());
    public string GetTextProfile() => ReadTrimmedUpper(LocalAiPaths.TextProfileFile, "STANDARD", "STANDARD", "UNCENSORED");

    internal static string ResolveTextModelPath(string selectedProfileModel, bool hasHeavyGpu, string? requestedModelPath) =>
        string.IsNullOrWhiteSpace(requestedModelPath)
            ? (hasHeavyGpu ? selectedProfileModel : LocalAiPaths.SmallModel)
            : requestedModelPath;
    public void SetTextProfile(string value) => WriteState(LocalAiPaths.TextProfileFile, value.ToUpperInvariant());

    private static string ReadTrimmedUpper(string path, string fallback, params string[] allowed)
    {
        string value = ReadTrimmed(path).ToUpperInvariant();
        return Array.IndexOf(allowed, value) >= 0 ? value : fallback;
    }

    private static string ReadTrimmed(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;

    /// <summary>Same model regardless of casing/relative-vs-full-path spelling, and also regardless
    /// of which of two on-disk copies is named - either side blank never counts as a match (nothing
    /// resident is not "the same" as something requested).
    ///
    /// The exact-path check alone was not enough: confirmed live on a machine where the same 27B
    /// GGUF exists both under the shared runtime models folder (the plain chat/Standard path) and
    /// under PaRuX's own per-user models folder (where this machine's "coding" skill role happened
    /// to be pointed - a leftover duplicate download, not a different model). Every alternating
    /// chat-vs-coding call named a different absolute path for what is byte-for-byte the same
    /// resident file, so the exact-path guard still forced a restart on nearly every poll. Same
    /// filename plus identical size is a strong enough signal for "do not needlessly restart" -
    /// getting this wrong costs at most one avoidable reuse of a slightly different same-named file,
    /// where getting the restart storm right costs a real user a request every ~10 seconds.</summary>
    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        string fullLeft, fullRight;
        try { fullLeft = Path.GetFullPath(left); fullRight = Path.GetFullPath(right); }
        catch { fullLeft = left.Trim(); fullRight = right.Trim(); }
        if (string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase)) return true;

        if (!string.Equals(Path.GetFileName(fullLeft), Path.GetFileName(fullRight), StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var leftInfo = new FileInfo(fullLeft);
            var rightInfo = new FileInfo(fullRight);
            return leftInfo.Exists && rightInfo.Exists && leftInfo.Length == rightInfo.Length;
        }
        catch { return false; }
    }

    private static void WriteState(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    /// <summary>
    /// Whether anything is accepting connections on a local port, answered from the OS listener
    /// table instead of by trying to connect and catching the failure.
    ///
    /// Measured, not guessed: with no llama-server running - the normal state before a model is
    /// loaded, and the permanent state on a machine with no GPU - every GET /api/v1/health threw
    /// and caught roughly **nine** exceptions, because the probe below opened a TCP connection to
    /// a port nobody was listening on. Idle Core showed 0 exceptions/sec; 60 health requests
    /// showed 536, while runtime/status and models/roles showed 2 each over the same 60. The
    /// desktop app and the Android client poll health continuously for connection state, so that
    /// cost was permanent and paid on the hottest endpoint in the product.
    ///
    /// Checking the listener table rather than the PID file deliberately preserves the existing
    /// behaviour for a llama-server the user started outside PaRuX's own tracking: if something is
    /// listening, it is still probed exactly as before.
    /// </summary>
    private static bool IsPortListening(int port)
        => ManagedListenerOwnership.IsPortListening(port);

    /// <summary>
    /// Chooses the loopback port the next text launch listens on and records it for every consumer. Returns the refusal when every
    /// candidate is held by another program, or null once the choice is stored. Call it only after the previous text runtime has
    /// stopped, so a listener left by PaRuX itself is not mistaken for a foreign one. A foreign listener is skipped, never stopped.
    /// </summary>
    private static string? ChooseTextPortForLaunch()
    {
        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(LocalAiPaths.PreferredTextPort, IsHeldByAnotherProgram);
        if (!choice.Found) return TextRuntimePortSelector.ExhaustedMessage(choice.Skipped);
        LocalAiPaths.SetChosenTextPort(choice.Port);
        return null;
    }

    /// <summary>
    /// A listener on a candidate port that another program owns. A listener this process owns is PaRuX's own runtime port (the same
    /// reading the ownership checks use), so it is reused rather than skipped; the foreign listener is the only one that moves a launch.
    /// </summary>
    private static bool IsHeldByAnotherProgram(int port) =>
        IsPortListening(port) && !ManagedListenerOwnership.IsOwnedByThisProcess(port);

    /// <summary>
    /// Whether PaRuX's own text runtime is up and answering.
    ///
    /// "Own" is load-bearing and used to be missing. This asked the port for /health and adopted
    /// whatever answered. Demonstrated on the physical PC: with PaRuX's model unloaded, a
    /// llama-server started by hand on 1234 - no PID file, --gpu-layers 0, a context PaRuX never
    /// chose - was adopted and answered a user's chat, while Runtime Manager correctly reported
    /// both backends as not running. 1234 is also LM Studio's default port, so the collision is
    /// ordinary rather than exotic, and a local process that claims the port first would quietly
    /// receive every prompt the user types.
    ///
    /// The PID file is written immediately after Process.Start and before this is first polled, so
    /// requiring it costs a legitimate launch nothing. User-managed backends remain supported the
    /// deliberate way, through ExternalTextProviderService, where the user names the endpoint.
    /// </summary>
    public Task<bool> IsTextHealthyAsync(CancellationToken ct) =>
        IsTextHealthyAsync(ct, static (url, token) => Http.GetAsync(url, token));

    internal async Task<bool> IsTextHealthyAsync(
        CancellationToken ct,
        Func<string, CancellationToken, Task<HttpResponseMessage>> getHealthAsync)
    {
        if (TextHealthProbeForTests is { } probe) return await probe(ct).ConfigureAwait(false);
        if (!IsPortListening(TextPort)) return false;

        if (!IsManagedTextRuntimeOwned()) return false;

        try
        {
            using HttpResponseMessage response = await getHealthAsync(TextEndpoint + "/health", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            string? status = doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
            return status is "ok" or "no slot available";
        }
        catch { return false; }
    }

    public async Task<bool> IsMediaHealthyAsync(CancellationToken ct)
    {
        if (MediaHealthProbeForTests is { } probe) return await probe(ct).ConfigureAwait(false);
        // Same reasoning as IsTextHealthyAsync: ComfyUI is absent far more often than present, and
        // asking a port nobody is listening on costs an exception every time.
        if (!IsPortListening(MediaPort)) return false;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(MediaEndpointUrl + "/system_stats", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("system", out _);
        }
        catch { return false; }
    }

    private static (bool Owned, DateTime CheckedUtc) _textOwnership = (false, DateTime.MinValue);
    internal static void ResetTextOwnershipCacheForTests() => _textOwnership = (false, DateTime.MinValue);
    private static readonly TimeSpan OwnershipTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether the process serving PaRuX's runtime port is the one PaRuX started.
    ///
    /// Separate from <see cref="IsTextHealthyAsync"/> because the chat path cannot afford that
    /// method's HTTP round trip - per-request work added there is what cost 2.2 s before anyone
    /// measured it. This reads a PID file and, on Windows, the TCP table: no network, and cached
    /// for two seconds because ownership only changes when a process starts or stops, which PaRuX
    /// itself does.
    ///
    /// It exists because the chat path proxies straight to the endpoint without asking whether the
    /// runtime is healthy, so fixing the health check alone left the real hole open: with a
    /// hand-started llama-server on 1234, PaRuX still answered a user's chat from it while
    /// reporting providerReady=False.
    /// </summary>
    public bool IsManagedTextRuntimeOwned()
    {
        (bool owned, DateTime checkedUtc) = _textOwnership;
        if (DateTime.UtcNow - checkedUtc < OwnershipTtl) return owned;

        // A host serving the port from inside Core's own process really is PaRuX, whatever the PID
        // file says - that is how the overhead benchmarks supply an inert runtime.
        bool result = ManagedListenerOwnership.IsOwnedByThisProcess(TextPort);
        if (!result)
        {
            if (!OperatingSystem.IsWindows())
            {
                Process? managed = GetManagedProcess(LocalAiPaths.TextPidFile, "llama-server");
                result = managed != null && ManagedListenerOwnership.IsOwnedBy(TextPort, managed.Id);
            }
            else
            {
                // Only the runtime this session launched and recorded can be owned. No record (Core
                // restarted, launch identity unreadable) means "not verified": readiness is false, so
                // the normal relaunch path replaces whatever the PID file points at.
                int? verified = GetVerifiedTextLaunchProcessId();
                result = verified.HasValue && ManagedListenerOwnership.IsOwnedBy(TextPort, verified.Value);
            }
        }

        _textOwnership = (result, DateTime.UtcNow);
        return result;
    }

    public int? GetTextProcessId() => GetManagedProcess(LocalAiPaths.TextPidFile, "llama-server")?.Id;

    /// <summary>
    /// The verified launch descriptor only when that verified launch is also the process the PID file
    /// (and therefore the exact-bytes identity proof) currently names. Synchronous and in-memory apart
    /// from process identity queries; suitable for re-reading inside a CAS.
    /// </summary>
    internal TextLaunchDescriptor? GetVerifiedCurrentTextLaunchDescriptor()
    {
        LaunchIdentityBox? recorded = Volatile.Read(ref _textLaunchIdentity);
        TextLaunchDescriptor? descriptor = GetVerifiedTextLaunchDescriptor();
        if (descriptor is null || recorded?.Descriptor is null
            || !ReferenceEquals(recorded.Descriptor, descriptor)) return null;
        using Process? current = GetManagedProcess(LocalAiPaths.TextPidFile, "llama-server");
        return current is not null && current.Id == recorded.Identity.ProcessId ? descriptor : null;
    }
    public int? GetMediaProcessId() => GetManagedProcess(LocalAiPaths.MediaPidFile, "python", "pythonw")?.Id;

    internal string? GetMediaProcessIdentityKey()
    {
        Process? process = GetManagedProcess(LocalAiPaths.MediaPidFile, "python", "pythonw");
        if (process == null) return null;
        using (process)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return null;
                return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                    + process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    public long GetTextWorkingSetBytes()
    {
        try
        {
            Process? process = GetManagedProcess(LocalAiPaths.TextPidFile, "llama-server");
            if (process == null) return 0;
            process.Refresh();
            return Math.Max(0, process.WorkingSet64);
        }
        catch { return 0; }
    }
    public string GetActiveProfileLabel() => ReadTrimmed(LocalAiPaths.ActiveProfileFile);

    /// <summary>The model file the running text runtime was actually launched with, or empty when
    /// no text runtime is running. Read back from what <see cref="RecordActiveTextRuntime"/> wrote
    /// out of the real argument list, so it cannot drift from the live process the way a
    /// caller-supplied "the role should be serving X" claim can.</summary>
    public string GetActiveTextModelPath() => ReadTrimmed(LocalAiPaths.ActiveModelPathFile);

    internal string? GetActiveTextModelSha256() => ReadExactTextModelProof()?.Sha256;

    internal static bool HasExactTextModelIdentity(bool runtimeHealthy, string? requestedPath,
        string? activePath, string? activeSha256, string? expectedSha256,
        int? provenProcessId, long? provenStartTimeUtcTicks,
        int? currentProcessId, long? currentStartTimeUtcTicks)
    {
        string? expected = ModelDiscoveryIdentity.NormalizeSha256(expectedSha256);
        string? active = ModelDiscoveryIdentity.NormalizeSha256(activeSha256);
        if (!runtimeHealthy || expected is null || active is null
            || !string.Equals(active, expected, StringComparison.OrdinalIgnoreCase)
            || provenProcessId is null or <= 0 || currentProcessId is null or <= 0
            || provenStartTimeUtcTicks is null or <= 0 || currentStartTimeUtcTicks is null or <= 0
            || provenProcessId != currentProcessId || provenStartTimeUtcTicks != currentStartTimeUtcTicks
            || string.IsNullOrWhiteSpace(requestedPath) || string.IsNullOrWhiteSpace(activePath)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(requestedPath), Path.GetFullPath(activePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal void RecordExactTextModelIdentity(string modelPath, string sha256)
    {
        string? normalized = ModelDiscoveryIdentity.NormalizeSha256(sha256);
        TextProcessIdentity? processIdentity = GetCurrentTextProcessIdentity();
        if (normalized is null || !string.Equals(Path.GetFullPath(modelPath),
                Path.GetFullPath(GetActiveTextModelPath()), StringComparison.OrdinalIgnoreCase)
            || processIdentity is null)
            throw new InvalidDataException("The exact loaded model identity does not match the active model path.");
        WriteState(LocalAiPaths.ActiveModelSha256File, string.Join(Environment.NewLine,
            normalized, processIdentity.Value.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            processIdentity.Value.StartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal bool IsActiveTextModelIdentity(bool runtimeHealthy, string? requestedPath,
        string? activePath, string? expectedSha256)
    {
        ExactTextModelProof? proof = ReadExactTextModelProof();
        TextProcessIdentity? current = GetCurrentTextProcessIdentity();
        return HasExactTextModelIdentity(runtimeHealthy, requestedPath, activePath,
            proof?.Sha256, expectedSha256, proof?.ProcessId, proof?.StartTimeUtcTicks,
            current?.ProcessId, current?.StartTimeUtcTicks);
    }

    private ExactTextModelProof? ReadExactTextModelProof()
    {
        try
        {
            string[] fields = File.ReadAllLines(LocalAiPaths.ActiveModelSha256File);
            if (fields.Length != 3 || ModelDiscoveryIdentity.NormalizeSha256(fields[0]) is not { } sha
                || !int.TryParse(fields[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0
                || !long.TryParse(fields[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long startTicks) || startTicks <= 0)
                return null;
            return new ExactTextModelProof(sha, pid, startTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static TextProcessIdentity? GetCurrentTextProcessIdentity()
    {
        Process? process = GetManagedProcess(LocalAiPaths.TextPidFile, "llama-server");
        if (process is null) return null;
        using (process)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return null;
                return new TextProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    private static void ClearExactTextModelIdentity() => TryDelete(LocalAiPaths.ActiveModelSha256File);

    /// <summary>The vision projector actually attached to the running text runtime, or empty when
    /// it is serving text-only. This is the only honest answer to "can the loaded model see an
    /// image right now" - the role's configured pairing is what was WANTED, not what is loaded.</summary>
    public string GetActiveVisionProjectorPath() => ReadTrimmed(LocalAiPaths.ActiveVisionProjectorFile);

    /// <summary>Records what a text launch is really serving, taken from the argument list handed
    /// to llama-server rather than from any caller's intent. Every text launch funnels through
    /// <c>LaunchAndWaitAsync</c>, so recording here makes divergence structurally impossible: a
    /// no-heavy-GPU machine that substitutes the text-only 4B fallback for the configured 27B
    /// vision profile now records the 4B and no projector, which is what actually happened.</summary>
    internal static void RecordActiveTextRuntime(IReadOnlyList<string> args)
    {
        // Every non-Force runtime transition invalidates the exact byte proof. The scoped Force
        // path writes it back only after a fresh load, health check and post-load hash verification.
        ClearExactTextModelIdentity();
        string? model = ReadArgumentValue(args, "--model");
        string? mmproj = ReadArgumentValue(args, "--mmproj");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.ActiveModelPathFile)!);
            if (model != null) File.WriteAllText(LocalAiPaths.ActiveModelPathFile, model);
            else TryDelete(LocalAiPaths.ActiveModelPathFile);
            if (mmproj != null) File.WriteAllText(LocalAiPaths.ActiveVisionProjectorFile, mmproj);
            else TryDelete(LocalAiPaths.ActiveVisionProjectorFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void ClearActiveTextRuntime()
    {
        TryDelete(LocalAiPaths.ActiveModelPathFile);
        ClearExactTextModelIdentity();
        TryDelete(LocalAiPaths.ActiveVisionProjectorFile);
    }

    internal static string? ReadArgumentValue(IReadOnlyList<string> args, string option)
    {
        for (int index = args.Count - 2; index >= 0; index--)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(args[index + 1]))
                return args[index + 1];
        }
        return null;
    }

    /// <summary>
    /// Identity of a process PaRuX launched, stored beside its PID file. A PID alone is not an identity:
    /// Windows reuses PIDs, so a stale PID file can name an unrelated process that shares the expected
    /// name. The start time tells apart two processes that once had the same PID; the image path shows
    /// which executable it runs.
    /// </summary>
    public sealed record ManagedProcessRecord(int ProcessId, long StartTimeUtcTicks, string? ImagePath, int? TextPort = null);

    /// <summary>
    /// Records what PaRuX just launched: the PID file, and the identity that lets a later stop prove the PID
    /// still names this process. A process whose identity cannot be recorded (Windows gives no start time, or
    /// the record cannot be written) is not left running untracked: it is stopped here and the call returns
    /// false. If it will not stop, its PID file stays with a record that cannot be verified, so every later
    /// stop refuses to report it gone.
    /// </summary>
    /// <returns>True when the identity is recorded and the process is tracked.</returns>
    internal static bool RecordManagedProcess(string pidFile, Process process)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        // A record left by an earlier process must never describe this one.
        TryDelete(IdentityPathFor(pidFile));
        File.WriteAllText(pidFile, process.Id.ToString());
        long startTicks = 0;
        try { startTicks = ReadStartTimeUtc(process).Ticks; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        int? textPort = string.Equals(pidFile, LocalAiPaths.TextPidFile, StringComparison.OrdinalIgnoreCase)
            ? LocalAiPaths.TextPort
            : null;
        bool recorded = TryWriteIdentity(pidFile, new ManagedProcessRecord(process.Id, startTicks, TryGetImagePath(process), textPort));
        if (recorded && startTicks != 0) return true;

        // Without a start time, or without a record, PaRuX cannot prove later that this PID is still its runtime.
        if (TryTerminate(process, TimeSpan.FromSeconds(15))) ForgetManagedProcess(pidFile);
        return false;
    }

    private static bool TryWriteIdentity(string pidFile, ManagedProcessRecord record)
    {
        try { File.WriteAllText(IdentityPathFor(pidFile), JsonSerializer.Serialize(record)); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>What a PID file names right now. Only <see cref="Process"/> may be stopped. An unverifiable record stays on
    /// disk, so every stop refuses to report it gone rather than dropping the only handle to a runtime that may still hold VRAM.</summary>
    private readonly record struct ManagedLookup(Process? Process, bool Unverifiable);

    private enum ManagedIdentity { Verified, Foreign, Unverifiable }

    private static ManagedLookup LookUpManagedProcess(string pidFile, params string[] expectedNames)
    {
        if (!File.Exists(pidFile)) return default;
        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) { ForgetManagedProcess(pidFile); return default; }
        try
        {
            Process process = Process.GetProcessById(pid);
            if (Array.IndexOf(expectedNames, process.ProcessName) < 0)
            {
                process.Dispose();
                ForgetManagedProcess(pidFile);
                return default;
            }
            switch (ClassifyManagedIdentity(pidFile, process))
            {
                case ManagedIdentity.Verified:
                    return new ManagedLookup(process, false);
                case ManagedIdentity.Unverifiable:
                    process.Dispose();
                    return new ManagedLookup(null, true);
                default:
                    // A recycled PID, or a process PaRuX did not launch: forgotten, never returned, never stopped.
                    process.Dispose();
                    ForgetManagedProcess(pidFile);
                    return default;
            }
        }
        catch (ArgumentException) { ForgetManagedProcess(pidFile); return default; }
    }

    private static Process? GetManagedProcess(string pidFile, params string[] expectedNames) =>
        LookUpManagedProcess(pidFile, expectedNames).Process;

    /// <summary>True when the PID file names a runtime that is running, or one whose identity cannot be proven. The start
    /// paths stop it first and refuse to launch beside it when that stop fails.</summary>
    private static bool ManagedRuntimeMayBeRunning(string pidFile, params string[] expectedNames)
    {
        ManagedLookup lookup = LookUpManagedProcess(pidFile, expectedNames);
        lookup.Process?.Dispose();
        return lookup.Process != null || lookup.Unverifiable;
    }

    /// <summary>
    /// Classifies a live process against its PID file. Verified: PaRuX launched it, shown by the same PID, start time
    /// and image as its record, or, with no record, by running from PaRuX's own runtime root. Unverifiable: PaRuX
    /// recorded it, but the identity cannot be proven now (no start time was recorded, or the live start time or image
    /// cannot be read). Foreign: the PID now names something else, or a record-less process outside the runtime root,
    /// which PaRuX never stops, so a user's own llama-server or python is left alone.
    /// </summary>
    private static ManagedIdentity ClassifyManagedIdentity(string pidFile, Process process)
    {
        try
        {
            string? image = TryGetImagePath(process);
            ManagedProcessRecord? record = ReadManagedProcessRecord(pidFile);
            if (record is null)
            {
                // The launch identity this Core session recorded (PID, start time, image) is stronger proof than the
                // runtime-root fallback, so a runtime PaRuX launched from elsewhere is still stopped by its own stop.
                if (string.Equals(pidFile, LocalAiPaths.TextPidFile, StringComparison.OrdinalIgnoreCase)
                    && GetVerifiedTextLaunchProcessId() == process.Id)
                    return ManagedIdentity.Verified;
                return image != null && IsImageUnderRuntimeRoot(image) ? ManagedIdentity.Verified : ManagedIdentity.Foreign;
            }
            if (record.ProcessId != process.Id) return ManagedIdentity.Foreign;
            // Recorded without a start time, so PaRuX cannot prove this is the process it launched.
            if (record.StartTimeUtcTicks == 0) return ManagedIdentity.Unverifiable;
            long liveStartTicks;
            try { liveStartTicks = ReadStartTimeUtc(process).Ticks; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return ManagedIdentity.Unverifiable;
            }
            if (liveStartTicks != record.StartTimeUtcTicks) return ManagedIdentity.Foreign;
            if (record.ImagePath == null)
            {
                RestoreManagedTextPort(pidFile, record);
                return ManagedIdentity.Verified;
            }
            if (image == null) return ManagedIdentity.Unverifiable;
            if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(record.ImagePath), StringComparison.OrdinalIgnoreCase))
                return ManagedIdentity.Foreign;
            RestoreManagedTextPort(pidFile, record);
            return ManagedIdentity.Verified;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return ManagedIdentity.Foreign;
        }
    }

    private static void RestoreManagedTextPort(string pidFile, ManagedProcessRecord record)
    {
        if (string.Equals(pidFile, LocalAiPaths.TextPidFile, StringComparison.OrdinalIgnoreCase)
            && record.TextPort is > 0 and <= 65535)
            LocalAiPaths.SetChosenTextPort(record.TextPort.Value);
    }

    private static ManagedProcessRecord? ReadManagedProcessRecord(string pidFile)
    {
        try
        {
            string path = IdentityPathFor(pidFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<ManagedProcessRecord>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable record is treated as no record: only a process under PaRuX's runtime root then counts as PaRuX's.
            Trace.TraceWarning("PaRuX could not read a process identity record: " + ex.GetType().Name);
            return null;
        }
    }

    private static string IdentityPathFor(string pidFile) => pidFile + ".identity.json";

    /// <summary>Drops a PID file and its identity record together. Only used once the process is gone or was never PaRuX's.</summary>
    private static void ForgetManagedProcess(string pidFile)
    {
        TryDelete(pidFile);
        TryDelete(IdentityPathFor(pidFile));
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    /// <summary>
    /// Executable path of a live process. Asked of the kernel directly: Process.MainModule enumerates modules
    /// and can come back null for a process that has only just started, which would make a genuine PaRuX
    /// runtime look unverifiable.
    /// </summary>
    private static string? TryGetImagePath(Process process)
    {
        try
        {
            var buffer = new StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process.Handle, 0, buffer, ref size) && size > 0
                ? buffer.ToString(0, (int)size)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>
    /// Kills a managed process and reports whether it is genuinely gone.
    ///
    /// Both stop paths used to swallow every failure and then delete the PID file regardless. A
    /// kill that threw (access denied), or a process that outlived the wait, therefore left PaRuX
    /// with no handle to a still-running llama-server that still holds VRAM, while
    /// <see cref="GetTextProcessId"/> reported "nothing running" - a silent false "stopped" plus
    /// an untracked process. Gaming Mode is a hard-off for heavy AI, so "I could not stop it" has
    /// to be an answer the caller can receive rather than one that is discarded here.
    /// </summary>
    /// <summary>Test seam: replaces the kill step for the current call context only, so a stop path can be shown
    /// to handle a runtime that survives. Production code never sets it.</summary>
    private static readonly AsyncLocal<Func<Process, bool>?> ScopedTerminateOverride = new();

    internal static IDisposable OverrideTerminateForTests(Func<Process, bool> terminate)
    {
        Func<Process, bool>? previous = ScopedTerminateOverride.Value;
        ScopedTerminateOverride.Value = terminate;
        return new TerminateOverrideScope(previous);
    }

    private sealed class TerminateOverrideScope : IDisposable
    {
        private readonly Func<Process, bool>? _previous;
        private bool _disposed;

        internal TerminateOverrideScope(Func<Process, bool>? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedTerminateOverride.Value = _previous;
        }
    }

    /// <summary>Test seam: replaces the start-time read for the current call context only, so an unreadable start time can be
    /// shown. Production code never sets it.</summary>
    private static readonly AsyncLocal<Func<Process, DateTime>?> ScopedStartTimeOverride = new();

    internal static IDisposable OverrideStartTimeForTests(Func<Process, DateTime> startTimeUtc)
    {
        Func<Process, DateTime>? previous = ScopedStartTimeOverride.Value;
        ScopedStartTimeOverride.Value = startTimeUtc;
        return new StartTimeOverrideScope(previous);
    }

    private sealed class StartTimeOverrideScope : IDisposable
    {
        private readonly Func<Process, DateTime>? _previous;
        private bool _disposed;

        internal StartTimeOverrideScope(Func<Process, DateTime>? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedStartTimeOverride.Value = _previous;
        }
    }

    /// <summary>The start time of a live process in UTC. Throws when Windows will not report it; callers treat that as unknown.</summary>
    private static DateTime ReadStartTimeUtc(Process process) =>
        ScopedStartTimeOverride.Value is { } read ? read(process) : process.StartTime.ToUniversalTime();

    private static bool TryTerminate(Process? process, TimeSpan patience)
    {
        if (process == null) return true;
        if (ScopedTerminateOverride.Value is { } terminate) return terminate(process);
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)patience.TotalMilliseconds);
        }
        catch (InvalidOperationException) { return true; } // already exited between lookup and kill
        catch (Exception) { /* fall through to the authoritative HasExited check below */ }

        try { return process.HasExited; }
        catch { return false; }
    }

    /// <returns>False when a text runtime is still running after the stop attempt. The PID and
    /// profile records are deliberately kept in that case, so the surviving process stays
    /// trackable and a later stop can retry it, instead of being orphaned.</returns>
    public bool StopText()
    {
        // Any stop ends the runtime that the switch snapshot describes, so no later failure may relaunch it.
        _lastTextLaunch = null;
        ManagedLookup lookup = LookUpManagedProcess(LocalAiPaths.TextPidFile, "llama-server");
        // A recorded runtime whose identity cannot be proven is neither killed nor reported as stopped.
        if (lookup.Unverifiable) return false;
        if (!TryTerminate(lookup.Process, TimeSpan.FromSeconds(15))) return false;
        ForgetManagedProcess(LocalAiPaths.TextPidFile);
        ClearTextLaunchIdentity();
        TryDelete(LocalAiPaths.ActiveProfileFile);
        TryDelete(LocalAiPaths.ActiveRoleFile);
        ClearActiveTextRuntime();
        HotModelPoolCatalog.ClearActive();
        return true;
    }

    /// <returns>False when the media runtime is still running after the stop attempt.</returns>
    public bool StopMedia()
    {
        ManagedLookup lookup = LookUpManagedProcess(LocalAiPaths.MediaPidFile, "python", "pythonw");
        if (lookup.Unverifiable) return false;
        if (!TryTerminate(lookup.Process, TimeSpan.FromSeconds(20))) return false;
        ForgetManagedProcess(LocalAiPaths.MediaPidFile);
        return true;
    }

    /// <returns>False when either runtime survived. Both are always attempted - a text runtime
    /// that refuses to die must not prevent the media runtime from being stopped.</returns>
    public bool StopAll()
    {
        bool text = StopText();
        bool media = StopMedia();
        return text && media;
    }

    public async Task<RuntimeResult> StartTextAsync(CancellationToken ct, string? modelPathOverride = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (GamingActive) return new RuntimeResult(false, "A game is currently active; local AI start is blocked.");
            if (!await StopMediaAwaitingVramReleaseAsync(ct).ConfigureAwait(false)) return new RuntimeResult(false, "The media runtime did not stop, so local text was not started.");
            // One inventory decides the tier and the backend: nvidia-smi for an NVIDIA device, DXGI for every other adapter. The launch reads
            // that same decision, so the tier, the build and the status line cannot disagree about the GPU.
            GpuInventoryStatus inventory = InventoryProbe();
            TextLaunchTier tier = TextLaunchTierSelector.Select(inventory, await GetVramMibAsync(ct).ConfigureAwait(false));
            bool hasGpu = tier.HeavyGpu;
            bool heavyCuda = hasGpu && tier.Backend == TextRuntimeBackend.Cuda;
            // CPU-only means DXGI reports no usable adapter at all. A host with an integrated or non-NVIDIA
            // adapter keeps the Vulkan launch, which is the working path there. Only a host with nothing to
            // offload to skips the Vulkan attempt and launches CPU arguments directly.
            bool cpuOnly = RuntimeLaunchOptimizer.ResolveCpuOnly(hasGpu, () => inventory);
            string choice = GetTextProfile();

            string model, alias = "local-coder", runtime, profile;
            string? vision = null;
            List<string> reasoning, sampling;
            int fallbackBatch, fallbackUbatch;
            string fallbackCache;

            if (!hasGpu)
            {
                model = ResolveTextModelPath(LocalAiPaths.SmallModel, hasGpu, modelPathOverride);
                profile = modelPathOverride == null
                    ? "Qwen3.5-4B Radeon fallback"
                    : Path.GetFileNameWithoutExtension(model) + " CPU/Vulkan";
                runtime = LocalAiPaths.RuntimeVulkanPath;
                fallbackBatch = 512; fallbackUbatch = 128; fallbackCache = "q8_0";
                reasoning = ["--reasoning-effort", "low", "--reasoning-budget", "512"];
                sampling = ["--temp", "1.0", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "1.5", "--repeat-penalty", "1.0"];
            }
            else if (choice == "UNCENSORED")
            {
                model = ResolveTextModelPath(LocalAiPaths.UncensoredModel, hasGpu, modelPathOverride);
                vision = modelPathOverride == null
                    ? LocalAiPaths.UncensoredVision
                    : LocalAiPaths.GetBundledVisionProjectorForModel("uncensored", modelPathOverride);
                runtime = LocalAiPaths.ResolveGpuTextRuntimePath(heavyCuda);
                profile = modelPathOverride != null || LocalAiPaths.GetRoleOverride("uncensored") != null
                    ? Path.GetFileNameWithoutExtension(model) + " Uncensored GPU" : "Qwen3.8-27B Uncensored GPU";
                fallbackBatch = 256; fallbackUbatch = 64; fallbackCache = "q8_0";
                reasoning = ["--reasoning-effort", "medium", "--reasoning-preserve"];
                sampling = ["--temp", "0.8", "--top-p", "0.95", "--top-k", "40", "--min-p", "0.02", "--presence-penalty", "0.0", "--repeat-penalty", "1.0"];
            }
            else
            {
                model = ResolveTextModelPath(LocalAiPaths.StandardModel, hasGpu, modelPathOverride);
                vision = modelPathOverride == null
                    ? LocalAiPaths.StandardVision
                    : LocalAiPaths.GetBundledVisionProjectorForModel("standard", modelPathOverride);
                runtime = LocalAiPaths.ResolveGpuTextRuntimePath(heavyCuda);
                profile = modelPathOverride != null || LocalAiPaths.GetRoleOverride("standard") != null
                    ? Path.GetFileNameWithoutExtension(model) + " Standard GPU" : "Qwen3.8-27B Standard GPU";
                fallbackBatch = 512; fallbackUbatch = 128; fallbackCache = "q8_0";
                reasoning = ["--reasoning-effort", "medium", "--reasoning-preserve"];
                sampling = ["--temp", "1.0", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "0.0", "--repeat-penalty", "1.0"];
            }

            // The display-name profile string is not the only writer of ActiveProfileFile: a model
            // routed here through a "Role: X" launch (RuntimeManagerService.StartArbitraryTextModelAsync,
            // used by per-skill model-role assignment) records "Role: <name>" instead of this method's
            // own "<Model> Standard/Uncensored GPU" label, even when both name the exact same resident
            // model file. Comparing only the label meant a coding task whose "coding" role happens to
            // point at the same model as the default chat profile - a completely ordinary, even
            // recommended, configuration - saw every alternating call conclude "different profile,
            // restart", discarding a perfectly healthy, already-warm process and its KV cache each
            // time. Confirmed live: with a coding role mapped to the resident Standard model, the pid
            // in %LOCALAPPDATA%\LocalAI\llama-server.pid changed on nearly every ~10s poll while
            // completely idle, active-profile.txt flipping "Role: Coding" / "<Model> Standard GPU" on
            // each restart. Comparing the actual resident model file - what a restart would or would
            // not change - fixes the false mismatch without weakening the check: a real profile change
            // (different model, or GPU/CPU fallback switch) still changes the model path and still
            // restarts correctly.
            string current = ReadTrimmed(LocalAiPaths.ActiveProfileFile);
            bool sameModelAlreadyResident = PathsEqual(GetActiveTextModelPath(), model);
            if ((current == profile || sameModelAlreadyResident)
                && await IsTextHealthyAsync(ct).ConfigureAwait(false))
                return new RuntimeResult(true, "Already running: " + profile);

            // Captured before the stop below. When this start is a switch to another model and no rung of it becomes healthy,
            // the runtime that was working is relaunched from this snapshot instead of being lost.
            TextLaunchSnapshot? previous = _lastTextLaunch is { } lastLaunch && await IsTextHealthyAsync(ct).ConfigureAwait(false)
                ? lastLaunch : null;

            // A resident runtime that will not stop keeps its VRAM. Launching beside it would orphan it, so the start stops here.
            if (ManagedRuntimeMayBeRunning(LocalAiPaths.TextPidFile, "llama-server") && !StopText())
                return new RuntimeResult(false, "The previous text runtime did not stop, so the requested model was not started.");

            string exe = Path.Combine(runtime, LocalAiPaths.LlamaServerExecutable);
            if (!File.Exists(exe)) return new RuntimeResult(false, "File not found: " + exe);
            if (!File.Exists(model)) return new RuntimeResult(false, "File not found: " + model);
            string? portRefusal = ChooseTextPortForLaunch();
            if (portRefusal != null) return new RuntimeResult(false, portRefusal);

            (fallbackBatch, fallbackUbatch) = DefaultBatchSizes(heavyCuda, fallbackBatch, fallbackUbatch);
            RuntimeLaunchOptimization optimization = await RuntimeLaunchOptimizer.ResolveAsync(
                model, "chat", hasGpu, fallbackBatch, fallbackUbatch, fallbackCache, ct, inventory).ConfigureAwait(false);

            // The user's own context choice wins over the tuned profile's, and is applied to the
            // profile rather than appended afterwards so --ctx-size and --cache-type-* keep a single
            // writer. Null for a model the user has never configured, which leaves the launch exactly
            // as it was.
            ContextPreferenceStore.ContextPreference? contextPreference =
                ContextPreferenceLaunch.ApplyTo(optimization.Profile, model);

            List<string> performanceArgs;
            try { performanceArgs = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, heavyCuda, cpuOnly); }
            catch (InvalidDataException ex) { return new RuntimeResult(false, "Saved performance profile is not safe to apply: " + ex.Message); }

            var args = new List<string>
            {
                "--model", model, "--alias", alias, "--host", "127.0.0.1", "--port", TextPort.ToString(),
                "--jinja", "--reasoning", "auto",
                "--sleep-idle-seconds", hasGpu ? WarmTextKeepAliveSeconds.ToString() : "-1"
            };
            args.AddRange(WebUiArguments());
            args.AddRange(performanceArgs);
            args.AddRange(ContextPreferenceLaunch.ExtraArguments(contextPreference));
            args.AddRange(reasoning);
            args.AddRange(sampling);
            KeepLastValueFor(args, "--reasoning-effort");
            KeepLastValueFor(args, "--reasoning-budget");

            // The launch plan's known-good fallback: the same launch built from the pre-plan profile. It exists only when
            // a plan changed something, and the ladder tries it right after the planned primary fails its health check.
            List<string>? baselineArgs = null;
            if (optimization.Plan is { Applied: true } && optimization.BaselineProfile != null)
            {
                try
                {
                    List<string> baselinePerformance = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.BaselineProfile, heavyCuda, cpuOnly);
                    baselineArgs = new List<string>
                    {
                        "--model", model, "--alias", alias, "--host", "127.0.0.1", "--port", TextPort.ToString(),
                        "--jinja", "--reasoning", "auto",
                        "--sleep-idle-seconds", hasGpu ? WarmTextKeepAliveSeconds.ToString() : "-1"
                    };
                    baselineArgs.AddRange(WebUiArguments());
                    baselineArgs.AddRange(baselinePerformance);
                    baselineArgs.AddRange(ContextPreferenceLaunch.ExtraArguments(contextPreference));
                    baselineArgs.AddRange(reasoning);
                    baselineArgs.AddRange(sampling);
                    KeepLastValueFor(baselineArgs, "--reasoning-effort");
                    KeepLastValueFor(baselineArgs, "--reasoning-budget");
                }
                catch (InvalidDataException) { baselineArgs = null; }
            }
            if (vision != null)
            {
                if (!File.Exists(vision)) return new RuntimeResult(false, "Vision projector not found: " + vision);
                string mediaInput = Path.Combine(LocalAiPaths.Root, "media-input");
                Directory.CreateDirectory(mediaInput);
                // Let llama.cpp's --fit policy place the projector. Forcing the multimodal
                // projector onto the CPU made a single Computer Use screenshot sit at zero
                // decoded tokens for minutes on an otherwise idle RTX system. --fit already owns
                // the VRAM safety margin and can offload model layers if the projector needs room.
                // Computer Use sends a fresh desktop image for every action. At the previous
                // 1024-token floor a 1080x2400 screen expanded to ~2.5k prompt tokens and spent
                // over two minutes in prompt evaluation. A fixed 256-token visual budget keeps
                // the loop interactive while retaining enough detail for ordinary Windows UI.
                string[] visionArguments = ["--mmproj", vision,
                    "--image-min-tokens", "256", "--image-max-tokens", "256",
                    "--media-path", mediaInput];
                args.AddRange(visionArguments);
                baselineArgs?.AddRange(visionArguments);
            }

            // The launch ladder. Each rung is one bounded attempt built from the same tuned arguments: the primary engine first,
            // then flash attention off, reduced GPU offload, the Vulkan engine for a CUDA primary, and CPU last. A rung that
            // fails has already stopped its own process, so the next one starts clean and the trace records every outcome.
            IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(
                heavyCuda ? TextLaunchLadder.Engine.Cuda : TextLaunchLadder.Engine.Vulkan, cpuOnly, args,
                vulkanAvailable: File.Exists(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable)),
                hasPlannedLaunch: baselineArgs != null,
                primaryAvailable: !heavyCuda
                    || File.Exists(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable)));
            if (optimization.Plan is { Applied: true } appliedPlan)
                _lastLaunchPlanExplanation = appliedPlan.Explain();
            else _lastLaunchPlanExplanation = optimization.Plan?.Summary ?? string.Empty;
            var trace = new List<string>();
            RuntimeResult? firstFailure = null;
            RuntimeResult? lastFailure = null;
            for (int index = 0; index < rungs.Count; index++)
            {
                TextLaunchLadder.Rung rung = rungs[index];
                if (index > 0 && !StopText())
                    return new RuntimeResult(false, (firstFailure?.Message ?? string.Empty) + " The text runtime did not stop, so the next launch rung was not started.");

                string rungRuntime = rung.Engine == TextLaunchLadder.Engine.Cuda ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
                string rungExe = Path.Combine(rungRuntime, LocalAiPaths.LlamaServerExecutable);
                string rungProfile = profile + rung.ProfileSuffix;
                List<string> rungArguments = TextLaunchLadder.ArgumentsFor(rung, args, baselineArgs);
                RuntimeResult attempt = await LaunchAndWaitAsync(rungExe, rungArguments, rungRuntime, rungProfile, LocalAiPaths.TextPidFile,
                    optimization.Profile, () => IsTextHealthyAsync(ct), () => StopText(), TimeSpan.FromMinutes(5), ct,
                    ProbeTextOutputSanityAsync).ConfigureAwait(false);
                trace.Add(rung.Label + (attempt.Success ? ": ready" : ": failed (" + FirstLine(attempt.Message) + ")"));
                if (attempt.Success)
                {
                    _lastTextLaunchTrace = trace.ToArray();
                    try { File.WriteAllText(LocalAiPaths.ActiveRoleFile, choice == "UNCENSORED" ? "uncensored" : "standard"); } catch { }
                    _lastTextLaunch = new TextLaunchSnapshot(model, rungExe, rungRuntime, rungArguments, rungProfile, optimization.Profile);
                    return attempt;
                }
                firstFailure ??= attempt;
                lastFailure = attempt;
                // Cancellation, and a game that started during the load, both end the ladder: a further rung would start a runtime during gaming.
                if (ct.IsCancellationRequested || GamingActive) break;
            }

            _lastTextLaunchTrace = trace.ToArray();
            // A single attempt keeps its own message. Several attempts keep the first failure, which is the more useful diagnostic,
            // and add how the ladder ended so no rung's outcome is lost.
            string failure = firstFailure is null ? "The text runtime did not start."
                : trace.Count <= 1 ? firstFailure.Message
                : firstFailure.Message + " The launch ladder ended without a healthy runtime (" + string.Join(" -> ", trace)
                    + "). Last attempt: " + lastFailure?.Message;

            // A switch that fails must not leave the user with nothing. The runtime that was healthy before this start is relaunched
            // from its snapshot, unless the failure was cancellation, a game, a missing executable, or the very same model.
            if (previous is null || ct.IsCancellationRequested || GamingActive || PathsEqual(previous.ModelPath, model)
                || !File.Exists(previous.Executable))
                return new RuntimeResult(false, failure);
            if (!StopText())
                return new RuntimeResult(false, failure + " The text runtime did not stop, so the previous runtime was not restored.");
            RuntimeResult restored = await LaunchAndWaitAsync(previous.Executable, previous.Arguments.ToList(), previous.RuntimeDirectory,
                previous.ProfileLabel, LocalAiPaths.TextPidFile, previous.LaunchProfile, () => IsTextHealthyAsync(ct), () => StopText(),
                TimeSpan.FromMinutes(5), ct, ProbeTextOutputSanityAsync).ConfigureAwait(false);
            trace.Add("restore " + previous.ProfileLabel + (restored.Success ? ": ready" : ": failed (" + FirstLine(restored.Message) + ")"));
            _lastTextLaunchTrace = trace.ToArray();
            if (!restored.Success)
                return new RuntimeResult(false, failure + " Restoring the previous text runtime also failed: " + FirstLine(restored.Message) + ".");
            _lastTextLaunch = previous;
            return new RuntimeResult(false, failure + " The previous text runtime (" + previous.ProfileLabel + ") was restored.");
        }
        finally { _gate.Release(); }
    }

    public Task<RuntimeResult> StartMediaAssistantAsync(CancellationToken ct) => StartMediaAssistantAsync(ct, null);

    /// <param name="onOutgoingTextReleased">Runs once, right before the assistant's own (slow) model load begins, i.e. after
    /// the previous text resident has stopped and every precondition has passed. StartMediaAsync starts ComfyUI there so the
    /// two cold starts overlap instead of queueing. It is not called when the assistant is already resident or the start is refused.</param>
    internal async Task<RuntimeResult> StartMediaAssistantAsync(CancellationToken ct, Action? onOutgoingTextReleased)
    {
        string wanted = MediaAssistantProfileLabel;
        if (ReadTrimmed(LocalAiPaths.ActiveProfileFile) == wanted && await IsTextHealthyAsync(ct).ConfigureAwait(false))
            return new RuntimeResult(true, "Already running.");
        if (ManagedRuntimeMayBeRunning(LocalAiPaths.TextPidFile, "llama-server") && !StopText())
            return new RuntimeResult(false, "The previous text runtime did not stop, so the media assistant was not started.");

        string model = File.Exists(LocalAiPaths.SmallUncensoredModel) ? LocalAiPaths.SmallUncensoredModel : LocalAiPaths.SmallModel;
        // The assistant answers chat for the whole media burst window (about a minute) and writes the image prompts. Measured on the
        // RTX 5080 with this exact argv: 27 tok/s on the Vulkan build against 178 tok/s on CUDA for the same 4B model, with the same
        // ~3.2 GiB of VRAM and a Z-Image job still fitting beside it. So an NVIDIA machine with the CUDA runtime installed uses CUDA;
        // everything else keeps the Vulkan build.
        string assistantRuntime = LocalAiPaths.RuntimeVulkanPath;
        try
        {
            TextLaunchTier assistantTier = TextLaunchTierSelector.Select(InventoryProbe(), await GetVramMibAsync(ct).ConfigureAwait(false));
            if (assistantTier.HeavyGpu && assistantTier.Backend == TextRuntimeBackend.Cuda)
                assistantRuntime = LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* inventory unreadable: the Vulkan assistant is the known-good fallback */ }
        string exe = Path.Combine(assistantRuntime, LocalAiPaths.LlamaServerExecutable);
        if (!File.Exists(exe)) return new RuntimeResult(false, "File not found: " + exe);
        if (!File.Exists(model)) return new RuntimeResult(false, "File not found: " + model);
        string? portRefusal = ChooseTextPortForLaunch();
        if (portRefusal != null) return new RuntimeResult(false, portRefusal);

        var args = new List<string>
        {
            "--model", model, "--alias", "local-coder","--host", "127.0.0.1", "--port", TextPort.ToString(),
            "--ctx-size", "16384", "--parallel", "1", "--batch-size", "256",
            "--ubatch-size", "64", "--flash-attn", "on", "--cache-type-k", "q8_0", "--cache-type-v", "q8_0", "--jinja", "--reasoning", "auto",
            "--reasoning-effort", "low", "--reasoning-budget", "768", "--temp", "0.8", "--top-p", "0.95", "--top-k", "40", "--min-p", "0.02",
            "--gpu-layers", "all", "--sleep-idle-seconds", "-1"
        };
        if (PathsEqual(assistantRuntime, LocalAiPaths.RuntimeVulkanPath)) { args.Add("--device"); args.Add("Vulkan0"); }
        args.AddRange(WebUiArguments());
        onOutgoingTextReleased?.Invoke();
        var assistantWatch = Stopwatch.StartNew();
        RuntimeResult assistantResult = await LaunchAndWaitAsync(exe, args, assistantRuntime, wanted, LocalAiPaths.TextPidFile,
            null, () => IsTextHealthyAsync(ct), () => StopText(), TimeSpan.FromMinutes(3), ct,
            ProbeTextOutputSanityAsync).ConfigureAwait(false);
        MediaSwapTimings.Record("media-assistant-launch", assistantWatch.Elapsed, assistantResult.Success ? "ready" : "failed");
        return assistantResult;
    }

    /// <summary>What an in-flight ComfyUI launch has done to the machine, so a failed or cancelled caller can undo exactly that.</summary>
    private sealed class ComfyLaunchState
    {
        public volatile bool ProcessStarted;
    }

    private const int LaunchHealthPollMs = 500;

    public async Task<RuntimeResult> StartMediaAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (GamingActive) return new RuntimeResult(false, "A game is currently active; local AI start is blocked.");
            if (!await HasHeavyGpuAsync(ct).ConfigureAwait(false))
                return new RuntimeResult(false, "No sufficiently powerful GPU detected; this feature needs one.");

            var total = Stopwatch.StartNew();
            // The assistant's model load and ComfyUI's cold start are independent and both slow. ComfyUI begins as soon as the
            // outgoing text resident has stopped (so the RAM admission that discounted it still holds) and runs beside the
            // assistant load. Neither touches the other's VRAM: the assistant is a small Vulkan model, ComfyUI loads no weights yet.
            using var overlapCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var comfyState = new ComfyLaunchState();
            Task<RuntimeResult>? comfy = null;
            long comfyStartedAtMs = -1;
            void BeginComfy()
            {
                if (comfy != null) return;
                comfyStartedAtMs = total.ElapsedMilliseconds;
                comfy = Task.Run(() => EnsureComfyRunningAsync(comfyState, overlapCts.Token), CancellationToken.None);
            }

            RuntimeResult assistant;
            try { assistant = await StartMediaAssistantAsync(ct, BeginComfy).ConfigureAwait(false); }
            catch
            {
                await AbandonOverlappedComfyAsync(comfy, comfyState, overlapCts).ConfigureAwait(false);
                throw;
            }
            if (!assistant.Success)
            {
                await AbandonOverlappedComfyAsync(comfy, comfyState, overlapCts).ConfigureAwait(false);
                return assistant;
            }

            bool overlapped = comfy != null;
            BeginComfy();
            RuntimeResult launched;
            try { launched = await comfy!.WaitAsync(ct).ConfigureAwait(false); }
            catch
            {
                await AbandonOverlappedComfyAsync(comfy, comfyState, overlapCts).ConfigureAwait(false);
                throw;
            }
            MediaSwapTimings.Record("media-runtime-start", total.Elapsed,
                (overlapped ? "comfy overlapped with assistant, began at +" + comfyStartedAtMs + " ms; " : "comfy after assistant; ")
                + (launched.Success ? "ready" : "failed"));
            return launched;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A start that failed or was cancelled after ComfyUI had already begun must not leave that ComfyUI behind: before
    /// the overlap, nothing started ComfyUI unless the assistant had come up first.</summary>
    private async Task AbandonOverlappedComfyAsync(Task<RuntimeResult>? comfy, ComfyLaunchState state, CancellationTokenSource overlapCts)
    {
        if (comfy == null) return;
        overlapCts.Cancel();
        try { await comfy.ConfigureAwait(false); } catch { /* cancelled or failed: the stop below is the cleanup */ }
        if (state.ProcessStarted && !StopMedia())
            Trace.TraceWarning("An overlapped ComfyUI start was abandoned, but its process did not stop and stays tracked.");
    }

    /// <summary>ComfyUI half of the media start: reuse a healthy one, replace a half-dead one, otherwise launch with the measured flags.</summary>
    private async Task<RuntimeResult> EnsureComfyRunningAsync(ComfyLaunchState state, CancellationToken ct)
    {
        if (await IsMediaHealthyAsync(ct).ConfigureAwait(false)) return new RuntimeResult(true, "Already running.");
        if (ManagedRuntimeMayBeRunning(LocalAiPaths.MediaPidFile, "python", "pythonw") && !StopMedia())
            return new RuntimeResult(false, "The media runtime did not stop, so it was not restarted.");
        if (!File.Exists(LocalAiPaths.ComfyPython) || !File.Exists(LocalAiPaths.ComfyMain))
            return new RuntimeResult(false, "ComfyUI is incomplete at " + LocalAiPaths.ComfyRoot);

        var comfyWatch = Stopwatch.StartNew();
        // Tuning flags: the ones measured faster AND output-equivalent on this PC for this exact ComfyUI + PyTorch (see
        // ComfyMediaOptimizer), otherwise the defaults PaRuX has always used. A launch failure with measured flags forgets them.
        ComfyLaunchArguments.Resolution tuning = ComfyLaunchArguments.ResolveForLaunch();
        List<string> BuildArgs(IEnumerable<string> flags)
        {
            var list = new List<string>
            {
                "-s", LocalAiPaths.ComfyMain, "--windows-standalone-build", "--listen", "127.0.0.1",
                "--port", MediaPort.ToString(), "--disable-auto-launch", "--preview-method", "auto"
            };
            list.AddRange(flags);
            return list;
        }
        state.ProcessStarted = true;
        RuntimeResult launched = await LaunchAndWaitAsync(LocalAiPaths.ComfyPython, BuildArgs(tuning.Arguments), LocalAiPaths.ComfyApp, null,
            LocalAiPaths.MediaPidFile, null, () => IsMediaHealthyAsync(ct), () => StopMedia(), TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (!launched.Success && tuning.Fingerprint is not null && !ct.IsCancellationRequested)
        {
            new ComfyOptimizationStore().Invalidate(tuning.Fingerprint, launched.Message);
            launched = await LaunchAndWaitAsync(LocalAiPaths.ComfyPython, BuildArgs(ComfyLaunchArguments.Legacy), LocalAiPaths.ComfyApp, null,
                LocalAiPaths.MediaPidFile, null, () => IsMediaHealthyAsync(ct), () => StopMedia(), TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        }
        MediaSwapTimings.Record("comfyui-launch", comfyWatch.Elapsed, launched.Success ? "ready" : "failed");
        // A freshly adopted ComfyUI build is on probation: its first real launches decide whether it stays or is rolled back.
        ManagedRuntimeLaunchObserver.Report(LocalAiPaths.ComfyRoot, launched.Success, launched.Message);
        return launched;
    }

    /// <summary>
    /// Starts only ComfyUI, with no text swap, so the first image after text work does not pay ComfyUI's cold start. The caller's
    /// <paramref name="admit"/> returns a refusal reason (RAM/VRAM admission against the text model that stays resident) or null.
    /// Gaming Mode refuses. Honest results only: the returned message says warm, already warm, or why not.
    /// </summary>
    internal async Task<RuntimeResult> PrewarmMediaRuntimeAsync(Func<CancellationToken, Task<string?>> admit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admit);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (GamingActive) return new RuntimeResult(false, "A game is currently active; local AI start is blocked.");
            if (!await HasHeavyGpuAsync(ct).ConfigureAwait(false))
                return new RuntimeResult(false, "No sufficiently powerful GPU detected; this feature needs one.");
            if (await IsMediaHealthyAsync(ct).ConfigureAwait(false)) return new RuntimeResult(true, "Already warm.");
            string? refusal = await admit(ct).ConfigureAwait(false);
            if (refusal != null) return new RuntimeResult(false, refusal);
            if (GamingActive) return new RuntimeResult(false, "A game is currently active; local AI start is blocked.");

            var state = new ComfyLaunchState();
            RuntimeResult result = await EnsureComfyRunningAsync(state, ct).ConfigureAwait(false);
            if (!result.Success && state.ProcessStarted && ct.IsCancellationRequested) StopMedia();
            return result;
        }
        catch (OperationCanceledException)
        {
            // A cancelled prewarm (Gaming Mode, timeout, shutdown) must not leave the half-started ComfyUI it began.
            StopMedia();
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Stops a ComfyUI that was started only as a prewarm and was never needed. A ComfyUI that serves the media assistant profile is
    /// left alone (a media job is in its burst window and the text restore stops it). Returns whether something was stopped.
    /// </summary>
    internal async Task<bool> ReleaseUnusedPrewarmedMediaAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReadTrimmed(LocalAiPaths.ActiveProfileFile) == MediaAssistantProfileLabel) return false;
            if (!ManagedRuntimeMayBeRunning(LocalAiPaths.MediaPidFile, "python", "pythonw")) return false;
            return StopMedia();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops ComfyUI and, if it was really running, waits (bounded) for the driver to hand its VRAM back before a text model sizes
    /// its GPU offload against the card. Returns false only when ComfyUI would not stop.</summary>
    private async Task<bool> StopMediaAwaitingVramReleaseAsync(CancellationToken ct)
    {
        bool wasRunning = ManagedRuntimeMayBeRunning(LocalAiPaths.MediaPidFile, "python", "pythonw");
        if (!StopMedia()) return false;
        if (!wasRunning) return true;
        GpuMemoryReleaseWaiter.Result released = await GpuMemoryReleaseWaiter.WaitForSettledAsync(ct).ConfigureAwait(false);
        MediaSwapTimings.Record("comfyui-vram-release-wait", released.Waited,
            released.Measured
                ? (released.Settled ? "settled" : "limit reached") + ", used " + released.FirstUsedMib + " -> " + released.LastUsedMib + " MiB"
                : "VRAM not measurable");
        return true;
    }

    public async Task<RuntimeResult> RestoreTextProfileAsync(CancellationToken ct)
    {
        if (!await HasHeavyGpuAsync(ct).ConfigureAwait(false)) return new RuntimeResult(true, "No capable GPU; nothing to restore.");
        var restoreWatch = Stopwatch.StartNew();
        RuntimeResult restored = await StartTextAsync(ct).ConfigureAwait(false);
        MediaSwapTimings.Record("text-restore", restoreWatch.Elapsed, restored.Success ? "ready" : "failed");
        if (restored.Success && !GamingActive) await WarmRestoredTextAsync(ct).ConfigureAwait(false);
        return restored;
    }

    /// <summary>Raised after a media burst ended and the flagship text model is back, with the GPU lease already released.
    /// MediaPrewarmService listens and may start an idle ComfyUI for the next image.</summary>
    internal event Action? TextRestoredAfterMedia;

    internal void RaiseTextRestoredAfterMedia()
    {
        try { TextRestoredAfterMedia?.Invoke(); }
        catch (Exception ex) { Trace.TraceWarning("Post-restore media prewarm request failed: " + ex.Message); }
    }

    /// <summary>Test seam: replaces the network step of the post-restore warm-up. Production code never sets it.</summary>
    internal Func<CancellationToken, Task>? TextWarmupForTests { get; set; }

    /// <summary>
    /// One short real completion right after a restore, so the first user message does not also pay the one-time cost of the first
    /// real request (kernel/graph set-up, chat template, sampler init) on a freshly loaded model. Bounded and best-effort: it cannot
    /// fail the restore, it is skipped in Gaming Mode, and cancellation still propagates to the caller.
    /// </summary>
    private async Task WarmRestoredTextAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        string outcome = "warmed";
        try
        {
            if (TextWarmupForTests is { } warmup) await warmup(limit.Token).ConfigureAwait(false);
            else
            {
                string payload = JsonSerializer.Serialize(new
                {
                    model = "local-coder",
                    messages = new[] { new { role = "user", content = "Hi" } },
                    max_tokens = 8,
                    stream = false,
                    cache_prompt = true
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, TextEndpoint + "/v1/chat/completions")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                using HttpResponseMessage response = await OutputSanityHttp.SendAsync(request, limit.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) outcome = "HTTP " + (int)response.StatusCode;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { outcome = "timed out"; }
        catch (Exception ex) { outcome = "failed: " + ex.GetType().Name; }
        MediaSwapTimings.Record("text-restore-warmup", watch.Elapsed, outcome);
    }

    public async Task WaitForIdleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
    }

    public Task<RuntimeResult> StartArbitraryTextModelAsync(string modelPath, string label, CancellationToken ct) =>
        StartArbitraryTextModelAsync(modelPath, label, "Compare: ", ct);

    public Task<RuntimeResult> StartArbitraryTextModelAsync(string modelPath, string label, string profilePrefix, CancellationToken ct) =>
        StartArbitraryTextModelAsync(modelPath, label, profilePrefix, adapterPaths: null, ct);

    /// <summary>Explicit user Force only: never reuse a resident path or a hot-pool alias.</summary>
    internal Task<RuntimeResult> StartFreshExactTextModelAsync(string modelPath, string label, CancellationToken ct) =>
        StartArbitraryTextModelCoreAsync(modelPath, label, "Role: ", adapterPaths: null, freshExact: true, ct);

    public Task<RuntimeResult> StartArbitraryTextModelAsync(
        string modelPath, string label, string profilePrefix, IReadOnlyList<string>? adapterPaths, CancellationToken ct) =>
        StartArbitraryTextModelCoreAsync(modelPath, label, profilePrefix, adapterPaths, freshExact: false, ct);

    private async Task<RuntimeResult> StartArbitraryTextModelCoreAsync(
        string modelPath, string label, string profilePrefix, IReadOnlyList<string>? adapterPaths,
        bool freshExact, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (GamingActive) return new RuntimeResult(false, "A game is currently active; local AI start is blocked.");
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                return new RuntimeResult(false, "File not found: " + modelPath);
            if (!await StopMediaAwaitingVramReleaseAsync(ct).ConfigureAwait(false)) return new RuntimeResult(false, "The media runtime did not stop, so the requested model was not started.");

            bool hasGpu = await HasHeavyGpuAsync(ct).ConfigureAwait(false);
            string runtime = LocalAiPaths.ResolveGpuTextRuntimePath(hasGpu);
            string profile = profilePrefix + label;
            string workload = RuntimeLaunchOptimizer.InferWorkload(label, profilePrefix);

            bool hasAdapters = adapterPaths?.Any(path => !string.IsNullOrWhiteSpace(path)) == true;
            if (CanReuseHotPoolForRequest(freshExact, hasGpu, hasAdapters)
                && HotModelPoolCatalog.TryGetActiveAlias(modelPath, out string existingAlias)
                && await IsTextHealthyAsync(ct).ConfigureAwait(false))
            {
                // A pool reuse serves a different file than the one recorded at pool launch, so the
                // active-model record has to move with it or prompt-cache scoping and reuse-vs-swap
                // decisions would keep comparing against the previously routed model.
                RecordActiveTextRuntime(new[] { "--model", modelPath });
                return new RuntimeResult(true, "Hot model pool reused without process swap (" + existingAlias + ").");
            }

            // A role/label-driven request (e.g. a skill's model-role assignment) for the exact model
            // already resident via the plain single-model path (StartTextAsync) previously restarted
            // anyway: this method's own label ("Role: <name>") never matches StartTextAsync's
            // "<Model> Standard/Uncensored GPU" label, so the two calls fought over active-profile.txt
            // on every poll even though both wanted the same file. See the matching guard and comment
            // in StartTextAsync for the live-reproduced evidence.
            if (CanReuseActiveTextModelForRequest(freshExact, hasAdapters,
                    PathsEqual(GetActiveTextModelPath(), modelPath))
                && await IsTextHealthyAsync(ct).ConfigureAwait(false))
                return new RuntimeResult(true, "Already running the requested model (" + label + ").");

            if (freshExact)
            {
                if (!StopText() || await IsTextHealthyAsync(ct).ConfigureAwait(false))
                    return new RuntimeResult(false, "The resident text runtime could not be stopped for an exact fresh load.");
            }
            else if (ManagedRuntimeMayBeRunning(LocalAiPaths.TextPidFile, "llama-server") && !StopText())
                return new RuntimeResult(false, "The resident text runtime did not stop, so the requested model was not started.");
            // Chosen after the stop above, and before the hot-pool router below, which reads the same port for its arguments.
            string? portRefusal = ChooseTextPortForLaunch();
            if (portRefusal != null) return new RuntimeResult(false, portRefusal);
            string exe = Path.Combine(runtime, LocalAiPaths.LlamaServerExecutable);
            if (!File.Exists(exe)) return new RuntimeResult(false, "File not found: " + exe);

            if (CanReuseHotPoolForRequest(freshExact, hasGpu, hasAdapters))
            {
                RuntimeResult? hot = await TryStartHotModelPoolAsync(exe, runtime, modelPath, workload, ct).ConfigureAwait(false);
                if (hot != null) return hot;
                // A router capability/proxy/load failure is intentionally non-fatal: the exact
                // same requested model continues through the proven single-model loader below.
            }

            ArbitraryTextLaunchPlan plan;
            try
            {
                plan = await PlanArbitraryTextLaunchAsync(modelPath, label, profilePrefix, adapterPaths, hasGpu,
                    materializeUiConfig: true, inventoryProbe: InventoryProbe, ct: ct).ConfigureAwait(false);
            }
            catch (UnsafeLaunchProfileException ex) { return new RuntimeResult(false, "Saved performance profile is not safe to apply: " + ex.Message); }
            try
            {
                return await LaunchAndWaitAsync(exe, plan.Arguments, runtime, profile, LocalAiPaths.TextPidFile,
                    plan.Profile, () => IsTextHealthyAsync(ct), () => StopText(), TimeSpan.FromMinutes(5), ct,
                    ProbeTextOutputSanityAsync).ConfigureAwait(false);
            }
            catch when (freshExact)
            {
                // The original failure is what the caller must see. A runtime that survives this cleanup keeps its PID
                // record, so the next start refuses to launch beside it (ManagedRuntimeMayBeRunning).
                if (!StopText()) Trace.TraceWarning("A fresh exact text load failed, and its text runtime did not stop.");
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Only an unsafe saved profile maps to the "not safe to apply" result; any other
    /// planner failure keeps propagating exactly as before the planner was extracted.</summary>
    private sealed class UnsafeLaunchProfileException(string message, Exception inner)
        : Exception(message, inner);

    internal sealed record ArbitraryTextLaunchPlan(LlamaPerformanceProfileDto Profile, List<string> Arguments,
        IReadOnlyDictionary<string, string> EnvironmentOverrides, TextLaunchDescriptorResult Descriptor);

    /// <summary>A planned fresh-exact launch: executable, exact argv, explicit environment overrides and
    /// the descriptor (or refusal) derived from them. Planning starts nothing.</summary>
    internal sealed record FreshExactTextLaunchPlan(string? Executable, IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> EnvironmentOverrides, TextLaunchDescriptorResult Descriptor);

    /// <summary>
    /// The one production planner for single-model arbitrary/exact text launches. The launch path
    /// and the planning-only API both call it, so the descriptor an evaluator or admission check
    /// compares against is generated by the same code that builds the real command line.
    /// </summary>
    private static async Task<ArbitraryTextLaunchPlan> PlanArbitraryTextLaunchAsync(string modelPath, string label,
        string profilePrefix, IReadOnlyList<string>? adapterPaths, bool hasGpu, bool materializeUiConfig,
        Func<GpuInventoryStatus> inventoryProbe, CancellationToken ct)
    {
        string runtime = LocalAiPaths.ResolveGpuTextRuntimePath(hasGpu);
        string workload = RuntimeLaunchOptimizer.InferWorkload(label, profilePrefix);
        // With no usable hardware adapter at all, launch CPU arguments directly instead of first
        // failing a Vulkan device launch; any real adapter keeps the unchanged GPU/Vulkan path.
        // The probe is injectable so a test fixes the adapter inventory; production reads DXGI through InventoryProbe.
        GpuInventoryStatus inventory = inventoryProbe();
        bool cpuOnly = RuntimeLaunchOptimizer.ResolveCpuOnly(hasGpu, () => inventory);
        (int arbitraryBatch, int arbitraryUbatch) = DefaultBatchSizes(hasGpu, hasGpu ? 512 : 256, hasGpu ? 128 : 64);
        RuntimeLaunchOptimization optimization = await RuntimeLaunchOptimizer.ResolveAsync(
            modelPath, workload, hasGpu, arbitraryBatch, arbitraryUbatch, "q8_0", ct, inventory).ConfigureAwait(false);
        List<string> performanceArgs;
        try { performanceArgs = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasGpu, cpuOnly); }
        catch (InvalidDataException ex) { throw new UnsafeLaunchProfileException(ex.Message, ex); }
        List<string> args = BuildArbitraryTextArguments(modelPath, hasGpu, performanceArgs, adapterPaths,
            materializeUiConfig);
        IReadOnlyDictionary<string, string> overrides = RuntimeLaunchOptimizer.LaunchEnvironment(optimization.Profile);
        TextLaunchDescriptorResult descriptor = TextLaunchDescriptor.Create(
            TextLaunchDescriptor.BackendForRuntimeDirectory(runtime), args,
            TextLaunchDescriptor.EffectiveEnvironment(overrides));
        return new ArbitraryTextLaunchPlan(optimization.Profile, args, overrides, descriptor);
    }

    /// <summary>
    /// Default batch/ubatch for a launch before any per-PC calibration. The small iGPU values (512/128, 256/64)
    /// stay for Vulkan/CPU; a dedicated NVIDIA CUDA launch uses 2048/1024, measured faster on an RTX 5080
    /// (prefill 4B: 4564 -> 9199 tok/s, 27B: 1679 -> 2128 tok/s vs ubatch 128). LaunchPlanner still shrinks it
    /// when less than 1 GiB of accelerator memory remains, and "Optimize for this PC" can override it.
    /// </summary>
    internal static (int Batch, int Ubatch) DefaultBatchSizes(bool heavyCuda, int fallbackBatch, int fallbackUbatch) =>
        heavyCuda ? (2048, 1024) : (fallbackBatch, fallbackUbatch);

    internal static List<string> BuildArbitraryTextArguments(string modelPath, bool hasGpu,
        IReadOnlyList<string> performanceArgs, IReadOnlyList<string>? adapterPaths, bool materializeUiConfig = true)
    {
        var args = new List<string>
        {
            "--model", modelPath, "--alias", "local-compare", "--host", "127.0.0.1", "--port", TextPort.ToString(),
            "--jinja", "--reasoning", "auto",
            "--sleep-idle-seconds", hasGpu ? "600" : "-1",
            "--reasoning-effort", "low", "--reasoning-budget", "512",
            "--temp", "0.8", "--top-p", "0.95", "--top-k", "40", "--min-p", "0.02"
        };
        // Planning must not write files; the UI config path is ephemeral and excluded from identity.
        args.AddRange(materializeUiConfig ? WebUiArguments() : new[] { "--ui" });
        args.AddRange(performanceArgs);
        args.AddRange(BuildLoraArguments(adapterPaths));
        KeepLastValueFor(args, "--reasoning-effort");
        KeepLastValueFor(args, "--reasoning-budget");
        return args;
    }

    /// <summary>
    /// Planning only: the descriptor of the fresh exact launch this manager would perform right now
    /// for <paramref name="modelPath"/> under <paramref name="label"/> (the same label the exact
    /// Candidate load passes). Launches nothing and changes no runtime state. Any reason the launch
    /// cannot be described exactly is returned as a refusal, never as an approximate descriptor.
    /// </summary>
    internal async Task<TextLaunchDescriptorResult> PlanFreshExactTextLaunchAsync(string modelPath, string label,
        CancellationToken ct) =>
        (await PlanFreshExactTextLaunchDetailsAsync(modelPath, label, ct).ConfigureAwait(false)).Descriptor;

    /// <summary>Planning only, with the executable, argv and overrides an evaluator must reproduce exactly.</summary>
    internal async Task<FreshExactTextLaunchPlan> PlanFreshExactTextLaunchDetailsAsync(string modelPath, string label,
        CancellationToken ct)
    {
        static FreshExactTextLaunchPlan Refuse(string reason) => new(null, Array.Empty<string>(),
            new Dictionary<string, string>(), new TextLaunchDescriptorResult(null, reason));
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return Refuse("model_unavailable");
        bool hasGpu = await HasHeavyGpuAsync(ct).ConfigureAwait(false);
        string runtime = LocalAiPaths.ResolveGpuTextRuntimePath(hasGpu);
        string executable = Path.Combine(runtime, LocalAiPaths.LlamaServerExecutable);
        if (!File.Exists(executable)) return Refuse("runtime_unavailable");
        try
        {
            ArbitraryTextLaunchPlan plan = await PlanArbitraryTextLaunchAsync(modelPath, label, "Role: ",
                adapterPaths: null, hasGpu, materializeUiConfig: false, inventoryProbe: InventoryProbe, ct: ct).ConfigureAwait(false);
            return new FreshExactTextLaunchPlan(executable, plan.Arguments, plan.EnvironmentOverrides, plan.Descriptor);
        }
        catch (UnsafeLaunchProfileException) { return Refuse("launch_profile_unsafe"); }
    }

    internal static bool CanReuseHotPoolForRequest(bool freshExact, bool hasGpu, bool hasAdapters) =>
        !freshExact && hasGpu && !hasAdapters;

    internal static bool CanReuseActiveTextModelForRequest(bool freshExact, bool hasAdapters, bool exactPathMatches) =>
        !freshExact && !hasAdapters && exactPathMatches;

    /// <summary>llama-server's built-in web UI arguments.
    ///
    /// <c>--ui-config-file</c> is only supplied once the file is actually there, because llama.cpp
    /// treats an unopenable path as a usage error and exits instead of starting: passing it
    /// unconditionally turned a missing cosmetic config into "no model will load on this machine",
    /// which is exactly what happened when a LocalAI root was tidied up and its config folder went
    /// with it. ConfigAssets restores the file from the copy embedded in this binary, so the normal
    /// path is that it exists; when it genuinely cannot be written we still start, just without the
    /// UI defaults.</summary>
    private static IEnumerable<string> WebUiArguments()
    {
        string? uiConfig = ConfigAssets.EnsureUiConfig();
        return uiConfig == null
            ? new[] { "--ui" }
            : new[] { "--ui", "--ui-config-file", uiConfig };
    }

    internal static void KeepLastValueFor(List<string> args, string option)
    {
        int last = args.FindLastIndex(value => string.Equals(value, option, StringComparison.Ordinal));
        if (last < 0) return;

        for (int index = last - 1; index >= 0; index--)
        {
            if (!string.Equals(args[index], option, StringComparison.Ordinal)) continue;
            args.RemoveAt(index);
            if (index < args.Count) args.RemoveAt(index);
            last -= 2;
        }
    }

    private async Task<RuntimeResult?> TryStartHotModelPoolAsync(
        string exe, string runtime, string targetModelPath, string workload, CancellationToken ct)
    {
        if (!await SupportsRouterAsync(exe, runtime, ct).ConfigureAwait(false)) return null;

        int vramMib = await GetVramMibAsync(ct).ConfigureAwait(false);
        HotModelPoolCatalog.Plan plan;
        try { plan = HotModelPoolCatalog.Build(targetModelPath, (long)vramMib * 1024L * 1024L); }
        catch { return null; }
        if (!plan.IsUsefulPool) return null;

        var runtimeArguments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (HotModelPoolCatalog.Entry entry in plan.Entries)
        {
            RuntimeLaunchOptimization optimization = await RuntimeLaunchOptimizer.ResolveAsync(
                entry.ModelPath, workload, hasCuda: true, 512, 128, "q8_0", ct,
                planLaunch: false /* pool children share one GPU; a per-model plan would assume it owns all of it */).ConfigureAwait(false);
            // Per-process P2P environment cannot vary across router children. A profile whose win
            // depends on that environment stays on the single-model path rather than being changed.
            if (optimization.Profile.EnableCudaP2P) return null;
            List<string> args;
            try { args = RuntimeLaunchOptimizer.BuildRuntimeArguments(optimization.Profile, hasCuda: true); }
            catch (InvalidDataException) { return null; }
            runtimeArguments[entry.ModelPath] = args;
        }

        try { HotModelPoolCatalog.WritePreset(plan, runtimeArguments); }
        catch { return null; }

        var routerArgs = new List<string>
        {
            "--models-preset", HotModelPoolCatalog.PresetPath,
            "--models-max", plan.MaxResidentModels.ToString(),
            "--models-autoload",
            "--host", "127.0.0.1", "--port", TextPort.ToString(),
            "--jinja", "--reasoning", "auto", "--sleep-idle-seconds", WarmTextKeepAliveSeconds.ToString(),
            "--temp", "0.8", "--top-p", "0.95", "--top-k", "40", "--min-p", "0.02"
        };
        routerArgs.AddRange(WebUiArguments());
        string label = "PaRuX Hot Model Pool (" + plan.Entries.Count + ")";
        RuntimeResult launch = await LaunchAndWaitAsync(
            exe, routerArgs, runtime, label, LocalAiPaths.TextPidFile, null,
            () => IsTextHealthyAsync(ct), () => StopText(), TimeSpan.FromMinutes(5), ct,
            ProbeTextOutputSanityAsync).ConfigureAwait(false);
        if (!launch.Success)
        {
            HotModelPoolCatalog.ClearActive();
            return null;
        }

        string alias = HotModelPoolCatalog.AliasForPath(targetModelPath);
        if (!await ValidateHotModelRouteAsync(alias, ct).ConfigureAwait(false))
        {
            // Null reports the failure. A router that survives the stop keeps its PID record, so the next start refuses beside it.
            if (!StopText()) Trace.TraceWarning("The hot model pool failed validation, and its router did not stop.");
            return null;
        }

        string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda")
            ?? LocalAiPaths.DefaultRuntimeBuildTag("text-cuda");
        HotModelPoolCatalog.MarkActive(plan, runtimeTag);
        // The router is launched with --models-preset and no --model, so LaunchAndWaitAsync could
        // not infer what it is serving. It is now proven (self-test above) to be routing to this
        // exact file, text-only, so record that rather than leaving "nothing loaded" behind.
        RecordActiveTextRuntime(new[] { "--model", targetModelPath });
        return new RuntimeResult(true,
            "Started VRAM-aware hot model pool with " + plan.Entries.Count + " resident model(s); router self-test passed for " + alias + ".");
    }

    private async Task<bool> SupportsRouterAsync(string exe, string workingDirectory, CancellationToken ct)
    {
        if (HotModelRouterSupportProbeForTests is { } supportProbe)
            return await supportProbe(exe, workingDirectory, ct).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--help");
            using var process = new Process { StartInfo = psi };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            string help = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
            return help.Contains("--models-preset", StringComparison.Ordinal)
                && help.Contains("--models-max", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private async Task<bool> ValidateHotModelRouteAsync(string alias, CancellationToken ct)
    {
        if (HotModelRouteValidationProbeForTests is { } validationProbe)
            return await validationProbe(alias, ct).ConfigureAwait(false);

        try
        {
            using var client = new HttpClient(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromMinutes(3) };
            string payload = JsonSerializer.Serialize(new
            {
                model = alias,
                messages = new[] { new { role = "user", content = "Reply with OK." } },
                max_tokens = 1,
                stream = false,
                cache_prompt = false
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, TextEndpoint + "/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    internal static List<string> BuildLoraArguments(IReadOnlyList<string>? adapterPaths)
    {
        var args = new List<string>();
        if (adapterPaths == null) return args;
        foreach (string path in adapterPaths)
        {
            args.Add("--lora");
            args.Add(path);
        }
        return args;
    }

    internal async Task<RuntimeResult> LaunchAndWaitAsync(
        string exe, List<string> args, string workingDirectory, string? profileToRecord,
        string pidFile, LlamaPerformanceProfileDto? launchProfile, Func<Task<bool>> isHealthy,
        Action stopOnFailure, TimeSpan timeout, CancellationToken ct,
        Func<CancellationToken, Task<RuntimeOutputSanity.Verdict>>? outputSanity = null)
    {
        RuntimeResult result = await LaunchAndWaitCoreAsync(exe, args, workingDirectory, profileToRecord, pidFile,
            launchProfile, isHealthy, stopOnFailure, timeout, ct, outputSanity).ConfigureAwait(false);
        // Tell the managed-runtime updater how a real text launch went (probation of a freshly adopted build).
        // Cancellation and a game start are not build faults.
        if (pidFile == LocalAiPaths.TextPidFile && LaunchInterceptForTests is null && !ct.IsCancellationRequested
            && (result.Success || !result.Message.Contains("A game", StringComparison.Ordinal)))
            ManagedRuntimeLaunchObserver.Report(workingDirectory, result.Success, result.Success ? null : result.Message);
        return result;
    }

    private async Task<RuntimeResult> LaunchAndWaitCoreAsync(
        string exe, List<string> args, string workingDirectory, string? profileToRecord,
        string pidFile, LlamaPerformanceProfileDto? launchProfile, Func<Task<bool>> isHealthy,
        Action stopOnFailure, TimeSpan timeout, CancellationToken ct,
        Func<CancellationToken, Task<RuntimeOutputSanity.Verdict>>? outputSanity)
    {
        if (LaunchInterceptForTests is { } intercept) return intercept(exe, args);
        Directory.CreateDirectory(LocalAiPaths.LogsPath);
        string baseName = Path.GetFileNameWithoutExtension(exe) == "python" ? "comfyui" : "llama-server";
        string outLog = Path.Combine(LocalAiPaths.LogsPath, baseName + ".stdout.log");
        string errLog = Path.Combine(LocalAiPaths.LogsPath, baseName + ".stderr.log");
        TryDelete(outLog); TryDelete(errLog);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        if (launchProfile != null) RuntimeLaunchOptimizer.ApplyEnvironment(psi, launchProfile);
        if (baseName == "comfyui") ComfyLaunchEnvironment.Apply(psi);
        // The actual-launch fence: derived from the very argv/environment given to this process
        // (after every override), not from what a caller intended to launch.
        TextLaunchDescriptor? launchDescriptor = null;
        if (pidFile == LocalAiPaths.TextPidFile)
        {
            TextLaunchDescriptorResult described = TextLaunchDescriptor.Create(
                TextLaunchDescriptor.BackendForRuntimeDirectory(workingDirectory),
                psi.ArgumentList.ToArray(), TextLaunchDescriptor.RelevantEnvironment(psi.Environment));
            launchDescriptor = described.Descriptor;
            // Diagnostics: a refused descriptor silently disables recommendation admission, so the bounded
            // reason (never values) is kept for status/diagnostics and traced once per launch.
            Volatile.Write(ref _lastTextLaunchDescriptorRefusal, described.Reason);
            if (described.Reason is not null)
                Trace.TraceWarning("Text launch has no exact launch descriptor: " + described.Reason);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        try
        {
            process.Start();
            ChildProcessJob.Assign(process); // result is logged inside; ownership does not depend on it
            _ = DrainToFileAsync(process.StandardOutput, outLog);
            _ = DrainToFileAsync(process.StandardError, errLog);
        }
        catch (Exception ex)
        {
            if (pidFile == LocalAiPaths.TextPidFile) ClearTextLaunchIdentity();
            return new RuntimeResult(false, "Failed to start " + Path.GetFileName(exe) + ": " + ex.Message);
        }

        if (!RecordManagedProcess(pidFile, process))
        {
            if (pidFile == LocalAiPaths.TextPidFile) ClearTextLaunchIdentity();
            return new RuntimeResult(false, Path.GetFileName(exe) + (process.HasExited
                ? " started, but Windows did not report its start time, so it was stopped rather than tracked unverified."
                : " started, but Windows did not report its start time, and it did not stop. It stays recorded so no runtime starts beside it."));
        }
        // Bound to the verified PID record above, so the launch fence never describes a process PaRuX did not track.
        if (pidFile == LocalAiPaths.TextPidFile) RecordTextLaunchIdentity(process.Id, launchDescriptor);
        if (profileToRecord != null) File.WriteAllText(LocalAiPaths.ActiveProfileFile, profileToRecord);
        if (pidFile == LocalAiPaths.TextPidFile) RecordActiveTextRuntime(args);

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await isHealthy().ConfigureAwait(false))
            {
                // Health proves only that the listener is alive. A build that computes garbage still answers "ok", so the
                // launch is accepted only after one fixed question gets a plausible reply. A failed probe stops the process.
                if (!GamingActive && outputSanity is not null)
                {
                    RuntimeOutputSanity.Verdict verdict = await outputSanity(ct).ConfigureAwait(false);
                    if (!verdict.Plausible)
                    {
                        stopOnFailure();
                        return new RuntimeResult(false, Path.GetFileName(exe) + " reported healthy, but its output sanity check failed: "
                            + verdict.Reason + ". It was stopped.");
                    }
                }
                // A game can start while the model is still loading. GameStart writes its marker before it stops the
                // runtimes, and the PID record above already exists, so a launch that began before the marker is caught
                // here instead of staying resident through the game.
                if (!GamingActive) return new RuntimeResult(true, "Started: " + (profileToRecord ?? Path.GetFileName(exe)));
                if (!TryTerminate(process, TimeSpan.FromSeconds(15)))
                    return new RuntimeResult(false, "A game started while " + Path.GetFileName(exe)
                        + " was loading, and it did not stop. It is still running and remains tracked for a later stop.");
                ForgetManagedProcess(pidFile);
                if (profileToRecord != null) TryDelete(LocalAiPaths.ActiveProfileFile);
                if (pidFile == LocalAiPaths.TextPidFile) ClearActiveTextRuntime();
                return new RuntimeResult(false, "A game started while " + Path.GetFileName(exe) + " was loading, so it was stopped.");
            }
            if (process.HasExited)
            {
                string tail = TailSafe(errLog, 40);
                ForgetManagedProcess(pidFile);
                if (profileToRecord != null) TryDelete(LocalAiPaths.ActiveProfileFile);
                if (pidFile == LocalAiPaths.TextPidFile) ClearActiveTextRuntime();
                if (pidFile == LocalAiPaths.TextPidFile) ClearTextLaunchIdentity();
                // The exit code and stderr name the cause when they can, so the user gets a CPU-specific reason and build instead of a bare exit.
                RuntimeLaunchFailureDiagnosis.Diagnosis? diagnosis = RuntimeLaunchFailureDiagnosis.Diagnose(process.ExitCode, tail);
                string cause = diagnosis is null ? string.Empty : " " + diagnosis.Cause + " " + diagnosis.SuggestedBuildVariant;
                return new RuntimeResult(false, Path.GetFileName(exe) + " exited unexpectedly." + cause + "\n" + tail);
            }
            await Task.Delay(LaunchHealthPollMs, ct).ConfigureAwait(false);
        }
        stopOnFailure();
        return new RuntimeResult(false, "Timed out waiting for " + Path.GetFileName(exe) + " to become healthy.");
    }

    /// <summary>
    /// The post-health output check for the text runtime on this instance's endpoint. The test seam replaces the network step only,
    /// so the decision and the launch behaviour around it stay the production code path.
    /// </summary>
    internal Task<RuntimeOutputSanity.Verdict> ProbeTextOutputSanityAsync(CancellationToken ct) =>
        TextOutputSanityProbeForTests is { } probe
            ? probe(ct)
            : RuntimeOutputSanity.ProbeAsync(TextEndpoint, OutputSanityHttp, ct);

    /// <summary>The first line of a runtime message, so a ladder trace stays one line per rung even when stderr is attached.</summary>
    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n');
        return (newline < 0 ? message : message[..newline]).Trim();
    }

    private static async Task DrainToFileAsync(StreamReader reader, string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                await writer.WriteAsync(buffer, 0, read).ConfigureAwait(false);
        }
        catch { }
    }

    private static string TailSafe(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            string[] all = File.ReadAllLines(path);
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch { return string.Empty; }
    }

    public async Task<RuntimeResult> RunActionAsync(
        string action, CancellationToken ct, string? modelPathOverride = null)
    {
        switch (action)
        {
            case "On": SetMode("ON"); return await StartTextAsync(ct).ConfigureAwait(false);
            case "Off":
                SetMode("OFF");
                return StopAll()
                    ? new RuntimeResult(true, "Stopped.")
                    : new RuntimeResult(false, "Local AI is set to OFF, but a process did not stop and is still running.");
            case "Auto":
                // AUTO is the normal serving mode, not an OFF-like reset. Stopping the warmed
                // model here made the next user message pay the entire model-load latency again.
                // Keep a healthy process hot; StartTextAsync remains idempotent and will repair a
                // missing or mismatched profile when the tray action is used after a cold start.
                SetMode("AUTO");
                return await StartTextAsync(ct).ConfigureAwait(false);
            case "Ensure":
                if (GetMode() == "OFF") return new RuntimeResult(false, "Local AI is in OFF mode.");
                return await StartTextAsync(ct).ConfigureAwait(false);
            case "Standard":
                SetTextProfile("STANDARD"); if (GetMode() == "OFF") SetMode("AUTO");
                if (!StopText()) return new RuntimeResult(false, "The text runtime did not stop, so the Standard profile was not started.");
                return await StartTextAsync(ct, modelPathOverride).ConfigureAwait(false);
            case "Uncensored":
                if (GamingActive) return new RuntimeResult(false, "A game is currently active; local AI start is blocked.");
                SetTextProfile("UNCENSORED"); if (GetMode() == "OFF") SetMode("AUTO");
                if (!StopText()) return new RuntimeResult(false, "The text runtime did not stop, so the Uncensored profile was not started.");
                return await StartTextAsync(ct, modelPathOverride).ConfigureAwait(false);
            case "MediaStart":
                if (GetMode() == "OFF") return new RuntimeResult(false, "Local AI is in OFF mode.");
                return await StartMediaAsync(ct).ConfigureAwait(false);
            case "MediaStop":
                bool mediaStopped = StopMedia();
                bool textStopped = StopText();
                return mediaStopped && textStopped
                    ? new RuntimeResult(true, "Stopped.")
                    : new RuntimeResult(false, "A local AI process did not stop and is still running.");
            case "GameStart":
                Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!);
                File.WriteAllText(LocalAiPaths.GameMarkerFile, JsonSerializer.Serialize(new { started = DateTime.UtcNow, source = "Playnite" }));
                // The marker is written first so nothing can restart AI while this runs, but the
                // result must reflect what actually happened: Gaming Mode is a hard-off, and
                // reporting success while a llama-server survived (still holding VRAM the game
                // needs) is exactly the fake success this codebase forbids.
                return StopAll()
                    ? new RuntimeResult(true, "Game mode on.")
                    : new RuntimeResult(false,
                        "Game mode is on and no new AI work can start, but a local AI process did not stop and is still holding resources.");
            case "GameStop":
                TryDelete(LocalAiPaths.GameMarkerFile);
                return StopAll()
                    ? new RuntimeResult(true, "Game mode off.")
                    : new RuntimeResult(false, "Game mode is off, but a local AI process did not stop.");
            default: return new RuntimeResult(false, "Unsupported runtime action.");
        }
    }

    public async Task<string> GetStatusTextAsync(CancellationToken ct)
    {
        bool hasGpu = await HasHeavyGpuAsync(ct).ConfigureAwait(false);
        string gpu = hasGpu ? "capable GPU available" : "no capable GPU detected";
        string game = GamingActive ? "game active" : "no game reported";
        Process? textProcess = GetManagedProcess(LocalAiPaths.TextPidFile, "llama-server");
        string text;
        if (textProcess != null)
        {
            string profile = ReadTrimmed(LocalAiPaths.ActiveProfileFile);
            text = await IsTextHealthyAsync(ct).ConfigureAwait(false)
                ? $"text ready ({profile}, PID {textProcess.Id})" : $"text loading ({profile}, PID {textProcess.Id})";
        }
        else text = ManagedRuntimeMayBeRunning(LocalAiPaths.TextPidFile, "llama-server")
            ? "text unverified (a recorded runtime cannot be proven stopped)" : "text off";
        Process? mediaProcess = GetManagedProcess(LocalAiPaths.MediaPidFile, "python", "pythonw");
        string media = mediaProcess != null
            ? (await IsMediaHealthyAsync(ct).ConfigureAwait(false) ? $"media ready (PID {mediaProcess.Id})" : $"media loading (PID {mediaProcess.Id})")
            : ManagedRuntimeMayBeRunning(LocalAiPaths.MediaPidFile, "python", "pythonw")
                ? "media unverified (a recorded runtime cannot be proven stopped)" : "media off";
        string pool = HotModelPoolCatalog.IsActive ? "; hot model pool active" : string.Empty;
        // A multi-rung trace is shown only when the last start needed recovery, so a plain start keeps the status line unchanged.
        string ladder = _lastTextLaunchTrace.Count > 1 ? "; launch ladder: " + string.Join(" -> ", _lastTextLaunchTrace) : string.Empty;
        return $"Mode {GetMode()}; text profile {GetTextProfile()}; {gpu}; {game}; {text}; {media}{pool}{ladder}.";
    }
}
