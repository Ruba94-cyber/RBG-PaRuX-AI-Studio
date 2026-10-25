using System;
using System.Collections.Generic;
using System.IO;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public class WindowsCodingSandboxAclTests
    {
        [Fact]
        public void ResolveGitDirectories_StandardDirectory_ReturnsGitDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parux-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                string gitDir = Path.Combine(tempDir, ".git");
                Directory.CreateDirectory(gitDir);

                // A .git directory inside the root is already covered by the root's own grant; no
                // extra (and never a sandbox-chosen) directory is added.
                List<string> dirs = WindowsCodingSandbox.ResolveGitDirectories(tempDir);
                Assert.Empty(dirs);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void ResolveGitDirectories_WorktreeWithCommondir_ReturnsBothDirs()
        {
            string root = Path.Combine(Path.GetTempPath(), "parux-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mainRepo = Path.Combine(root, "main");
                string mainGit = Path.Combine(mainRepo, ".git");
                string wtGit = Path.Combine(mainGit, "worktrees", "task1");
                Directory.CreateDirectory(wtGit);

                // Write commondir pointing to ../..
                File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..\n");

                string worktreeDir = Path.Combine(root, "worktree");
                Directory.CreateDirectory(worktreeDir);

                // Write .git file in worktree pointing to wtGit
                File.WriteAllText(Path.Combine(worktreeDir, ".git"), "gitdir: " + wtGit + "\n");
                // Git's back-link from the metadata to this worktree.
                File.WriteAllText(Path.Combine(wtGit, "gitdir"), Path.Combine(worktreeDir, ".git") + "\n");

                List<string> dirs = WindowsCodingSandbox.ResolveGitDirectories(worktreeDir);
                Assert.Equal(2, dirs.Count);
                Assert.Contains(Path.GetFullPath(wtGit), dirs);
                Assert.Contains(Path.GetFullPath(mainGit), dirs);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ResolveGitDirectories_WorktreeWithRelativeGitdir_ReturnsBothDirs()
        {
            string root = Path.Combine(Path.GetTempPath(), "parux-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mainRepo = Path.Combine(root, "main");
                string mainGit = Path.Combine(mainRepo, ".git");
                string wtGit = Path.Combine(mainGit, "worktrees", "task1");
                Directory.CreateDirectory(wtGit);
                File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..\n");

                string worktreeDir = Path.Combine(root, "worktree");
                Directory.CreateDirectory(worktreeDir);

                // Relative gitdir: ../main/.git/worktrees/task1
                File.WriteAllText(Path.Combine(worktreeDir, ".git"), "gitdir: ../main/.git/worktrees/task1\n");
                File.WriteAllText(Path.Combine(wtGit, "gitdir"), Path.Combine(worktreeDir, ".git") + "\n");

                List<string> dirs = WindowsCodingSandbox.ResolveGitDirectories(worktreeDir);
                Assert.Equal(2, dirs.Count);
                Assert.Contains(Path.GetFullPath(wtGit), dirs);
                Assert.Contains(Path.GetFullPath(mainGit), dirs);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ResolveGitDirectories_WorktreeLinkWithoutBackLink_IsNotGranted()
        {
            string root = Path.Combine(Path.GetTempPath(), "parux-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string wtGit = Path.Combine(root, "main", ".git", "worktrees", "task1");
                Directory.CreateDirectory(wtGit);
                string worktreeDir = Path.Combine(root, "worktree");
                Directory.CreateDirectory(worktreeDir);
                File.WriteAllText(Path.Combine(worktreeDir, ".git"), "gitdir: " + wtGit + "\n");

                Assert.Empty(WindowsCodingSandbox.ResolveGitDirectories(worktreeDir));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ResolveGitDirectories_NonGitDirectory_ReturnsEmpty()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parux-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                List<string> dirs = WindowsCodingSandbox.ResolveGitDirectories(tempDir);
                Assert.Empty(dirs);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task RunAsync_InDirectory_CanRunEchoInsideAppContainer()
        {
            if (!OperatingSystem.IsWindows()) return;

            // Keep both workspace and test-only PaRuX state under a fresh user-profile directory;
            // ACL updates against shared %TEMP%/%LOCALAPPDATA% ancestors can block for 90+ seconds.
            string tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".parux-sandbox-tests",
                "acl-" + Guid.NewGuid().ToString("N"));
            string previousDataRoot = PaRuXDataRoot.Path;
            bool cleanupComplete = true;
            try
            {
                Directory.CreateDirectory(tempDir);
                PaRuXDataRoot.UseForTests(Path.Combine(tempDir, "parux-data"));
                WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
                    tempDir, "echo hello", System.Threading.CancellationToken.None);

                Assert.True(result.ExitCode == 0, $"ExitCode: {result.ExitCode}, Error: {result.Error}, Output: {result.Output}");
                Assert.Contains("hello", result.Output);
            }
            finally
            {
                try { cleanupComplete = WindowsCodingSandbox.DeleteProfileForTests(tempDir, WindowsCodingSandbox.NetworkMode.None); } catch { cleanupComplete = false; }
                finally
                {
                    PaRuXDataRoot.UseForTests(previousDataRoot);
                    if (cleanupComplete)
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            }
        }
    }
}
