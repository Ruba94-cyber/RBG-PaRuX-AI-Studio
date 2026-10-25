using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// A best-effort rollback journal for explicitly approved changes to NON-Git projects.
    /// Snapshots and staged sources are kept outside the user's project. A pending
    /// journal deliberately survives a crash or an ambiguous rollback; never silently
    /// discard recovery copies. A filesystem journal is not an OS-enforced transaction,
    /// and does not protect against a hostile concurrent writer replacing a directory.
    /// </summary>
    internal sealed class ShadowApplyTransaction : IDisposable
    {
        internal sealed record Mutation(string Kind, string RelativePath, string? OldPath = null);

        // Action states what PaRuX does to the path ("Write" or "Remove"). The recovery inspector checks that the
        // recorded version digest agrees with it, so a written entry cannot quietly lose its digest.
        private sealed record Snapshot(string RelativePath, string? BeforeSha256,
            string? AfterSha256, string? BackupPath, string Action);

        private sealed record PreparedMutation(Mutation Change, string? StagedFile,
            string? StagedSha256);

        private readonly string _originalRoot;
        private readonly string _sourceRoot;
        private readonly string _recoveryRoot;
        private readonly FileStream _lease;
        private readonly List<Snapshot> _snapshots = new();
        private readonly List<PreparedMutation> _prepared = new();
        // Folders this apply creates inside the project, shallowest first. They are journaled so that rollback and
        // recovery remove them again, but only while they are still empty.
        private readonly List<string> _createdDirectories = new();
        private bool _journalReady;
        private bool _committed;
        private bool _disposed;

        private ShadowApplyTransaction(string originalRoot, string sourceRoot,
            string recoveryRoot, FileStream lease)
        {
            _originalRoot = originalRoot;
            _sourceRoot = sourceRoot;
            _recoveryRoot = recoveryRoot;
            _lease = lease;
        }

        internal string RecoveryPath => _recoveryRoot;

        /// <summary>The journal root shared by apply and recovery, created on demand under PaRuX state.</summary>
        internal static string RecoveryBaseDirectory()
        {
            AppPaths.EnsureCreated();
            string baseDir = Path.Combine(AppPaths.CodingAgentStateDir, "shadow-apply-recovery");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        /// <summary>Stable identity of one project folder: the prefix of its journals and the name of its lease.</summary>
        internal static string ProjectKey(string originalRoot) =>
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(originalRoot).ToUpperInvariant()))).ToLowerInvariant();

        /// <summary>
        /// The exclusive cross-process lease of one project folder. Apply holds it through the target writes and the
        /// shadow baseline commit; recovery holds it while it inspects, restores or discards a journal.
        /// </summary>
        internal static FileStream AcquireProjectLease(string originalRoot) =>
            new(Path.Combine(RecoveryBaseDirectory(), ProjectKey(originalRoot) + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        /// <summary>
        /// The folder of an unresolved journal for this project, or null. Begin refuses new work while one exists, and
        /// the apply caller asks the same question to name that journal for explicit recovery.
        /// </summary>
        internal static string? FindUnresolvedJournal(string originalRoot)
        {
            string key = ProjectKey(originalRoot);
            return Directory.EnumerateDirectories(RecoveryBaseDirectory(), key + "-*")
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "PENDING.json")));
        }

        // findUnresolvedJournal exists so that tests can make the journal lookup fail after the lease is taken.
        internal static ShadowApplyTransaction Begin(string originalRoot,
            string sourceRoot, IReadOnlyList<Mutation> mutations,
            IReadOnlyDictionary<string, string?>? expectedOriginalGitBlobs = null,
            CancellationToken cancellationToken = default,
            Func<string, string?>? findUnresolvedJournal = null)
        {
            if (mutations.Count == 0 || mutations.Count > 10_000)
                throw new InvalidOperationException("Invalid number of shadow file changes.");
            string target = Path.GetFullPath(originalRoot);
            string source = Path.GetFullPath(sourceRoot);
            if (!Directory.Exists(target) || !Directory.Exists(source))
                throw new DirectoryNotFoundException("The approved source or destination no longer exists.");

            string baseDir = RecoveryBaseDirectory();
            string key = ProjectKey(target);
            // Held through target writes AND the shadow baseline commit. Concurrent PaRuX
            // instances sharing the state root fail closed instead of interleaving writes.
            FileStream lease = AcquireProjectLease(target);
            string recovery = Path.Combine(baseDir, key + "-" + Guid.NewGuid().ToString("N"));
            ShadowApplyTransaction transaction;
            try
            {
                // A prior crash can leave some original files modified. Do not start
                // a second adoption until the operator reconciles that journal. Any failure
                // while looking for that journal releases the lease too, so it is never leaked.
                string? pendingDirectory = findUnresolvedJournal != null
                    ? findUnresolvedJournal(target)
                    : FindUnresolvedJournal(target);
                if (pendingDirectory != null)
                {
                    ShadowApplyRecoveryInspector.RecoveryAssessment finding =
                        ShadowApplyRecoveryInspector.Inspect(pendingDirectory, target);
                    throw new InvalidOperationException(
                        "An earlier non-Git apply is unresolved ("
                        + finding.Status + "). " + finding.Message
                        + " Recovery folder: " + pendingDirectory
                        + ". No new file changes were attempted.");
                }
                transaction = new ShadowApplyTransaction(target, source, recovery, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            try
            {
                transaction.Prepare(mutations, expectedOriginalGitBlobs, cancellationToken);
                return transaction;
            }
            catch
            {
                // A failed preflight has written no target files. Keep a journal
                // if it was already durably published, otherwise clean staging.
                if (!transaction._journalReady)
                {
                    try { if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
                    catch { }
                }
                lease.Dispose();
                throw;
            }
        }

        private void Prepare(IReadOnlyList<Mutation> mutations,
            IReadOnlyDictionary<string, string?>? expectedOriginalGitBlobs,
            CancellationToken cancellationToken)
        {
            var writes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Mutation mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mutation.Kind is not ("Added" or "Modified" or "Deleted" or "Renamed")
                    || !ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, mutation.RelativePath))
                    throw new InvalidOperationException("Shadow apply contains an unsupported or unsafe path.");
                if (!affected.Add(mutation.RelativePath))
                    throw new InvalidOperationException("Two shadow changes target the same destination.");
                if (mutation.Kind == "Renamed")
                {
                    if (string.IsNullOrWhiteSpace(mutation.OldPath)
                        || !ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, mutation.OldPath)
                        || !affected.Add(mutation.OldPath))
                        throw new InvalidOperationException("Shadow rename has an unsafe or duplicate source path.");
                    writes[mutation.OldPath] = null;
                    actions[mutation.OldPath] = "Remove";
                }
                writes[mutation.RelativePath] = mutation.Kind == "Deleted" ? null : string.Empty;
                actions[mutation.RelativePath] = mutation.Kind == "Deleted" ? "Remove" : "Write";
            }

            Directory.CreateDirectory(_recoveryRoot);
            string stage = Path.Combine(_recoveryRoot, "staged");
            string backups = Path.Combine(_recoveryRoot, "backups");
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(backups);

            for (int i = 0; i < mutations.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Mutation mutation = mutations[i];
                string? stagedFile = null;
                string? digest = null;
                if (mutation.Kind != "Deleted")
                {
                    string originalSource = Path.Combine(_sourceRoot, mutation.RelativePath);
                    if (!File.Exists(originalSource)
                        || !ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, mutation.RelativePath))
                        throw new IOException("Approved shadow source vanished or became unsafe before staging.");
                    stagedFile = Path.Combine(stage, i.ToString("D6") + ".new");
                    string before = Hash(originalSource);
                    CopyAndFlush(originalSource, stagedFile);
                    digest = Hash(stagedFile);
                    if (!string.Equals(before, digest, StringComparison.Ordinal))
                        throw new IOException("Shadow source changed during recovery staging.");
                    writes[mutation.RelativePath] = digest;
                }
                _prepared.Add(new PreparedMutation(mutation, stagedFile, digest));
            }

            int index = 0;
            foreach (string relative in affected.OrderBy(x => x, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, relative))
                    throw new IOException("Project folder acquired a link or an unsafe component.");
                string path = Path.Combine(_originalRoot, relative);
                if (Directory.Exists(path))
                    throw new IOException("A changed file path is now a directory: " + relative);
                string? backup = null;
                string? before = null;
                if (File.Exists(path))
                {
                    before = Hash(path);
                    backup = Path.Combine(backups, (index++).ToString("D6") + ".bak");
                    CopyAndFlush(path, backup);
                    if (!string.Equals(before, Hash(backup), StringComparison.Ordinal))
                        throw new IOException("Target changed while preserving its original contents.");
                }
                _snapshots.Add(new Snapshot(relative, before, writes[relative], backup, actions[relative]));
            }

            // The staged preimages must match the ORIGINAL Git baseline
            // proof obtained in the caller, not merely arbitrary target bytes
            // that appeared after approval during the backup operation.
            if (expectedOriginalGitBlobs != null)
            {
                if (expectedOriginalGitBlobs.Count != affected.Count)
                    throw new IOException("Incomplete approved target baseline manifest.");
                foreach (Snapshot snapshot in _snapshots)
                {
                    if (!expectedOriginalGitBlobs.TryGetValue(
                            snapshot.RelativePath, out string? expected))
                        throw new IOException("An affected file lacks a baseline proof.");
                    if (expected == null)
                    {
                        if (snapshot.BeforeSha256 != null)
                            throw new IOException("A file appeared after the approved baseline check.");
                    }
                    else
                    {
                        if (snapshot.BackupPath == null
                            || expected.Length is not (40 or 64)
                            || !expected.All(Uri.IsHexDigit)
                            || !string.Equals(GitBlobHash(snapshot.BackupPath,
                                    expected.Length), expected,
                                StringComparison.OrdinalIgnoreCase))
                            throw new IOException(
                                "An original file changed after the approved Git baseline check.");
                    }
                }
            }
            // Recheck snapshots immediately before publishing the recovery journal;
            // a concurrent human edit must NOT silently become the approved baseline.
            foreach (Snapshot snapshot in _snapshots)
                EnsureSnapshotUnchanged(snapshot);
            foreach (Mutation mutation in mutations)
            {
                if (mutation.Kind != "Deleted") RecordMissingDirectories(mutation.RelativePath);
            }

            // The manifest is written under a temporary name and renamed only once it is complete. A crash while it
            // is being written therefore never leaves a torn PENDING.json, which would block every later apply.
            string manifestPath = Path.Combine(_recoveryRoot, "PENDING.json");
            string manifestTemp = Path.Combine(_recoveryRoot, "PENDING.json.tmp");
            using (var stream = new FileStream(manifestTemp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new
                {
                    OriginalRoot = _originalRoot,
                    CreatedUtc = DateTime.UtcNow,
                    Warning = "Pending PaRuX apply. Restore only after verifying current user changes.",
                    Files = _snapshots.Select(s => new
                    {
                        s.RelativePath, s.BeforeSha256, s.AfterSha256, s.Action,
                        Backup = s.BackupPath == null ? null : Path.GetFileName(s.BackupPath)
                    }).ToArray(),
                    CreatedDirectories = _createdDirectories.ToArray()
                });
                ShadowApplyFaults.Reach("shadow.manifest-writing", manifestTemp);
                stream.Flush(flushToDisk: true);
            }
            // Never overwrites: a PENDING.json already present here would be a second journal for the same folder.
            File.Move(manifestTemp, manifestPath);
            _journalReady = true;
        }

        internal void Apply(Action<int>? beforeMutation = null,
            CancellationToken cancellationToken = default)
        {
            if (!_journalReady || _committed || _disposed)
                throw new InvalidOperationException("Shadow apply transaction is not pending.");
            int index = 0;
            foreach (PreparedMutation item in _prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int step = index++;
                beforeMutation?.Invoke(step); // Optional deterministic failure injection for tests.
                ShadowApplyFaults.Reach("shadow.step", step.ToString(CultureInfo.InvariantCulture));
                cancellationToken.ThrowIfCancellationRequested();
                Mutation mutation = item.Change;
                if (mutation.Kind == "Renamed")
                    Remove(mutation.OldPath!);
                if (mutation.Kind == "Deleted")
                    Remove(mutation.RelativePath);
                else
                    Write(mutation.RelativePath, item.StagedFile!, item.StagedSha256!);
            }
        }

        private void Remove(string relative)
        {
            Snapshot snapshot = _snapshots.Single(s => s.RelativePath.Equals(
                relative, StringComparison.OrdinalIgnoreCase));
            EnsureSnapshotUnchanged(snapshot);
            string path = Path.Combine(_originalRoot, relative);
            // Check-then-delete has the residual window that ReplaceUnchanged describes: an OS delete has no compare-and-swap.
            if (File.Exists(path)) File.Delete(path);
        }

        private void Write(string relative, string stagedFile, string expectedSha256)
        {
            Snapshot snapshot = _snapshots.Single(s => s.RelativePath.Equals(
                relative, StringComparison.OrdinalIgnoreCase));
            EnsureSnapshotUnchanged(snapshot);
            if (!string.Equals(Hash(stagedFile), expectedSha256, StringComparison.Ordinal))
                throw new IOException("The staged approved contents were modified after snapshot.");
            string destination = Path.Combine(_originalRoot, relative);
            string directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            if (!ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, relative))
                throw new IOException("Target path acquired a junction or link before writing.");
            string temp = Path.Combine(directory, ".parux-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                CopyAndFlush(stagedFile, temp);
                if (!string.Equals(Hash(temp), expectedSha256, StringComparison.Ordinal))
                    throw new IOException("Staged file changed while preparing the atomic replacement.");
                // The destination is checked again right before the move. A writer that changed it during the staged
                // copy above is refused here, and the journal keeps the snapshot for recovery.
                ShadowApplyFaults.Reach("shadow.before-replace", destination);
                ReplaceUnchanged(temp, destination, snapshot.BeforeSha256);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private void EnsureSnapshotUnchanged(Snapshot snapshot)
        {
            if (!ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, snapshot.RelativePath))
                throw new IOException("Target path became unsafe during shadow apply.");
            string path = Path.Combine(_originalRoot, snapshot.RelativePath);
            if (Directory.Exists(path)
                || !string.Equals(HashOrNull(path), snapshot.BeforeSha256, StringComparison.Ordinal))
                throw new IOException("Target changed since shadow apply snapshot; refusing overwrite.");
        }

        internal void Complete()
        {
            foreach (Snapshot snapshot in _snapshots)
            {
                if (!ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, snapshot.RelativePath)
                    || !string.Equals(HashOrNull(
                        Path.Combine(_originalRoot, snapshot.RelativePath)),
                        snapshot.AfterSha256, StringComparison.Ordinal))
                    throw new IOException("Shadow apply postcondition failed; recovery journal retained.");
            }
            _committed = true;
            TryDeleteRecovery();
        }

        /// <summary>
        /// Restore only paths still matching PaRuX's preimage or postimage.
        /// A concurrent human edit is never overwritten by automated rollback.
        /// Retain the durable journal and backups if recovery is ambiguous.
        /// </summary>
        internal bool TryRollback()
        {
            if (!_journalReady || _committed) return true;
            bool success = true;
            foreach (Snapshot snapshot in _snapshots.AsEnumerable().Reverse())
            {
                try
                {
                    if (!ShadowApplyPathPolicy.IsSafe(_sourceRoot, _originalRoot, snapshot.RelativePath))
                        throw new IOException("Unsafe rollback path.");
                    string destination = Path.Combine(_originalRoot, snapshot.RelativePath);
                    if (Directory.Exists(destination))
                        throw new IOException("Rollback destination became a directory.");
                    string? current = HashOrNull(destination);
                    if (string.Equals(current, snapshot.BeforeSha256, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(current, snapshot.AfterSha256, StringComparison.Ordinal))
                        throw new IOException("A concurrent edit prevents safe rollback.");
                    // Check-then-delete, like ReplaceUnchanged: an OS delete has no compare-and-swap, so a write in the
                    // instants after the check above is not observable here.
                    if (snapshot.BackupPath == null)
                        File.Delete(destination);
                    else
                    {
                        if (!string.Equals(Hash(snapshot.BackupPath), snapshot.BeforeSha256,
                            StringComparison.Ordinal))
                            throw new IOException("Recovery backup was modified.");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        string tmp = destination + ".parux-restore-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            CopyAndFlush(snapshot.BackupPath, tmp);
                            ShadowApplyFaults.Reach("shadow.before-replace", destination);
                            ReplaceUnchanged(tmp, destination, snapshot.AfterSha256);
                        }
                        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                    }
                }
                catch { success = false; }
            }
            if (success)
            {
                // Folders this apply created are removed only while they are empty, so a user file that landed in one
                // of them after the apply stays where it is.
                RemoveEmptyDirectories(_originalRoot, _createdDirectories);
                _committed = true; // Resolved by rollback: Dispose must not repeat it.
                TryDeleteRecovery();
            }
            return success;
        }

        private void TryDeleteRecovery()
        {
            try { Directory.Delete(_recoveryRoot, recursive: true); }
            catch { /* Keep forensic evidence instead of masking a successful apply. */ }
        }

        internal static void CopyAndFlush(string source, string destination)
        {
            // Do not acknowledge a staged recovery preimage until its bytes
            // have been flushed. Parent-directory metadata durability after
            // sudden power loss is platform-dependent and still unverified.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        /// <summary>
        /// Moves <paramref name="temporary"/> over <paramref name="destination"/> only while the destination still holds
        /// <paramref name="expectedSha256"/> (null means absent). The check runs immediately before the move, so a write
        /// that lands during the staged copy is refused and the journal is kept. An OS path replace has no
        /// compare-and-swap: a writer that acts in the instants between this check and the move is not observable to
        /// PaRuX. That residual window is narrowed by this check but cannot be closed by hashing.
        /// </summary>
        internal static void ReplaceUnchanged(string temporary, string destination, string? expectedSha256)
        {
            if (Directory.Exists(destination)
                || !string.Equals(HashOrNull(destination), expectedSha256, StringComparison.Ordinal))
                throw new IOException("Target changed while the approved replacement was prepared; refusing to overwrite it: "
                    + Path.GetFileName(destination));
            File.Move(temporary, destination, overwrite: true);
        }

        /// <summary>
        /// Removes journaled folders deepest first, and only while each one is empty and is not a link. A folder that
        /// cannot be removed is left in place rather than masking the outcome of the restore.
        /// </summary>
        internal static void RemoveEmptyDirectories(string originalRoot, IEnumerable<string> relativeDirectories)
        {
            foreach (string relative in relativeDirectories.OrderByDescending(item => item.Length))
            {
                string path = Path.Combine(originalRoot, relative);
                try
                {
                    if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()
                        || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    Directory.Delete(path, recursive: false);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Records each folder on the path of a written file that does not yet exist, shallowest first.</summary>
        private void RecordMissingDirectories(string relative)
        {
            string[] segments = relative.Replace('\\', '/').Split('/');
            string current = string.Empty;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = current.Length == 0 ? segments[i] : current + "/" + segments[i];
                if (Directory.Exists(Path.Combine(_originalRoot, current))) continue;
                if (File.Exists(Path.Combine(_originalRoot, current))) return; // A file blocks the path; Write refuses it.
                if (_createdDirectories.Any(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _createdDirectories.Add(current);
            }
        }

        /// <summary>
        /// Test seam: releases only the OS lease, as the operating system does when the process dies. No rollback runs,
        /// so the journal and every written file stay exactly as an interrupted apply left them.
        /// </summary>
        internal void SimulateProcessDeathForTests()
        {
            _disposed = true;
            _lease.Dispose();
        }

        private static string GitBlobHash(string path, int hexLength)
        {
            // Git stores SHA-1 or SHA-256 over "blob <length>\\0" + raw bytes.
            // This is an identity check against the already trusted Git object,
            // not a claim that SHA-1 provides authentication of adversarial data.
            using FileStream stream = File.OpenRead(path);
            using IncrementalHash hasher = IncrementalHash.CreateHash(
                hexLength == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            hasher.AppendData(Encoding.ASCII.GetBytes("blob " + stream.Length + "\0"));
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                hasher.AppendData(buffer, 0, read);
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        internal static string? HashOrNull(string path) =>
            File.Exists(path) ? Hash(path) : null;

        internal static string Hash(string path)
        {
            using FileStream input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (!_committed) TryRollback(); }
            finally { _lease.Dispose(); }
        }
    }

    /// <summary>
    /// Deterministic fault injection for the non-Git apply, rollback and recovery paths. A hook applies only to the
    /// async flow that installed it, so parallel tests cannot observe each other's faults, and production code never
    /// installs one. Named points: "shadow.step" (detail: step index), "shadow.before-replace" (detail: destination
    /// path), "shadow.manifest-writing" (detail: temporary manifest path).
    /// </summary>
    internal static class ShadowApplyFaults
    {
        private static readonly AsyncLocal<Action<string, string?>?> Hook = new();

        /// <summary>Installs a hook for the current async flow. Disposing the result restores the previous hook.</summary>
        internal static IDisposable Install(Action<string, string?> hook)
        {
            Action<string, string?>? previous = Hook.Value;
            Hook.Value = hook;
            return new Restorer(previous);
        }

        /// <summary>Called by the real transaction code at each named point.</summary>
        internal static void Reach(string point, string? detail = null) => Hook.Value?.Invoke(point, detail);

        private sealed class Restorer : IDisposable
        {
            private readonly Action<string, string?>? _previous;
            private bool _disposed;

            internal Restorer(Action<string, string?>? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Hook.Value = _previous;
            }
        }
    }
}
