using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

// SQLite connection pooling is per-process, not per-test, and the suite drives the real stores
// through their production paths. Two consequences collide with parallel collections, and both
// were observed as real gate failures on the physical PC within four consecutive runs:
//
//   * SqliteConnection.ClearAllPools() - called by roughly seven production stores (AdapterStore,
//     AutoLabStore, BackupService, ...) and thirteen test classes to release native file handles -
//     disposes the pooled handles of EVERY database in the process. Fired while another collection
//     was mid-query, it produced
//     "PluginBundleServiceTests ... ObjectDisposedException: 'SQLitePCL.sqlite3'" inside
//     sqlite3_prepare_v2, on a class that passes 5/5 in isolation.
//   * The mirror image: a pooled handle still open when a backup/restore needs exclusive file
//     access produced "BackupServiceTests ... IOException: the process cannot access the file
//     'artifacts.db' because it is being used by another process".
//
// Neither is a product defect being papered over - they are races created by running these suites
// concurrently - but a release gate that fails half its runs cannot be the release authority it is
// supposed to be. Serializing collections removes both races at their shared root. Measured cost on
// this machine: 6m18s serial against 4m43s-6m05s parallel, i.e. at most about a minute and a half,
// which is cheap for a deterministic gate. The narrower alternative - converting each
// ClearAllPools() to ClearPool(connection) so one store stops clearing every other store's pool -
// is worth doing on its own merits (it is also a real production concern: backing up one store
// currently perturbs unrelated ones) but it touches ~20 files and would not, by itself, remove the
// exclusive-file-access race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PaRuX.Server.Tests;

/// <summary>
/// Points the whole test run at a throwaway data root before any test runs.
///
/// Several suites deliberately exercise the real stores through their production paths -
/// <c>ServerState.StateDirectory</c>, the persona and MCP-client files, the backup service. Those
/// paths resolve through <see cref="PaRuXDataRoot"/>, so without this the suite creates and mutates
/// files inside the developer's own installation: running the tests once was enough to populate
/// %LOCALAPPDATA%\PaRuX with stub databases, which is both pollution and, before the resolver
/// learned to fold the two roots together, a way to strand a real installation still sitting under
/// the pre-rename name.
///
/// A module initializer rather than a fixture, because it has to win the race against the first
/// static that reads a path - <see cref="PaRuXDataRoot.Path"/> caches its answer for the process.
/// </summary>
internal static class TestDataRootIsolation
{
    // Keeps every suite on an isolated LocalAI installation for the lifetime of this serialized
    // testhost. A missing test binary/model makes accidental runtime-start paths fail closed before
    // they can launch a developer's installed llama-server or edit its active-model state.
    private static IDisposable? _localAiRootScope;

    [ModuleInitializer]
    internal static void Redirect()
    {
        string parent = Path.Combine(Path.GetTempPath(), "parux-tests");
        string directory = Path.Combine(parent, "root-" + Environment.ProcessId.ToString());

        // Each run gets its own root, so without this every run would leave one behind - the same
        // uncontrolled temp growth the suite checks the product itself for. A root still held by a
        // concurrent run simply fails to delete and is left alone.
        try
        {
            foreach (string stale in Directory.Exists(parent)
                ? Directory.GetDirectories(parent, "root-*")
                : Array.Empty<string>())
            {
                if (string.Equals(stale, directory, StringComparison.OrdinalIgnoreCase)) continue;
                // A concurrent test host (another worktree or agent) keeps writing into its own root. Deleting
                // that root mid-run can remove files the run still needs, so only roots whose owning process
                // has exited are abandoned.
                if (IsOwnedByLiveProcess(stale)) continue;
                try { Directory.Delete(stale, recursive: true); } catch { }
            }
        }
        catch { }

        Directory.CreateDirectory(directory);
        PaRuXDataRoot.UseForTests(directory);
        Environment.SetEnvironmentVariable("PARUX_LOCALAI_STATE_DIR", Path.Combine(directory, "localai-state"));
        // Collection-level parallelism is disabled above because production stores share SQLite
        // pools. With the suite serialized, one process-lifetime root avoids both races and the
        // much more serious side effect of a validation endpoint loading a developer's real model.
        // Individual tests may still create nested root scopes; RootScope restores this baseline.
        _localAiRootScope = LocalAiPaths.UseRootForTests(Path.Combine(directory, "localai-root"));

        SweepAbandonedTestDirectories();
    }

