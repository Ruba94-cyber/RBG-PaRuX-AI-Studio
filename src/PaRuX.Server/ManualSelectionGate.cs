using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The single linearization point for every writer of a user's manual model selection: role overrides
/// (<see cref="LocalAiPaths.SetRoleOverride"/>, also used by the auto-assigner and load-with-persist),
/// the chat Manual routing preference and scoped Force/Prefer overrides. Writers acquire <see cref="Sync"/>
/// and the process-shared exclusive file lease before their own store gate, and hold both for the complete
/// read-modify-write. The persistent lock file is intentionally never deleted: the operating system releases
/// its exclusive handle when a process exits, so a stale file is immediately reusable.
/// A recommendation/promotion transition evaluates <see cref="GetUserPinReason"/> under <see cref="Sync"/>
/// inside its final CAS. A user pin is therefore either visible to that check or ordered after the transition -
/// it is never lost, and the transition never writes a pin or a role assignment itself.
/// Only USER-origin role overrides pin; auto-assigned ones do not (see <see cref="RoleOverrideOrigin"/>).
/// Lock order: policy gate -> candidate metadata gate -> evidence-ledger gate -> recommendation store lock
/// -> <see cref="Sync"/> -> process-shared manual-selection lock -> individual preference store gates.
/// </summary>
internal static class ManualSelectionGate
{
    private const string LockFileName = ".manual-selection.lock";
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan WriteLockWait = TimeSpan.FromSeconds(2);
    internal static readonly object Sync = new();

    internal static string LockFilePath(string? root = null)
    {
        string dataRoot = string.IsNullOrWhiteSpace(root) ? PaRuXDataRoot.Path : root;
        return Path.Combine(Path.GetFullPath(dataRoot), LockFileName);
    }

    /// <summary>
    /// Try the same-process monitor and a persistent OS-exclusive file lease in that order. A null result is a
    /// bounded busy refusal. The file itself is a stable lock identity; FileShare.None is the ownership check,
    /// so a file left by a dead process does not retain the lock.
    /// </summary>
    internal static WriteLease? TryAcquireWrite(string? root, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        string lockPath = LockFilePath(root);
        Stopwatch wait = Stopwatch.StartNew();
        bool syncEntered = false;
        while (!syncEntered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            syncEntered = Monitor.TryEnter(Sync);
            if (syncEntered) break;

            int remaining = RemainingMilliseconds(wait, timeout);
            if (remaining == 0) return null;
            if (cancellationToken.WaitHandle.WaitOne(Math.Min(remaining, (int)LockPollInterval.TotalMilliseconds)))
                cancellationToken.ThrowIfCancellationRequested();
        }

        FileStream? stream = null;
        bool ownershipTransferred = false;
        try
        {
            string directory = Path.GetDirectoryName(lockPath)
                ?? throw new InvalidDataException("Manual-selection lock path has no parent directory.");
            Directory.CreateDirectory(directory);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(lockPath))
                    throw new InvalidDataException("Manual-selection lock path cannot be a reparse point.");

                try
                {
                    stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    int remaining = RemainingMilliseconds(wait, timeout);
                    if (remaining == 0) return null;
                    if (cancellationToken.WaitHandle.WaitOne(Math.Min(remaining, (int)LockPollInterval.TotalMilliseconds)))
                        cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                if (IsReparsePoint(lockPath))
                    throw new InvalidDataException("Manual-selection lock path cannot be a reparse point.");
                cancellationToken.ThrowIfCancellationRequested();
                ownershipTransferred = true;
                return new WriteLease(stream);
            }
        }
        finally
        {
            if (!ownershipTransferred)
            {
                stream?.Dispose();
                if (syncEntered) Monitor.Exit(Sync);
            }
        }
    }

    internal static WriteLease AcquireWrite(string? root = null, CancellationToken cancellationToken = default) =>
        TryAcquireWrite(root, WriteLockWait, cancellationToken)
            ?? throw new ManualSelectionGateBusyException();

    private static int RemainingMilliseconds(Stopwatch wait, TimeSpan timeout)
    {
        double remaining = (timeout - wait.Elapsed).TotalMilliseconds;
        return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining));
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal sealed class WriteLease : IDisposable
    {
        private FileStream? _stream;
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        internal WriteLease(FileStream stream) => _stream = stream;

        public void Dispose()
        {
            if (_stream is null) return;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Manual-selection write leases must be released on the acquiring thread.");

            FileStream stream = _stream;
            _stream = null;
            try { stream.Dispose(); }
            finally { Monitor.Exit(Sync); }
        }
    }

    internal static bool HasUserPin(string routeRole, string autoProfile, string? root = null) =>
        GetUserPinReason(routeRole, autoProfile, root) is not null;

    /// <summary>
    /// Null when the user holds no manual selection that an automatic recommendation for the scope would
    /// override; otherwise a <see cref="CandidateReasonText"/> code: manual_pin_role_override,
    /// manual_pin_chat_manual, manual_pin_scoped or manual_pin_unreadable. Unknown or unreadable state is a pin
    /// (fail closed). The role override is read from disk, so another process's write is seen.
    /// </summary>
    internal static string? GetUserPinReason(string routeRole, string autoProfile, string? root = null)
    {
        lock (Sync)
        {
            try
            {
                string role = LocalAiPaths.NormalizeTextRole(routeRole) ?? throw new InvalidDataException("role");
                if (LocalAiPaths.UserRoleOverridePinReason(role) is { } roleReason) return roleReason;
                if (ChatRoutingPreferenceStore.GetStrict(root).Mode == RouteMode.Manual) return "manual_pin_chat_manual";
                ScopedRoutingOverrideDto? scoped = CapabilityRoutingPreferenceStore.Get(role, autoProfile, root);
                return scoped is not null && scoped.Mode != ScopedRouteMode.Automatic ? "manual_pin_scoped" : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or JsonException or NotSupportedException)
            {
                return "manual_pin_unreadable";
            }
        }
    }
}

internal sealed class ManualSelectionGateBusyException : Exception
{
    internal const string ErrorCode = "manual_selection_busy";
    internal string Code => ErrorCode;

    internal ManualSelectionGateBusyException()
        : base("A manual-selection write is already in progress.") { }
}
