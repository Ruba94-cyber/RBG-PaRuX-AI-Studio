using System.Diagnostics;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

public sealed class OwnedProcessLineageTests
{
    [Fact]
    public void Captures_only_descendants_of_the_owned_process_and_requires_full_identity_match()
    {
        using Process ownedRoot = StartHelper("cmd.exe", "/d /c ping.exe -n 20 127.0.0.1");
        using Process unrelatedSibling = StartHelper("ping.exe", "-n 20 127.0.0.1");
        try
        {
            OwnedProcessIdentity root = OwnedProcessLineage.Capture(ownedRoot.Id);
            OwnedProcessIdentity foreign = OwnedProcessLineage.Capture(unrelatedSibling.Id);
            string pingImage = Path.Combine(Environment.SystemDirectory, "ping.exe");
            OwnedProcessIdentity? child = null;
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(5) && child is null)
            {
                OwnedProcessIdentity[] discovered = OwnedProcessLineage.CaptureDescendants(root)
                    .Where(identity => string.Equals(identity.ImagePath, pingImage, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (discovered.Length > 0) child = discovered[0];
                if (child is null) Thread.Sleep(25);
            }

            Assert.True(child.HasValue, "No verified ping.exe descendant was returned for the owned cmd.exe process.");
            Assert.True(child.Value.ProcessId > 0);
            Assert.True(child.Value.StartTimeUtcTicks >= root.StartTimeUtcTicks);
            Assert.Equal(Path.GetFullPath(pingImage), child.Value.ImagePath, ignoreCase: true);
            Assert.NotEqual(root.ProcessId, child.Value.ProcessId);
            Assert.True(OwnedProcessLineage.IsSameIdentity(foreign, OwnedProcessLineage.Capture(unrelatedSibling.Id)));
            Assert.DoesNotContain(OwnedProcessLineage.CaptureDescendants(root),
                identity => identity.ProcessId == foreign.ProcessId);
            Assert.False(OwnedProcessLineage.IsSameIdentity(child.Value,
                child.Value with { StartTimeUtcTicks = child.Value.StartTimeUtcTicks + 1 }));
            Assert.False(OwnedProcessLineage.IsSameIdentity(child.Value,
                child.Value with { ImagePath = child.Value.ImagePath + ".different" }));
        }
        finally
        {
            StopTestProcessTree(ownedRoot);
            StopTestProcessTree(unrelatedSibling);
        }
    }

    [Fact]
    public void Retries_identity_on_the_retained_handle_and_failure_cleanup_preserves_unrelated_process()
    {
        using Process launched = StartHelper("ping.exe", "-n 20 127.0.0.1");
        using Process unrelated = StartHelper("ping.exe", "-n 20 127.0.0.1");
        bool launchedStopped = false;
        bool unrelatedStopped = false;
        try
        {
            _ = launched.SafeHandle;
            _ = unrelated.SafeHandle;
            Assert.True(OwnedProcessLineage.TryCaptureWhenReady(launched, TimeSpan.FromSeconds(1), out var identity));
            Assert.Equal(launched.Id, identity.ProcessId);
            Assert.True(identity.StartTimeUtcTicks > 0);
            Assert.Equal(Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "ping.exe")), identity.ImagePath,
                ignoreCase: true);

            Assert.True(OwnedProcessLineage.TerminateExactProcess(launched, TimeSpan.FromSeconds(2)));
            Assert.True(launched.HasExited);
            Assert.False(unrelated.HasExited, "Failure cleanup must not terminate an unrelated same-image process.");
        }
        finally
        {
            launchedStopped = OwnedProcessLineage.TerminateExactProcess(launched, TimeSpan.FromSeconds(2));
            unrelatedStopped = OwnedProcessLineage.TerminateExactProcess(unrelated, TimeSpan.FromSeconds(2));
        }
        Assert.True(launchedStopped, "The owned helper must be stopped even when an assertion fails.");
        Assert.True(unrelatedStopped, "The unrelated helper must be stopped by test teardown.");
    }

    [Fact]
    public void Expired_launch_identity_fails_fast_and_exact_cleanup_does_not_touch_sibling()
    {
        using Process exited = StartHelper("cmd.exe", "/d /c exit 0");
        using Process unrelated = StartHelper("ping.exe", "-n 20 127.0.0.1");
        bool exitedStopped = false;
        bool unrelatedStopped = false;
        try
        {
            _ = exited.SafeHandle;
            _ = unrelated.SafeHandle;
            Assert.True(exited.WaitForExit(3_000), "The synthetic launch helper should exit promptly.");
            var elapsed = Stopwatch.StartNew();
            Assert.False(OwnedProcessLineage.TryCaptureWhenReady(exited, TimeSpan.FromSeconds(2), out _));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "An exited launch must not consume the retry deadline.");
            Assert.True(OwnedProcessLineage.TerminateExactProcess(exited, TimeSpan.FromSeconds(1)));
            Assert.False(unrelated.HasExited, "An exited owned handle must not affect an unrelated process.");
        }
        finally
        {
            exitedStopped = OwnedProcessLineage.TerminateExactProcess(exited, TimeSpan.FromSeconds(2));
            unrelatedStopped = OwnedProcessLineage.TerminateExactProcess(unrelated, TimeSpan.FromSeconds(2));
        }
        Assert.True(exitedStopped);
        Assert.True(unrelatedStopped);
    }

    [Fact]
    public void Rejects_child_older_than_parent_and_reused_intermediate_identity()
    {
        OwnedProcessIdentity root = Identity(100, 1000, "root.exe");
        OwnedProcessIdentity intermediate = Identity(200, 2000, "middle.exe");
        OwnedProcessIdentity childOlderThanParent = Identity(300, 1500, "child.exe");
        ProcessParent[] tree = { new(100, 1), new(200, 100), new(300, 200) };
        var identities = Identities(root, intermediate, childOlderThanParent);

        Assert.False(OwnedProcessLineage.IsStableLineage(root, childOlderThanParent.ProcessId,
            tree, identities, tree, identities));

        OwnedProcessIdentity validChild = childOlderThanParent with { StartTimeUtcTicks = 2500 };
        var validIdentities = Identities(root, intermediate, validChild);
        Assert.True(OwnedProcessLineage.IsStableLineage(root, validChild.ProcessId,
            tree, validIdentities, tree, validIdentities));

        OwnedProcessIdentity recycledIntermediate = intermediate with { StartTimeUtcTicks = intermediate.StartTimeUtcTicks + 1 };
        var secondSnapshotIdentities = Identities(root, recycledIntermediate, validChild);
        Assert.False(OwnedProcessLineage.IsStableLineage(root, validChild.ProcessId,
            tree, validIdentities, tree, secondSnapshotIdentities));

        ProcessParent[] missingIntermediate = { new(100, 1), new(300, 200) };
        Assert.False(OwnedProcessLineage.IsStableLineage(root, validChild.ProcessId,
            tree, validIdentities, missingIntermediate, validIdentities));
    }

    private static OwnedProcessIdentity Identity(int processId, long started, string image) =>
        new(processId, started, Path.GetFullPath(image));

    private static Dictionary<int, OwnedProcessIdentity> Identities(params OwnedProcessIdentity[] identities) =>
        identities.ToDictionary(identity => identity.ProcessId);

    private static Process StartHelper(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start process-lineage test helper.");

    private static void StopTestProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { }
    }
}
