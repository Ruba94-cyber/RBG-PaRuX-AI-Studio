using System;
using System.IO;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The three-step scenario shared by the in-process and the child-process recovery tests: two modified files and one
/// added file inside a new folder, so that a restore has to remove the folder the apply created as well.
/// </summary>
internal static class ShadowApplyScenario
{
    internal const string BeforeA = "before A";
    internal const string BeforeB = "before B";

    internal static ShadowApplyTransaction.Mutation[] Mutations() => new[]
    {
        new ShadowApplyTransaction.Mutation("Modified", "a.txt"),
        new ShadowApplyTransaction.Mutation("Modified", "b.txt"),
        new ShadowApplyTransaction.Mutation("Added", "sub/c.txt")
    };

    internal static void Seed(string original, string source)
    {
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(original, "a.txt"), BeforeA);
        File.WriteAllText(Path.Combine(original, "b.txt"), BeforeB);
        File.WriteAllText(Path.Combine(source, "a.txt"), "approved A");
        File.WriteAllText(Path.Combine(source, "b.txt"), "approved B");
        File.WriteAllText(Path.Combine(source, "sub", "c.txt"), "approved C");
    }

    /// <summary>The folder is back to its exact pre-apply state: the originals, no folder the apply created, and no
    /// PaRuX staging file left anywhere in the tree.</summary>
    internal static void AssertOriginal(string original)
    {
        Assert.Equal(BeforeA, File.ReadAllText(Path.Combine(original, "a.txt")));
        Assert.Equal(BeforeB, File.ReadAllText(Path.Combine(original, "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(original, "sub")));
        Assert.DoesNotContain(Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(".parux-", StringComparison.Ordinal));
    }
}

/// <summary>Stops the real code at an exact point, as a process death would. Only the test seams throw it.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException() : base("Simulated process death.")
    {
    }
}