    /// <summary>True when a <c>root-&lt;pid&gt;</c> directory belongs to a process that is still running.
    /// Names that do not parse, and any failure to inspect the owner, keep the directory.</summary>
    private static bool IsOwnedByLiveProcess(string rootDirectory)
    {
        string name = Path.GetFileName(rootDirectory);
        if (!name.StartsWith("root-", StringComparison.Ordinal)
            || !int.TryParse(name["root-".Length..], out int pid))
            return true;
        try
        {
            using Process owner = Process.GetProcessById(pid);
            return !owner.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process: the root was abandoned by a run that ended
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Prefixes the PRODUCT stages work under, taken from every %TEMP% path in src/.
    /// Anything matching one of these could belong to a real backup, avatar render, speech job or
    /// Computer Use capture that is still running, so it gets the long grace period. Everything else
    /// under the parux- prefix can only have come from a test process.</summary>
    private static readonly string[] ProductTempPrefixes =
    {
        "parux-artifact", "parux-avatar-", "parux-backup-", "parux-blackwell-native",
        "parux-computeruse-", "parux-embedding", "parux-ghidra-", "parux-main",
        "parux-managed-llama", "parux-memory", "parux-parent-snapshot-",
        "parux-performance-probe", "parux-restore-", "parux-stt-", "parux-training-",
        "parux-tts-", "parux-uiverify-", "parux-uncensored"
    };

    /// <summary>Grace period for a directory a running PaRuX might still own. Long enough that a
    /// slow backup or a long avatar render is never touched.</summary>
    private static readonly TimeSpan ProductGrace = TimeSpan.FromHours(24);

    /// <summary>Grace period for a directory only a test process creates. Short, because the leak
    /// rate is what matters: roughly 250 survive each full run, so a 24-hour window on these would
    /// hold thousands at steady state rather than clearing them between runs.</summary>
    private static readonly TimeSpan TestGrace = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Removes <c>parux-*</c> directories left in %TEMP% by earlier runs.
    ///
    /// Nearly every suite here creates a GUID-named temp directory and deletes it in Dispose, and
    /// nearly every one of those deletes is best-effort inside a catch - a SQLite file still held
    /// open, a handle not yet released. Each individual leak is harmless and each is invisible;
    /// together they had reached 5,876 directories on this machine, growing with every run. That is
    /// the uncontrolled temp growth this project checks the product for, produced by the tests that
    /// do the checking.
    ///
    /// Deliberately narrow: only under the temp path, only the <c>parux-</c> prefix the suites use,
    /// only entries older than <see cref="AbandonedAfter"/>, and every failure ignored so a
    /// directory another process still holds is simply left alone.
    /// </summary>
    private static void SweepAbandonedTestDirectories()
    {
        try
        {
            string temp = Path.GetTempPath();
            DateTime now = DateTime.UtcNow;

            foreach (string path in Directory.EnumerateDirectories(temp, "parux-*"))
            {
                try
                {
                    string name = Path.GetFileName(path);
                    // The per-process roots live under parux-tests and are managed by Redirect. Sweeping the
                    // parent would delete roots that concurrent test hosts are still using.
                    if (string.Equals(name, "parux-tests", StringComparison.OrdinalIgnoreCase)) continue;
                    bool product = ProductTempPrefixes.Any(prefix =>
                        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                    if (Directory.GetLastWriteTimeUtc(path) > now - (product ? ProductGrace : TestGrace))
                        continue;
                    Directory.Delete(path, recursive: true);
                }
                catch { /* held open, or gone already - either way, not this run's problem */ }
            }
        }
        catch { /* an unreadable temp directory must never fail the test run */ }
    }
}
