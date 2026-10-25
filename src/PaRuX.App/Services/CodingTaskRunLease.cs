using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// The task may run in only one PaRuX process at a time. The process-local
    /// dictionary makes duplicate detection cheap; an exclusive OS file handle
    /// additionally rejects an executor from a second process using the same
    /// PaRuX state directory. Windows/SMB locks can cover machines sharing
    /// that directory, but DIFFERENT independent state directories still need
    /// a Core-owned multi-machine workspace lease.
    ///
    /// Lock files are intentionally never deleted: unlinking a lock after its
    /// handle closes could let another process create a second inode while an
    /// earlier owner still holds the original one. OS handles are released on
    /// process crash, then the durable dispatch journal handles uncertain work.
    /// </summary>
    public sealed class CodingTaskRunLease
    {
        private readonly ConcurrentDictionary<string, byte> _active =
            new(StringComparer.Ordinal);
        private readonly string _directory;

        public CodingTaskRunLease(string? directory = null)
        {
            _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(AppPaths.CodingAgentStateDir, "execution-leases")
                : directory);
        }

        /// <summary>
        /// Process-local preflight only. Production callers must use Enter to
        /// obtain the cross-process handle and dispose its scope on task exit.
        /// </summary>
        public bool TryEnter(string? taskId) =>
            !string.IsNullOrWhiteSpace(taskId)
            && _active.TryAdd(taskId, 0);

        public IDisposable Enter(string taskId)
        {
            if (!TryEnter(taskId))
                throw new InvalidOperationException(
                    "This coding task already has a live executor. Its checkpoint and browser cannot be shared concurrently.");

            FileStream? handle = null;
            try
            {
                Directory.CreateDirectory(_directory);
                string digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(taskId))).ToLowerInvariant();
                string path = Path.Combine(_directory, digest + ".lock");
                if (File.Exists(path)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Coding execution lock path is a symlink.");

                // FileShare.None is enforced by Windows on all ordinary opens
                // and released by the OS when a process dies. No deletion race.
                handle = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new Scope(this, taskId, handle);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                handle?.Dispose();
                Exit(taskId);
                throw new InvalidOperationException(
                    "Another process owns this coding task, or the exclusive execution lease cannot be established. Nothing was started.", error);
            }
            catch
            {
                handle?.Dispose();
                Exit(taskId);
                throw;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly CodingTaskRunLease _owner;
            private readonly string _taskId;
            private readonly FileStream _handle;
            private int _disposed;

            public Scope(CodingTaskRunLease owner, string taskId, FileStream handle)
            {
                _owner = owner;
                _taskId = taskId;
                _handle = handle;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _handle.Dispose(); }
                finally { _owner.Exit(_taskId); }
            }
        }

        public bool IsActive(string? taskId) =>
            !string.IsNullOrWhiteSpace(taskId)
            && _active.ContainsKey(taskId);

        private void Exit(string? taskId)
        {
            if (!string.IsNullOrWhiteSpace(taskId))
                _active.TryRemove(taskId, out _);
        }
    }
}
