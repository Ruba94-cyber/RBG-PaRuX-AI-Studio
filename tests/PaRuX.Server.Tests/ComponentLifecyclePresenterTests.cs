using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class ComponentLifecyclePresenterTests
    {
        [Fact]
        public void FormatOwnership_Maps_All_Ownership_Types()
        {
            Assert.Equal("PaRuX-managed", ComponentLifecyclePresenter.FormatOwnership(ComponentOwnership.PARUX_MANAGED));
            Assert.Equal("User-managed", ComponentLifecyclePresenter.FormatOwnership(ComponentOwnership.USER_MANAGED));
            Assert.Equal("System-detected", ComponentLifecyclePresenter.FormatOwnership(ComponentOwnership.SYSTEM_DETECTED));
            Assert.Equal("Unknown", ComponentLifecyclePresenter.FormatOwnership((ComponentOwnership)999));
        }

        [Theory]
        [InlineData("llama-server-cuda", "text-cuda")]
        [InlineData("llama-server-vulkan", "text-vulkan")]
        [InlineData("comfyui", "media-comfyui")]
        [InlineData("git", null)]
        [InlineData("python", null)]
        [InlineData("unknown-component", null)]
        public void BackendForComponent_Maps_Expected_Components(string id, string? expectedBackend)
        {
            Assert.Equal(expectedBackend, ComponentLifecyclePresenter.BackendForComponent(id));
        }

        [Fact]
        public void FormatComponent_Maps_PaRuX_Managed_Healthy_State()
        {
            var component = new ComponentStatus
            {
                Id = "llama-server-vulkan",
                DisplayName = "llama.cpp (Vulkan)",
                Ownership = ComponentOwnership.PARUX_MANAGED,
                Detected = true,
                Health = "healthy",
                ValidationStatus = "validated",
                ValidationMessage = "Local engine responded to health probe.",
                Version = "b10549",
                Path = @"C:\PaRuX\runtimes\llama-server.exe",
                Actions = new[] { "install", "rollback" },
                Skipped = false
            };

            ComponentDisplayItem item = ComponentLifecyclePresenter.FormatComponent(component);

            Assert.Equal("llama-server-vulkan", item.Id);
            Assert.Equal("llama.cpp (Vulkan)", item.DisplayName);
            Assert.Equal(ComponentOwnership.PARUX_MANAGED, item.Ownership);
            Assert.Equal("PaRuX-managed", item.OwnershipLabel);
            Assert.True(item.IsDetected);
            Assert.True(item.IsInstalled);
            Assert.False(item.IsSkipped);
            Assert.Equal("b10549", item.Version);
            Assert.Equal(@"C:\PaRuX\runtimes\llama-server.exe", item.Path);
            Assert.Equal("healthy · b10549", item.StateSummary);
            Assert.True(item.CanInstall);
            Assert.Equal("Install / update…", item.InstallActionLabel);
            Assert.False(item.CanLocate);
            Assert.False(item.CanRemove);
            Assert.False(item.CanSkip);
            Assert.False(item.CanUnskip);
        }

        [Fact]
        public void FormatComponent_Maps_User_Managed_Located_State()
        {
            var component = new ComponentStatus
            {
                Id = "llama-server-cuda",
                DisplayName = "llama.cpp (CUDA)",
                Ownership = ComponentOwnership.USER_MANAGED,
                Detected = true,
                Health = "unverified",
                ValidationStatus = "unverified",
                ValidationMessage = "Path exists but runtime identity could not be verified.",
                Version = "custom-build",
                Path = @"D:\tools\llama\llama-server.exe",
                Actions = new[] { "relocate", "remove" },
                Skipped = false
            };

            ComponentDisplayItem item = ComponentLifecyclePresenter.FormatComponent(component);

            Assert.Equal("User-managed", item.OwnershipLabel);
            Assert.True(item.IsDetected);
            Assert.True(item.IsInstalled);
            Assert.False(item.CanInstall);
            Assert.True(item.CanLocate);
            Assert.True(item.CanRelocate);
            Assert.Equal("Change location…", item.LocateActionLabel);
            Assert.True(item.CanRemove);
            Assert.Equal("Forget location", item.RemoveActionLabel);
            Assert.Equal("unverified · custom-build", item.StateSummary);
        }

        [Fact]
        public void FormatComponent_Maps_System_Detected_Missing_State()
        {
            var component = new ComponentStatus
            {
                Id = "git",
                DisplayName = "Git",
                Ownership = ComponentOwnership.SYSTEM_DETECTED,
                Detected = false,
                Health = "missing",
                ValidationStatus = "missing",
                Actions = new[] { "locate", "skip" },
                Skipped = false
            };

            ComponentDisplayItem item = ComponentLifecyclePresenter.FormatComponent(component);

            Assert.Equal("System-detected", item.OwnershipLabel);
            Assert.False(item.IsDetected);
            Assert.False(item.IsInstalled);
            Assert.Equal("not configured (missing)", item.StateSummary);
            Assert.False(item.CanInstall);
            Assert.True(item.CanLocate);
            Assert.False(item.CanRelocate);
            Assert.Equal("Locate existing…", item.LocateActionLabel);
            Assert.True(item.CanSkip);
            Assert.Equal("Skip for now", item.SkipActionLabel);
        }

        [Fact]
        public void FormatComponent_Maps_Skipped_State()
        {
            var component = new ComponentStatus
            {
                Id = "comfyui",
                DisplayName = "ComfyUI",
                Ownership = ComponentOwnership.PARUX_MANAGED,
                Detected = false,
                Health = "missing",
                Skipped = true,
                Actions = new[] { "install", "locate", "unskip" }
            };

            ComponentDisplayItem item = ComponentLifecyclePresenter.FormatComponent(component);

            Assert.True(item.IsSkipped);
            Assert.Equal("skipped for now", item.StateSummary);
            Assert.True(item.CanUnskip);
            Assert.Equal("Configure now", item.UnskipActionLabel);
            Assert.True(item.CanInstall);
            Assert.Equal("Install with PaRuX…", item.InstallActionLabel);
        }

        [Fact]
        public void FormatSimpleModeRuntimeHealth_When_Null_Or_Empty_Returns_Safe_Defaults()
        {
            SimpleModeRuntimeHealthInfo nullInfo = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(null);
            Assert.False(nullInfo.HasAnyComponents);
            Assert.False(nullInfo.IsInstalled);
            Assert.False(nullInfo.IsHealthy);
            Assert.Equal("Needs attention · Check runtime status in Settings", nullInfo.BadgeText);
            Assert.Contains("Needs attention", nullInfo.AccessibleName);

            SimpleModeRuntimeHealthInfo emptyInfo = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(Array.Empty<ComponentStatus>());
            Assert.False(emptyInfo.HasAnyComponents);
            Assert.Equal("Needs attention · Install or locate a runtime in Settings", emptyInfo.BadgeText);
        }

        [Fact]
        public void FormatSimpleModeRuntimeHealth_With_Healthy_Llama_Displays_Complete_Health_And_Ownership()
        {
            var components = new List<ComponentStatus>
            {
                new ComponentStatus
                {
                    Id = "llama-server-vulkan",
                    DisplayName = "llama.cpp (Vulkan)",
                    Ownership = ComponentOwnership.PARUX_MANAGED,
                    Detected = true,
                    Health = "healthy",
                    ValidationStatus = "validated",
                    ValidationMessage = "Engine responded to health probe.",
                    Version = "b10549",
                    Path = @"C:\PaRuX\runtimes\llama-server.exe",
                    Actions = new[] { "install", "rollback" }
                },
                new ComponentStatus
                {
                    Id = "git",
                    DisplayName = "Git",
                    Ownership = ComponentOwnership.SYSTEM_DETECTED,
                    Detected = true,
                    Health = "healthy",
                    ValidationStatus = "validated",
                    Version = "2.44.0"
                }
            };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);

            Assert.True(info.HasAnyComponents);
            Assert.True(info.IsInstalled);
            Assert.True(info.IsHealthy);
            Assert.False(info.IsSkipped);
            Assert.Equal("llama-server-vulkan", info.PrimaryRuntimeId);
            Assert.Equal("b10549", info.Version);
            Assert.Equal(@"C:\PaRuX\runtimes\llama-server.exe", info.Path);
            Assert.Equal("PaRuX-managed", info.OwnershipLabel);

            // Badge text must include runtime name, version, ownership and health
            Assert.Equal("Ready · Start chatting in Chat", info.BadgeText);

            // Accessible name must explicitly contain installed, version, path, owner and health
            Assert.Contains("installed: yes", info.AccessibleName);
            Assert.Contains("b10549", info.AccessibleName);
            Assert.Contains(@"C:\PaRuX\runtimes\llama-server.exe", info.AccessibleName);
            Assert.Contains("PaRuX-managed", info.AccessibleName);
            Assert.Contains("healthy", info.AccessibleName);

            // Tooltip must contain multi-line breakdown
            Assert.Contains("• Installed: Yes", info.TooltipText);
            Assert.Contains("• Version: b10549", info.TooltipText);
            Assert.Contains("• Path: C:\\PaRuX\\runtimes\\llama-server.exe", info.TooltipText);
            Assert.Contains("• Owner: PaRuX-managed", info.TooltipText);
            Assert.Contains("• Health: healthy", info.TooltipText);
            Assert.Contains("Engine responded to health probe.", info.TooltipText);
            Assert.Contains("Git", info.TooltipText);
        }

        [Fact]
        public void FormatSimpleModeRuntimeHealth_With_Missing_Runtime_Shows_Missing_And_Accessible_Details()
        {
            var components = new List<ComponentStatus>
            {
                new ComponentStatus
                {
                    Id = "llama-server-vulkan",
                    DisplayName = "llama.cpp (Vulkan)",
                    Ownership = ComponentOwnership.PARUX_MANAGED,
                    Detected = false,
                    Health = "missing",
                    ValidationStatus = "missing",
                    Actions = new[] { "install", "locate", "skip" }
                }
            };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);

            Assert.True(info.HasAnyComponents);
            Assert.False(info.IsInstalled);
            Assert.False(info.IsHealthy);
            Assert.False(info.IsSkipped);
            Assert.Equal("Needs attention · Install or locate a runtime in Settings", info.BadgeText);
            Assert.Contains("installed: no", info.AccessibleName);
            Assert.Contains("owner: PaRuX-managed", info.AccessibleName);
            Assert.Contains("• Installed: No", info.TooltipText);
        }

        [Fact]
        public void FormatSimpleModeRuntimeHealth_With_Skipped_Runtime_Shows_Skipped()
        {
            var components = new List<ComponentStatus>
            {
                new ComponentStatus
                {
                    Id = "llama-server-cuda",
                    DisplayName = "llama.cpp (CUDA)",
                    Ownership = ComponentOwnership.PARUX_MANAGED,
                    Detected = false,
                    Health = "missing",
                    Skipped = true,
                    Actions = new[] { "unskip", "install" }
                }
            };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);

            Assert.True(info.IsSkipped);
            Assert.Equal("Needs attention · Re-enable runtime in Settings", info.BadgeText);
            Assert.Contains("health: skipped", info.AccessibleName);
            Assert.Contains("• Health: Skipped for now", info.TooltipText);
        }

        [Fact]
        public void FormatSimpleModeRuntimeHealth_With_User_Managed_Shows_User_Managed_Ownership_And_Path()
        {
            var components = new List<ComponentStatus>
            {
                new ComponentStatus
                {
                    Id = "llama-server-vulkan",
                    DisplayName = "llama.cpp (Vulkan)",
                    Ownership = ComponentOwnership.USER_MANAGED,
                    Detected = true,
                    Health = "unverified",
                    ValidationStatus = "unverified",
                    ValidationMessage = "Registered by user; executable not signed.",
                    Version = "local-dev",
                    Path = @"E:\runtimes\llama.exe",
                    Actions = new[] { "relocate", "remove" }
                }
            };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);

            Assert.True(info.IsInstalled);
            Assert.False(info.IsHealthy);
            Assert.Equal("User-managed", info.OwnershipLabel);
            Assert.Equal("Needs attention · Check runtime status in Settings", info.BadgeText);
            Assert.Contains("owner: User-managed", info.AccessibleName);
            Assert.Contains(@"E:\runtimes\llama.exe", info.AccessibleName);
            Assert.Contains("• Owner: User-managed", info.TooltipText);
            Assert.Contains(@"• Path: E:\runtimes\llama.exe", info.TooltipText);
        }

        [Fact]
        public void SelectRecommendedBuild_Prefers_Recommended_Build()
        {
            var builds = new List<RuntimeBuildOption>
            {
                new RuntimeBuildOption { Tag = "b1000", Recommended = false },
                new RuntimeBuildOption { Tag = "b10549", Recommended = true },
                new RuntimeBuildOption { Tag = "b10600", Recommended = false }
            };

            RuntimeBuildOption? selected = ComponentLifecyclePresenter.SelectRecommendedBuild(builds);
            Assert.NotNull(selected);
            Assert.Equal("b10549", selected.Tag);

            var noRecommended = new List<RuntimeBuildOption>
            {
                new RuntimeBuildOption { Tag = "b1000", Recommended = false },
                new RuntimeBuildOption { Tag = "b10600", Recommended = false }
            };
            Assert.Equal("b1000", ComponentLifecyclePresenter.SelectRecommendedBuild(noRecommended)?.Tag);
            Assert.Null(ComponentLifecyclePresenter.SelectRecommendedBuild(null));
            Assert.Null(ComponentLifecyclePresenter.SelectRecommendedBuild(Array.Empty<RuntimeBuildOption>()));
        }

        [Fact]
        public void FormatInstallProgress_And_Completion_Produce_Clean_Descriptions()
        {
            string progress = ComponentLifecyclePresenter.FormatInstallProgress(
                "llama.cpp", "downloading", 52428800, 104857600);
            Assert.Equal("llama.cpp: downloading · 50.0 MB / 100.0 MB", progress);

            var completed = new RuntimeInstallStatus
            {
                Stage = "completed",
                Tag = "b10549",
                ActiveTag = "b10549",
                PerformanceDecision = "accepted"
            };
            string success = ComponentLifecyclePresenter.FormatInstallCompletion("llama.cpp", completed);
            Assert.Contains("candidate installed: b10549", success);
            Assert.Contains("performance gate: accepted", success);

            var failed = new RuntimeInstallStatus
            {
                Stage = "failed",
                Tag = "b10549",
                Error = "SHA256 checksum mismatch"
            };
            string failure = ComponentLifecyclePresenter.FormatInstallCompletion("llama.cpp", failed);
            Assert.Equal("llama.cpp install failed: SHA256 checksum mismatch", failure);

            var cancelled = new RuntimeInstallStatus
            {
                Stage = "cancelled",
                Tag = "b10549"
            };
            string cancelMsg = ComponentLifecyclePresenter.FormatInstallCompletion("llama.cpp", cancelled);
            Assert.Equal("llama.cpp install was cancelled.", cancelMsg);
        }

        [Fact]
        public void IsActionPermitted_Enforces_Ownership_Invariants()
        {
            var userManaged = new ComponentStatus
            {
                Id = "llama-server-vulkan",
                Ownership = ComponentOwnership.USER_MANAGED,
                Actions = new[] { "install", "relocate", "remove" } // even if install was somehow reported
            };

            // Rule: PaRuX NEVER mutates user-managed software
            Assert.False(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Install));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Locate));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Remove));
            Assert.False(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Skip));

            var systemDetected = new ComponentStatus
            {
                Id = "git",
                Ownership = ComponentOwnership.SYSTEM_DETECTED,
                Actions = new[] { "locate", "skip" }
            };
            Assert.False(ComponentLifecyclePresenter.IsActionPermitted(systemDetected, ComponentSetupAction.Install));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(systemDetected, ComponentSetupAction.Locate));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(systemDetected, ComponentSetupAction.Skip));

            var paruxManaged = new ComponentStatus
            {
                Id = "llama-server-vulkan",
                Ownership = ComponentOwnership.PARUX_MANAGED,
                Actions = new[] { "install", "rollback" }
            };
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(paruxManaged, ComponentSetupAction.Install));
            Assert.False(ComponentLifecyclePresenter.IsActionPermitted(paruxManaged, ComponentSetupAction.Locate));
        }
    }
}
