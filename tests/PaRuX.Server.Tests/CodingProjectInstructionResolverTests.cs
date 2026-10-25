using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingProjectInstructionResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-project-instructions-" + Guid.NewGuid().ToString("N"));

    public CodingProjectInstructionResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Root_first_and_nearest_changed_directory_last_with_one_file_per_directory()
    {
        string nested = Path.Combine(_root, "src", "feature");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "Root constraint");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "Ignored duplicate");
        File.WriteAllText(Path.Combine(_root, "src", "AGENTS.md"), "Source constraint");
        File.WriteAllText(Path.Combine(nested, "AGENTS.md"), "Feature constraint");

        string result = CodingProjectInstructionResolver.Collect(
            _root, new[] { "src/feature/MyClass.cs" }, CancellationToken.None);
        Assert.Contains("Root constraint", result);
        Assert.Contains("Source constraint", result);
        Assert.Contains("Feature constraint", result);
        Assert.DoesNotContain("Ignored duplicate", result);
        Assert.True(result.IndexOf("Root constraint", StringComparison.Ordinal)
            < result.IndexOf("Source constraint", StringComparison.Ordinal));
        Assert.True(result.IndexOf("Source constraint", StringComparison.Ordinal)
            < result.IndexOf("Feature constraint", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_rules_file_is_loaded_with_agents_file_capped_and_labelled()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".parux"));
        File.WriteAllText(Path.Combine(_root, ".parux", "rules.md"),
            "Always run dotnet test before finishing. " + new string('x', 6_000));
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "Root constraint");

        string result = CodingProjectInstructionResolver.Collect(
            _root, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains(".parux/rules.md (project rules)", result);
        Assert.Contains("Always run dotnet test", result);
        Assert.Contains("[rules truncated]", result);
        Assert.Contains("Root constraint", result);
        Assert.True(result.Length < CodingProjectInstructionResolver.MaximumTotalCharacters + 200);
    }

    [Fact]
    public void Empty_project_rules_file_adds_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".parux"));
        File.WriteAllText(Path.Combine(_root, ".parux", "rules.md"), "   ");
        Assert.Equal(string.Empty, CodingProjectInstructionResolver.Collect(
            _root, Array.Empty<string>(), CancellationToken.None));
    }

    [Fact]
    public void First_step_loads_only_instructions_of_file_explicitly_named_in_goal()
    {
        string target = Path.Combine(_root, "src", "feature");
        string unrelated = Path.Combine(_root, "src", "unrelated");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "Root rules");
        File.WriteAllText(Path.Combine(target, "AGENTS.md"), "Feature-specific tests required");
        File.WriteAllText(Path.Combine(unrelated, "AGENTS.md"), "Do not leak unrelated rules");

        string result = CodingProjectInstructionResolver.Collect(
            _root, Array.Empty<string>(), CancellationToken.None,
            "Please update src/feature/NewWidget.cs and run its project tests.");

        Assert.Contains("Root rules", result);
        Assert.Contains("Feature-specific tests required", result);
        Assert.DoesNotContain("Do not leak unrelated rules", result);
    }

    [Fact]
    public void Task_file_targets_do_not_allow_escaping_the_workspace()
    {
        string feature = Path.Combine(_root, "real");
        Directory.CreateDirectory(feature);
        File.WriteAllText(Path.Combine(feature, "AGENTS.md"), "Local instructions");
        string result = CodingProjectInstructionResolver.Collect(
            _root, Array.Empty<string>(), CancellationToken.None,
            "Update ../private/secret.cs or https://example.com/stolen.txt, "
                + "then inspect real/NewFile.cs.");

        Assert.Contains("Local instructions", result);
        Assert.DoesNotContain("private", result);
        Assert.DoesNotContain("stolen", result);
    }

    [Fact]
    public void Task_targeted_rules_still_ignore_symbolic_link_directory()
    {
        string outside = Path.Combine(Path.GetTempPath(),
            "parux-task-target-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string linked = Path.Combine(_root, "shortcut");
        try
        {
            File.WriteAllText(Path.Combine(outside, "AGENTS.md"), "outside-only rule");
            try { Directory.CreateSymbolicLink(linked, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }
            string result = CodingProjectInstructionResolver.Collect(
                _root, Array.Empty<string>(), CancellationToken.None,
                "Please update shortcut/Injected.cs");
            Assert.DoesNotContain("outside-only rule", result);
        }
        finally
        {
            try { if (Directory.Exists(linked)) Directory.Delete(linked); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void Fallback_instructions_are_supported_without_copying_Jan_legacy_files()
    {
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "Project-specific conventions");
        File.WriteAllText(Path.Combine(_root, "JAN.md"), "Not a PaRuX repository instruction");
        string result = CodingProjectInstructionResolver.Collect(
            _root, Array.Empty<string>(), CancellationToken.None);
        Assert.Contains("Project-specific conventions", result);
        Assert.DoesNotContain("Not a PaRuX repository instruction", result);
    }

    [Fact]
    public void Path_escape_cannot_load_outside_repositories()
    {
        string outside = Path.Combine(Path.GetTempPath(),
            "parux-outside-instructions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "AGENTS.md"), "outside private data");
            File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "in scope");
            string result = CodingProjectInstructionResolver.Collect(
                _root, new[] { "../" + Path.GetFileName(outside) + "/X.cs",
                    outside + "/X.cs" }, CancellationToken.None);
            Assert.Contains("in scope", result);
            Assert.DoesNotContain("outside private data", result);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void Directory_junction_or_symlink_is_not_followed_into_a_foreign_tree()
    {
        string outside = Path.Combine(Path.GetTempPath(),
            "parux-external-guidance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string linked = Path.Combine(_root, "linked");
        try
        {
            File.WriteAllText(Path.Combine(outside, "AGENTS.md"), "secret outside");
            try { Directory.CreateSymbolicLink(linked, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                // Some Windows runners lack symlink privilege; the test is
                // exercised wherever the OS supports disposable symlinks.
                return;
            }
            string result = CodingProjectInstructionResolver.Collect(
                _root, new[] { "linked/source.cs" }, CancellationToken.None);
            Assert.DoesNotContain("secret outside", result);
        }
        finally
        {
            try { if (Directory.Exists(linked)) Directory.Delete(linked); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void Larger_guidance_is_bounded_and_cancellation_is_observed()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"),
            new string('x', CodingProjectInstructionResolver.MaximumDocumentCharacters + 1000));
        string result = CodingProjectInstructionResolver.Collect(
            _root, Array.Empty<string>(), CancellationToken.None);
        Assert.True(result.Length <= CodingProjectInstructionResolver.MaximumTotalCharacters);
        Assert.Contains("[guidance truncated]", result);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            CodingProjectInstructionResolver.Collect(
                _root, new[] { "src/App.cs" }, cts.Token));
    }

    [Fact]
    public void Guidance_not_related_to_changed_subtree_is_not_injected()
    {
        string projectA = Path.Combine(_root, "project-a");
        string projectB = Path.Combine(_root, "project-b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        File.WriteAllText(Path.Combine(projectA, "AGENTS.md"), "Project A rules");
        File.WriteAllText(Path.Combine(projectB, "AGENTS.md"), "Project B rules");

        string result = CodingProjectInstructionResolver.Collect(
            _root, new[] { "project-a/src/A.cs" }, CancellationToken.None);
        Assert.Contains("Project A rules", result);
        Assert.DoesNotContain("Project B rules", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
