using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Crash and cross-process tests. A child process is this same test assembly, started through `dotnet vstest` with one
/// role test selected, so the child runs the real PaRuX code. At a deterministic barrier the child writes a marker file
/// and waits, and the parent kills the whole child process tree there. The next step then runs against exactly the
/// state that a dead process left on disk.
/// </summary>
public sealed class ShadowApplyCrossProcessTests : IDisposable
{
    private const string RoleVariable = "PARUX_TEST_CHILD_ROLE";
    private const string DirectoryVariable = "PARUX_TEST_CHILD_DIRECTORY";
    private const string DataRootVariable = "PARUX_TEST_CHILD_DATA_ROOT";
    private const string StepVariable = "PARUX_TEST_CHILD_STEP";
    private const string ChildRoleTest = "PaRuX.Server.Tests.ShadowApplyCrossProcessTests.ChildRole";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan LeaseReleaseWait = TimeSpan.FromSeconds(10);
    private readonly string _base = Path.Combine(Path.GetTempPath(), "parux-cross-process-" + Guid.NewGuid().ToString("N"));

    public ShadowApplyCrossProcessTests()
    {
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_second_process_cannot_take_the_project_lease_while_the_first_holds_it()
    {
        string original = Path.Combine(_base, "original");
        string source = Path.Combine(_base, "source");
        ShadowApplyScenario.Seed(original, source);
        using ChildProcess child = ChildProcess.Start("hold-lease", _base, step: null);
        child.AwaitMarker("lease-held");

        // Two real OS processes contend for one lease: this process is refused while the child holds it.
        Assert.Throws<IOException>(() => ShadowApplyTransaction.AcquireProjectLease(original).Dispose());
        Assert.Throws<IOException>(() => ShadowApplyTransaction.Begin(original, source, ShadowApplyScenario.Mutations()));
        child.Kill();

        // The operating system released the dead child's lease, so the folder can be leased again.
        using (AwaitProjectLease(original)) { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_killed_process_in_the_middle_of_an_apply_leaves_a_journal_that_recovery_restores(int stopBeforeStep)
    {
        string original = Path.Combine(_base, "original");
        string source = Path.Combine(_base, "source");
        ShadowApplyScenario.Seed(original, source);
        int steps = ShadowApplyScenario.Mutations().Length;
        string barrier = stopBeforeStep < steps ? "before-step-" + stopBeforeStep : "applied";
        using ChildProcess child = ChildProcess.Start("crash-mid-apply", _base,
            stopBeforeStep.ToString(CultureInfo.InvariantCulture));
        child.AwaitMarker(barrier);
        child.Kill();
        using (AwaitProjectLease(original)) { }

        string? journal = ShadowApplyTransaction.FindUnresolvedJournal(original);
        Assert.NotNull(journal);
        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal!, original);

        Assert.True(outcome.Success, outcome.Message);
        ShadowApplyScenario.AssertOriginal(original);
        Assert.Null(ShadowApplyTransaction.FindUnresolvedJournal(original));
    }

    [Fact]
    public async Task A_killed_multi_repository_apply_is_settled_by_the_next_apply_of_the_same_set()
    {
        CreateTwoRepositories(_base);
        WriteTaskChanges(_base);
        AgentRepositorySetDto authorities = AuthoritiesFor(_base);
        using ChildProcess child = ChildProcess.Start("multi-after-first", _base, step: null);
        child.AwaitMarker("primary-applied");
        child.Kill();
        AwaitRepositoryLeases();

        // The dead child had written the first repository and nothing else.
        Assert.Equal("A1\n", ReadNormalized(Path.Combine(_base, "repo-a", "a.txt")));
        Assert.Equal("B0\n", ReadNormalized(Path.Combine(_base, "repo-b", "b.txt")));
        Assert.NotNull(MultiRepositoryApplyService.FindUnresolvedJournal(authorities));

        // The next apply of the same set finds the journal, settles it, and then completes the whole set.
        MultiRepositoryApplyResult retry = await MultiRepositoryApplyService.ApplyAsync(
            authorities, Path.Combine(_base, "aggregate"), CancellationToken.None);

        Assert.True(retry.Success, retry.Message);
        Assert.Equal("A1\n", ReadNormalized(Path.Combine(_base, "repo-a", "a.txt")));
        Assert.Equal("B1\n", ReadNormalized(Path.Combine(_base, "repo-b", "b.txt")));
        Assert.Null(MultiRepositoryApplyService.FindUnresolvedJournal(authorities));
    }

    [Fact]
    public async Task An_interrupted_multi_apply_with_a_diverged_repository_is_refused_and_writes_nothing()
    {
        CreateTwoRepositories(_base);
        WriteTaskChanges(_base);
        AgentRepositorySetDto authorities = AuthoritiesFor(_base);
        using ChildProcess child = ChildProcess.Start("multi-after-first", _base, step: null);
        child.AwaitMarker("primary-applied");
        child.Kill();
        AwaitRepositoryLeases();
        File.WriteAllText(Path.Combine(_base, "repo-b", "b.txt"), "HUMAN-B\n"); // A person edits the second repository meanwhile.

        MultiRepositoryApplyResult retry = await MultiRepositoryApplyService.ApplyAsync(
            authorities, Path.Combine(_base, "aggregate"), CancellationToken.None);

        Assert.False(retry.Success);
        // Nothing was reverted: the first repository still holds the change the dead process wrote.
        Assert.Equal("A1\n", ReadNormalized(Path.Combine(_base, "repo-a", "a.txt")));
        Assert.Equal("HUMAN-B\n", ReadNormalized(Path.Combine(_base, "repo-b", "b.txt")));
        Assert.NotNull(MultiRepositoryApplyService.FindUnresolvedJournal(authorities));
    }

    [Fact]
    public async Task A_multi_repository_apply_is_refused_while_another_process_holds_one_of_its_repository_leases()
    {
        CreateTwoRepositories(_base);
        WriteTaskChanges(_base);
        AgentRepositorySetDto authorities = AuthoritiesFor(_base);
        using ChildProcess child = ChildProcess.Start("hold-repository-lease", _base, step: null);
        child.AwaitMarker("repository-lease-held");

        // The child holds the lease of repo-b. The apply takes the lease of repo-a first, is refused at repo-b, and must
        // release the lease it already took.
        MultiRepositoryApplyResult result = await MultiRepositoryApplyService.ApplyAsync(
            authorities, Path.Combine(_base, "aggregate"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Another PaRuX apply or recovery is using the original repository", result.Message, StringComparison.Ordinal);
        Assert.Equal("A0\n", ReadNormalized(Path.Combine(_base, "repo-a", "a.txt")));
        Assert.Equal("B0\n", ReadNormalized(Path.Combine(_base, "repo-b", "b.txt")));
        Assert.Empty(Git(Path.Combine(_base, "repo-a"), "status", "--porcelain"));
        Assert.Empty(Git(Path.Combine(_base, "repo-b"), "status", "--porcelain"));
        Assert.Null(MultiRepositoryApplyService.FindUnresolvedJournal(authorities));
        // The refused apply released the lease of repo-a, so another process can take it at once.
        using (ShadowApplyTransaction.AcquireProjectLease(Path.Combine(_base, "repo-a"))) { }
    }

    /// <summary>
    /// Entry point of the child processes above. Outside them it returns at once, and the parent tests assert every
    /// observable outcome themselves.
    /// </summary>
    [Fact]
    public async Task ChildRole()
    {
        string? role = Environment.GetEnvironmentVariable(RoleVariable);
        if (string.IsNullOrEmpty(role)) return;
        string directory = Environment.GetEnvironmentVariable(DirectoryVariable)
            ?? throw new InvalidOperationException("The child process has no working directory.");
        // The module initializer gave this process its own data root. Join the parent's, so that both processes contend on
        // the same lock files and read the same journal folders.
        PaRuXDataRoot.UseForTests(Environment.GetEnvironmentVariable(DataRootVariable)
            ?? throw new InvalidOperationException("The child process has no shared data root."));
        string original = Path.Combine(directory, "original");
        string source = Path.Combine(directory, "source");
        switch (role)
        {
            case "hold-lease":
                using (ShadowApplyTransaction.AcquireProjectLease(original))
                {
                    Barrier(directory, "lease-held");
                }
                break;
            case "hold-repository-lease":
                using (ShadowApplyTransaction.AcquireProjectLease(Path.Combine(directory, "repo-b")))
                {
                    Barrier(directory, "repository-lease-held");
                }
                break;
            case "crash-mid-apply":
            {
                int stop = int.Parse(Environment.GetEnvironmentVariable(StepVariable)!, CultureInfo.InvariantCulture);
                ShadowApplyTransaction transaction = ShadowApplyTransaction.Begin(original, source, ShadowApplyScenario.Mutations());
                transaction.Apply(index =>
                {
                    if (index == stop) Barrier(directory, "before-step-" + index.ToString(CultureInfo.InvariantCulture));
                });
                Barrier(directory, "applied");
                break;
            }
            case "multi-after-first":
            {
                using IDisposable hook = ShadowApplyFaults.Install((point, detail) =>
                {
                    if (point == "multi.after-repository" && detail == "primary") Barrier(directory, "primary-applied");
                });
                await MultiRepositoryApplyService.ApplyAsync(AuthoritiesFor(directory),
                    Path.Combine(directory, "aggregate"), CancellationToken.None);
                break;
            }
            default:
                throw new InvalidOperationException("Unknown child role: " + role);
        }
    }

    /// <summary>Writes the marker the parent waits for, then holds this process at the exact point until it is killed.</summary>
    private static void Barrier(string directory, string marker)
    {
        File.WriteAllText(Path.Combine(directory, marker), marker);
        DateTime limit = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < limit) Thread.Sleep(50);
        throw new TimeoutException("The parent did not stop this child process at barrier '" + marker + "'.");
    }

    private static void CreateTwoRepositories(string root)
    {
        CreateRepo(root, "repo-a", "a.txt", "A0\n");
        CreateRepo(root, "repo-b", "b.txt", "B0\n");
        string aggregate = Path.Combine(root, "aggregate");
        Directory.CreateDirectory(aggregate);
        Git(Path.Combine(root, "repo-a"), "worktree", "add", "-b", "parux-cross-a", Path.Combine(aggregate, "primary"), "HEAD");
        Git(Path.Combine(root, "repo-b"), "worktree", "add", "-b", "parux-cross-b", Path.Combine(aggregate, "backend"), "HEAD");
    }

    private static void WriteTaskChanges(string root)
    {
        File.WriteAllText(Path.Combine(root, "aggregate", "primary", "a.txt"), "A1\n");
        File.WriteAllText(Path.Combine(root, "aggregate", "backend", "b.txt"), "B1\n");
    }

    private static AgentRepositorySetDto AuthoritiesFor(string root) => new()
    {
        TaskId = "cross-process",
        Repositories = new List<AgentRepositoryAuthorityDto>
        {
            new() { Alias = "primary", WorkspaceRoot = Path.Combine(root, "repo-a"), IsPrimary = true, AssignedUtc = DateTime.UtcNow },
            new() { Alias = "backend", WorkspaceRoot = Path.Combine(root, "repo-b"), IsPrimary = false, AssignedUtc = DateTime.UtcNow }
        }
    };

    private static void CreateRepo(string root, string name, string file, string content)
    {
        string repo = Path.Combine(root, name);
        Directory.CreateDirectory(repo);
        Git(repo, "init");
        Git(repo, "config", "user.name", "PaRuX Tests");
        Git(repo, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", file);
        Git(repo, "commit", "-m", "baseline");
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', arguments) + " failed: " + error);
        return output;
    }

    /// <summary>
    /// Takes the project lease once the killed child has released it. Kill waits only for the dotnet host, while the test
    /// host that it started can still be closing its handles, so the lease is retried for a bounded time before failing.
    /// </summary>
    private static FileStream AwaitProjectLease(string original)
    {
        DateTime limit = DateTime.UtcNow + LeaseReleaseWait;
        while (true)
        {
            try
            {
                return ShadowApplyTransaction.AcquireProjectLease(original);
            }
            catch (IOException) when (DateTime.UtcNow < limit)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Waits until the killed child has released the lease of each repository of the two-repository set.</summary>
    private void AwaitRepositoryLeases()
    {
        AwaitProjectLease(Path.Combine(_base, "repo-a")).Dispose();
        AwaitProjectLease(Path.Combine(_base, "repo-b")).Dispose();
    }

    private static string ReadNormalized(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// A child test host running one role test. Its output is drained continuously, so that it can never block on a
    /// full pipe, and it is killed as a whole process tree.
    /// </summary>
    private sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly string _directory;
        private readonly StringBuilder _output;

        private ChildProcess(Process process, string directory, StringBuilder output)
        {
            _process = process;
            _directory = directory;
            _output = output;
        }

        internal static ChildProcess Start(string role, string directory, string? step)
        {
            var start = new ProcessStartInfo(DotNetHost())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(ShadowApplyCrossProcessTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + ChildRoleTest);
            start.Environment[RoleVariable] = role;
            start.Environment[DirectoryVariable] = directory;
            start.Environment[DataRootVariable] = PaRuXDataRoot.Path;
            if (step != null) start.Environment[StepVariable] = step;

            var output = new StringBuilder();
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new ChildProcess(process, directory, output);
        }

        /// <summary>Waits until the child has written the named barrier marker. Fails at once if the child exits first.</summary>
        internal void AwaitMarker(string marker)
        {
            string path = Path.Combine(_directory, marker);
            DateTime limit = DateTime.UtcNow + WaitLimit;
            while (!File.Exists(path))
            {
                if (_process.HasExited)
                    throw new InvalidOperationException("The child process exited before reaching '" + marker + "'. Output: " + Output());
                if (DateTime.UtcNow > limit)
                    throw new TimeoutException("The child process did not reach '" + marker + "'. Output: " + Output());
                Thread.Sleep(50);
            }
        }

        /// <summary>Kills the child and its descendants. No finally block or rollback runs in the child.</summary>
        internal void Kill()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        public void Dispose()
        {
            try { Kill(); }
            catch (InvalidOperationException) { }
            finally { _process.Dispose(); }
        }

        private string Output()
        {
            lock (_output)
            {
                string text = _output.ToString();
                return text.Length <= 4_000 ? text : text[^4_000..];
            }
        }

        private static void Append(StringBuilder output, string? line)
        {
            if (line == null) return;
            lock (output) output.AppendLine(line);
        }

        private static string DotNetHost()
        {
            string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            return !string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet";
        }
    }
}
