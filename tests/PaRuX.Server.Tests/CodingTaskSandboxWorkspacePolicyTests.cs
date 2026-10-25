using System;
using System.IO;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public class CodingTaskSandboxWorkspacePolicyTests
    {
        [Fact]
        public void GetDefaultSandboxWorkspaceRoot_UsesDeterministicSubfolder()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "parux-sandbox-test-" + Guid.NewGuid().ToString("N"));
            string root = CodingTaskSandboxWorkspacePolicy.GetDefaultSandboxWorkspaceRoot(tempBase);

            Assert.Equal(Path.Combine(tempBase, "sandbox-workspace"), root);
        }

        [Fact]
        public void IsDefaultSandboxWorkspace_DetectsDefaultSandboxWithTrailingSeparatorsAndCase()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "parux-sandbox-test-" + Guid.NewGuid().ToString("N"));
            string defaultRoot = CodingTaskSandboxWorkspacePolicy.GetDefaultSandboxWorkspaceRoot(tempBase);

            Assert.True(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(defaultRoot, tempBase));
            Assert.True(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(defaultRoot + Path.DirectorySeparatorChar, tempBase));
            Assert.True(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(defaultRoot.ToLowerInvariant(), tempBase));
            Assert.True(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(defaultRoot.ToUpperInvariant(), tempBase));

            Assert.False(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(null, tempBase));
            Assert.False(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(string.Empty, tempBase));
            Assert.False(CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(Path.GetTempPath(), tempBase));
        }

        [Fact]
        public void GetWorkspaceDisplayName_ReturnsExpectedLabels()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "parux-sandbox-test-" + Guid.NewGuid().ToString("N"));
            string defaultRoot = CodingTaskSandboxWorkspacePolicy.GetDefaultSandboxWorkspaceRoot(tempBase);

            Assert.Equal("Autonomous Coding Agent", CodingTaskSandboxWorkspacePolicy.GetWorkspaceDisplayName(null, tempBase));
            Assert.Equal("Autonomous Coding Agent", CodingTaskSandboxWorkspacePolicy.GetWorkspaceDisplayName("", tempBase));
            Assert.Equal("Sandbox Workspace", CodingTaskSandboxWorkspacePolicy.GetWorkspaceDisplayName(defaultRoot, tempBase));
            Assert.Equal("MyCoolRepo", CodingTaskSandboxWorkspacePolicy.GetWorkspaceDisplayName(@"C:\Projects\MyCoolRepo", tempBase));
            Assert.Equal("MyCoolRepo", CodingTaskSandboxWorkspacePolicy.GetWorkspaceDisplayName(@"C:\Projects\MyCoolRepo\", tempBase));
        }

        [Fact]
        public void ResolveEffectiveCodingRoot_PrioritizesExplicitThenProjectThenConfiguredThenSandbox()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "parux-sandbox-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempBase);
            try
            {
                string explicitDir = Path.Combine(tempBase, "explicit-repo");
                string projectDir = Path.Combine(tempBase, "project-repo");
                string configuredDir = Path.Combine(tempBase, "configured-repo");
                Directory.CreateDirectory(explicitDir);
                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(configuredDir);

                // 1. Explicit path wins
                string r1 = CodingTaskSandboxWorkspacePolicy.ResolveEffectiveCodingRoot(
                    explicitDir, projectDir, configuredDir, stateDirectory: tempBase);
                Assert.Equal(explicitDir, r1);

                // 2. Project workspace wins if no explicit
                string r2 = CodingTaskSandboxWorkspacePolicy.ResolveEffectiveCodingRoot(
                    null, projectDir, configuredDir, stateDirectory: tempBase);
                Assert.Equal(projectDir, r2);

                // 3. Configured root wins if no explicit or project
                string r3 = CodingTaskSandboxWorkspacePolicy.ResolveEffectiveCodingRoot(
                    null, null, configuredDir, stateDirectory: tempBase);
                Assert.Equal(configuredDir, r3);

                // 4. Default sandbox workspace used if nothing configured (First coding-agent golden path)
                string r4 = CodingTaskSandboxWorkspacePolicy.ResolveEffectiveCodingRoot(
                    null, null, null, fallbackToDefaultSandbox: true, stateDirectory: tempBase);
                string expectedSandbox = CodingTaskSandboxWorkspacePolicy.GetDefaultSandboxWorkspaceRoot(tempBase);
                Assert.Equal(expectedSandbox, r4);
                Assert.True(Directory.Exists(expectedSandbox));

                // 5. Empty returned if fallback disabled
                string r5 = CodingTaskSandboxWorkspacePolicy.ResolveEffectiveCodingRoot(
                    null, null, null, fallbackToDefaultSandbox: false, stateDirectory: tempBase);
                Assert.Equal(string.Empty, r5);
            }
            finally
            {
                try { Directory.Delete(tempBase, true); } catch { }
            }
        }

        [Fact]
        public void BindSandboxTaskToRepository_UpdatesMessageAndReturnsSuccess()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parux-bind-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var msg = new ChatMessage
                {
                    Role = "assistant",
                    ActionKind = "code-handoff",
                    CodeWorkspacePath = @"C:\PaRuX\Sandbox"
                };

                CodingWorkspaceBindingResult result = CodingTaskSandboxWorkspacePolicy.BindSandboxTaskToRepository(msg, tempDir);

                Assert.True(result.Success);
                Assert.Equal(Path.GetFullPath(tempDir), msg.CodeWorkspacePath);
                Assert.Contains("Task bound to repository", result.Message);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void BindSandboxTaskToRepository_RefusesNonExistentTargetDirectory()
        {
            var msg = new ChatMessage
            {
                Role = "assistant",
                ActionKind = "code-handoff",
                CodeWorkspacePath = @"C:\PaRuX\Sandbox"
            };

            CodingWorkspaceBindingResult result = CodingTaskSandboxWorkspacePolicy.BindSandboxTaskToRepository(
                msg, @"C:\NonExistent\Folder\That\Does\Not\Exist\123456");

            Assert.False(result.Success);
            Assert.Contains("does not exist", result.Message);
            Assert.Equal(@"C:\PaRuX\Sandbox", msg.CodeWorkspacePath);
        }

        [Fact]
        public void TryBindConversationSandboxTasks_BindsAllSandboxMessages()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "parux-conv-bind-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempBase);
            try
            {
                string targetRepo = Path.Combine(tempBase, "RealTargetRepo");
                Directory.CreateDirectory(targetRepo);

                string sandboxRoot = CodingTaskSandboxWorkspacePolicy.GetDefaultSandboxWorkspaceRoot();

                var conv = new Conversation();
                conv.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
                conv.Messages.Add(new ChatMessage { Role = "assistant", ActionKind = "code-handoff", CodeWorkspacePath = sandboxRoot });
                conv.Messages.Add(new ChatMessage { Role = "assistant", ActionKind = "code-handoff", CodeWorkspacePath = "" });
                conv.Messages.Add(new ChatMessage { Role = "assistant", ActionKind = "code-handoff", CodeWorkspacePath = @"C:\Other\ExplicitRepo" });

                int bound = CodingTaskSandboxWorkspacePolicy.TryBindConversationSandboxTasks(conv, targetRepo);

                Assert.Equal(2, bound);
                Assert.Equal(Path.GetFullPath(targetRepo), conv.Messages[1].CodeWorkspacePath);
                Assert.Equal(Path.GetFullPath(targetRepo), conv.Messages[2].CodeWorkspacePath);
                Assert.Equal(@"C:\Other\ExplicitRepo", conv.Messages[3].CodeWorkspacePath);
            }
            finally
            {
                try { Directory.Delete(tempBase, true); } catch { }
            }
        }
    }
}
