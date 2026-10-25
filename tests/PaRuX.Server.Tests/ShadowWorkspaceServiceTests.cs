using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ShadowWorkspaceServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public ShadowWorkspaceServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "parux-shadow-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task EnsureShadowRepositoryAsync_leaves_user_directory_without_git()
    {
        string userProject = Path.Combine(_tempRoot, "MyCustomApp");
        Directory.CreateDirectory(userProject);
        await File.WriteAllTextAsync(Path.Combine(userProject, "Program.cs"), "Console.WriteLine(\"Hello\");");
        await File.WriteAllTextAsync(Path.Combine(userProject, "README.md"), "# My Custom App");

        string shadowRepo = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(userProject);

        Assert.True(Directory.Exists(shadowRepo));
        Assert.True(Directory.Exists(Path.Combine(shadowRepo, ".git")));
        Assert.True(ShadowWorkspaceService.IsShadowRepository(shadowRepo));
        Assert.Equal(Path.GetFullPath(userProject), ShadowWorkspaceService.TryGetOriginalPath(shadowRepo));

        // CRITICAL REQUIREMENT: User's original folder must NEVER have .git initialized
        Assert.False(Directory.Exists(Path.Combine(userProject, ".git")),
            "PaRuX must not initialize Git inside the user's unversioned directory.");

        // Shadow repo must have mirrored user files
        Assert.True(File.Exists(Path.Combine(shadowRepo, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(shadowRepo, "README.md")));
        Assert.Equal("Console.WriteLine(\"Hello\");", await File.ReadAllTextAsync(Path.Combine(shadowRepo, "Program.cs")));
    }

    [Fact]
    public async Task Shadow_apply_refuses_worktree_mutated_after_user_review()
    {
        string userFolder = Path.Combine(_tempRoot, "ReviewProtected");
        Directory.CreateDirectory(userFolder);
        await File.WriteAllTextAsync(Path.Combine(userFolder, "code.txt"), "original");

        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(userFolder);
        WorktreeResult worktree = await WorktreeService.CreateAsync(
            shadow, "task-review-protected", CancellationToken.None);
        Assert.True(worktree.Success, worktree.Error);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "code.txt"), "approved");
        string? approved = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            worktree.Path, CancellationToken.None);
        Assert.NotNull(approved);

        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "code.txt"), "unreviewed");
        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, worktree.Path, CancellationToken.None, approved);
        Assert.False(result.Success);
        Assert.Equal("original", await File.ReadAllTextAsync(
            Path.Combine(userFolder, "code.txt")));
        Assert.False(Directory.Exists(Path.Combine(userFolder, ".git")));
    }

    [Fact]
    public async Task Shadow_renamed_file_cannot_delete_a_newer_user_edit()
    {
        string folder = Path.Combine(_tempRoot, "RenameProtected");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"),
            "baseline content which will be renamed");

        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult wt = await WorktreeService.CreateAsync(
            shadow, "task-rename-protection", CancellationToken.None);
        Assert.True(wt.Success, wt.Error);
        File.Move(Path.Combine(wt.Path, "code.txt"),
            Path.Combine(wt.Path, "renamed.txt"));

        // The user modifies the original while PaRuX's isolated task is open.
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"),
            "newer human content");
        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, wt.Path, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("newer human content",
            await File.ReadAllTextAsync(Path.Combine(folder, "code.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "renamed.txt")));
    }

    [Fact]
    public async Task Shadow_added_filename_must_not_overwrite_an_unrelated_user_file()
    {
        string folder = Path.Combine(_tempRoot, "AddProtected");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "original");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult wt = await WorktreeService.CreateAsync(
            shadow, "task-add-protection", CancellationToken.None);
        Assert.True(wt.Success, wt.Error);
        await File.WriteAllTextAsync(Path.Combine(wt.Path, "new.txt"), "agent new");
        await File.WriteAllTextAsync(Path.Combine(folder, "new.txt"), "human new");

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, wt.Path, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("human new",
            await File.ReadAllTextAsync(Path.Combine(folder, "new.txt")));
        Assert.Equal("original",
            await File.ReadAllTextAsync(Path.Combine(folder, "code.txt")));
    }

    [Fact]
    public async Task Shadow_apply_accepts_reviewed_tracked_and_new_files()
    {
        string userFolder = Path.Combine(_tempRoot, "ReviewAccepted");
        Directory.CreateDirectory(userFolder);
        await File.WriteAllTextAsync(Path.Combine(userFolder, "code.txt"), "original");

        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(userFolder);
        WorktreeResult worktree = await WorktreeService.CreateAsync(
            shadow, "task-reviewed-copy", CancellationToken.None);
        Assert.True(worktree.Success, worktree.Error);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "code.txt"), "approved");
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "new.txt"), "new");
        string? approved = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            worktree.Path, CancellationToken.None);
        Assert.NotNull(approved);

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, worktree.Path, CancellationToken.None, approved);
        Assert.True(result.Success, result.Message);
        Assert.Equal("approved", await File.ReadAllTextAsync(
            Path.Combine(userFolder, "code.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(
            Path.Combine(userFolder, "new.txt")));
        Assert.False(Directory.Exists(Path.Combine(userFolder, ".git")));
    }

    [Fact]
    public async Task Shadow_initial_sync_does_not_import_files_outside_the_selected_folder_via_a_link()
    {
        string selected = Path.Combine(_tempRoot, "SymlinkProject");
        string outside = Path.Combine(_tempRoot, "OutsidePrivate");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(selected, "main.txt"), "main");
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "not part of project");
        string symlink = Path.Combine(selected, "external");

        try { Directory.CreateSymbolicLink(symlink, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException)
        {
            // Requires Windows Developer Mode or privilege on some real PCs.
            return;
        }

        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(selected);

        Assert.True(File.Exists(Path.Combine(shadow, "main.txt")));
        Assert.False(Directory.Exists(Path.Combine(shadow, "external")));
        Assert.False(File.Exists(Path.Combine(shadow, "external", "secret.txt")));
        Assert.Equal("not part of project",
            await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
    }

    [Fact]
    public async Task ApplyToOriginalAsync_copies_changes_and_leaves_user_directory_unversioned()
    {
        string userProject = Path.Combine(_tempRoot, "AppToMutate");
        Directory.CreateDirectory(userProject);
        await File.WriteAllTextAsync(Path.Combine(userProject, "code.txt"), "version 1");

        string shadowRepo = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(userProject);

        // Create an isolated task worktree exactly as PaRuX does in production
        WorktreeResult wtResult = await WorktreeService.CreateAsync(shadowRepo, "task-test-shadow", CancellationToken.None);
        Assert.True(wtResult.Success, "Worktree creation failed: " + wtResult.Error);

        // Agent mutates code.txt and adds new_feature.txt in the worktree
        await File.WriteAllTextAsync(Path.Combine(wtResult.Path, "code.txt"), "version 2 with improvements");
        await File.WriteAllTextAsync(Path.Combine(wtResult.Path, "new_feature.txt"), "brand new file");

        // Now ApplyToOriginalAsync
        CodeApplyResult applyResult = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadowRepo, wtResult.Path, CancellationToken.None);

        Assert.True(applyResult.Success, "Apply failed: " + applyResult.Message);
        Assert.False(Directory.Exists(Path.Combine(userProject, ".git")),
            "User project must still NOT have .git after apply.");

        // Check user files were updated
        Assert.Equal("version 2 with improvements", await File.ReadAllTextAsync(Path.Combine(userProject, "code.txt")));
        Assert.Equal("brand new file", await File.ReadAllTextAsync(Path.Combine(userProject, "new_feature.txt")));
    }

    [Fact]
    public async Task Forged_origin_marker_in_a_normal_repository_is_not_treated_as_shadow()
    {
        // The victim folder already matches the repository baseline, so only the forged
        // marker stands between an agent edit and a silent write into this folder.
        string victim = Path.Combine(_tempRoot, "ForgedVictim");
        Directory.CreateDirectory(victim);
        await File.WriteAllTextAsync(Path.Combine(victim, "code.txt"), "repo original");
        string repo = Path.Combine(_tempRoot, "ForgedRepo");
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "code.txt"), "repo original");
        Git(repo, "init");
        Git(repo, "config", "user.name", "PaRuX Tests");
        Git(repo, "config", "user.email", "tests@parux.invalid");
        Git(repo, "add", "code.txt");
        Git(repo, "commit", "-m", "baseline");
        await File.WriteAllTextAsync(Path.Combine(repo, ShadowWorkspaceService.OriginMarkerFilename),
            JsonSerializer.Serialize(new ShadowOriginMetadata { OriginalPath = victim }));

        Assert.False(ShadowWorkspaceService.IsShadowRepository(repo));
        Assert.Null(ShadowWorkspaceService.TryGetOriginalPath(repo));

        WorktreeResult worktree = await WorktreeService.CreateAsync(repo, "task-forged-marker", CancellationToken.None);
        Assert.True(worktree.Success, worktree.Error);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "code.txt"), "agent edit");
        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            repo, worktree.Path, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("repo original", await File.ReadAllTextAsync(Path.Combine(victim, "code.txt")));
    }

    [Fact]
    public async Task Shadow_marker_retargeted_to_another_folder_is_not_trusted()
    {
        string folder = Path.Combine(_tempRoot, "RetargetOriginal");
        string other = Path.Combine(_tempRoot, "RetargetOther");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "original");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        Assert.True(ShadowWorkspaceService.IsShadowRepository(shadow));

        await File.WriteAllTextAsync(Path.Combine(shadow, ShadowWorkspaceService.OriginMarkerFilename),
            JsonSerializer.Serialize(new ShadowOriginMetadata { OriginalPath = other }));

        Assert.False(ShadowWorkspaceService.IsShadowRepository(shadow));
        Assert.Null(ShadowWorkspaceService.TryGetOriginalPath(shadow));
    }

    [Fact]
    public async Task Task_started_before_a_user_edit_refuses_after_a_later_task_rebaselines()
    {
        string folder = Path.Combine(_tempRoot, "MovingBaseline");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "original");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult taskA = await WorktreeService.CreateAsync(shadow, "task-moving-a", CancellationToken.None);
        Assert.True(taskA.Success, taskA.Error);
        await File.WriteAllTextAsync(Path.Combine(taskA.Path, "code.txt"), "agent A");

        // The user edits the folder after task A started; task B then re-baselines the shadow,
        // which would absorb that edit into the shadow HEAD if apply still trusted HEAD.
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "user edit after A started");
        await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult taskB = await WorktreeService.CreateAsync(shadow, "task-moving-b", CancellationToken.None);
        Assert.True(taskB.Success, taskB.Error);

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, taskA.Path, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("user edit after A started", await File.ReadAllTextAsync(Path.Combine(folder, "code.txt")));
    }

    [Fact]
    public async Task Rebaseline_without_a_user_edit_still_lets_the_earlier_task_apply()
    {
        string folder = Path.Combine(_tempRoot, "StableBaseline");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "original");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult taskA = await WorktreeService.CreateAsync(shadow, "task-stable-a", CancellationToken.None);
        Assert.True(taskA.Success, taskA.Error);
        await File.WriteAllTextAsync(Path.Combine(taskA.Path, "code.txt"), "agent A");

        await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult taskB = await WorktreeService.CreateAsync(shadow, "task-stable-b", CancellationToken.None);
        Assert.True(taskB.Success, taskB.Error);

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, taskA.Path, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("agent A", await File.ReadAllTextAsync(Path.Combine(folder, "code.txt")));
    }

    [Fact]
    public async Task Task_started_after_a_user_edit_applies_its_own_changes_on_top_of_that_edit()
    {
        string folder = Path.Combine(_tempRoot, "EditBeforeTask");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "original");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "user edit before task");
        await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult taskB = await WorktreeService.CreateAsync(shadow, "task-after-edit", CancellationToken.None);
        Assert.True(taskB.Success, taskB.Error);
        await File.WriteAllTextAsync(Path.Combine(taskB.Path, "code.txt"), "agent B");

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, taskB.Path, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("agent B", await File.ReadAllTextAsync(Path.Combine(folder, "code.txt")));
    }

    [Fact]
    public async Task Agent_gitattributes_eol_rule_cannot_make_a_different_user_file_look_identical()
    {
        string folder = Path.Combine(_tempRoot, "EolSpoof");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "code.txt"), "original");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult worktree = await WorktreeService.CreateAsync(shadow, "task-eol-spoof", CancellationToken.None);
        Assert.True(worktree.Success, worktree.Error);

        // The agent's own .gitattributes makes CRLF normalize to LF when hashed with filters, so its CRLF
        // file would hash exactly like the LF file the user created after the task started.
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, ".gitattributes"), "*.txt text eol=crlf\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "notes.txt"), "alpha\r\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "alpha\n");

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, worktree.Path, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("alpha\n", await File.ReadAllTextAsync(Path.Combine(folder, "notes.txt")));
    }

    [Fact]
    public void Duplicate_expected_baseline_path_is_refused_instead_of_thrown()
    {
        // Paths are compared like the Windows file system does, so case-only duplicates collide too.
        var expected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        Assert.True(ShadowWorkspaceService.TryRecordExpectedBlob(expected, "Code.txt", "abc"));
        Assert.False(ShadowWorkspaceService.TryRecordExpectedBlob(expected, "code.txt", null));

        Assert.Single(expected);
        Assert.Equal("abc", expected["CODE.txt"]);
    }

    [Fact]
    public async Task Unexpected_failure_during_shadow_apply_still_names_the_recovery_folder_when_rollback_is_incomplete()
    {
        string folder = Path.Combine(_tempRoot, "UnexpectedFailure");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "orig-a");
        await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "orig-b");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult wt = await WorktreeService.CreateAsync(
            shadow, "task-unexpected-failure", CancellationToken.None);
        Assert.True(wt.Success, wt.Error);
        await File.WriteAllTextAsync(Path.Combine(wt.Path, "a.txt"), "new-a");
        await File.WriteAllTextAsync(Path.Combine(wt.Path, "b.txt"), "new-b");

        CodeApplyResult result;
        using (ShadowApplyFaults.Install((point, detail) =>
        {
            if (point != "shadow.step" || detail != "1") return;
            // The user edits a.txt after PaRuX wrote it, so its rollback is no longer safe. Then a failure of a type the
            // apply does not expect occurs.
            File.WriteAllText(Path.Combine(folder, "a.txt"), "human-a");
            throw new ArgumentException("Injected non-IO failure during apply.");
        }))
        {
            result = await ShadowWorkspaceService.ApplyToOriginalAsync(shadow, wt.Path, CancellationToken.None);
        }

        Assert.False(result.Success);
        Assert.Contains("Inspect the preserved recovery folder", result.Message, StringComparison.Ordinal);
        Assert.Equal("human-a", await File.ReadAllTextAsync(Path.Combine(folder, "a.txt")));
        foreach (string journal in Directory.EnumerateDirectories(ShadowApplyTransaction.RecoveryBaseDirectory(),
            ShadowApplyTransaction.ProjectKey(folder) + "-*").ToList())
            Directory.Delete(journal, recursive: true); // Synthetic test recovery only.
    }

    [Fact]
    public async Task Failure_before_the_journal_exists_is_reported_neutrally_and_leaves_the_folder_untouched()
    {
        string folder = Path.Combine(_tempRoot, "BeforeJournal");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "orig-a");
        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult wt = await WorktreeService.CreateAsync(shadow, "task-before-journal", CancellationToken.None);
        Assert.True(wt.Success, wt.Error);
        await File.WriteAllTextAsync(Path.Combine(wt.Path, "a.txt"), "new-a");

        CodeApplyResult result;
        using (ShadowApplyFaults.Install((point, detail) =>
        {
            // A failure of a type the apply does not expect, raised while the recovery journal is being written.
            if (point == "shadow.manifest-writing") throw new ArgumentException("Injected non-journal failure.");
        }))
        {
            result = await ShadowWorkspaceService.ApplyToOriginalAsync(shadow, wt.Path, CancellationToken.None);
        }

        Assert.False(result.Success);
        Assert.Contains("could not start", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("remains unresolved", result.Message, StringComparison.Ordinal);
        Assert.Equal("orig-a", await File.ReadAllTextAsync(Path.Combine(folder, "a.txt")));
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
