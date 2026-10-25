using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class ComponentLifecycleRuntimeHealthTests
    {
        private static ComponentStatus Llama(
            bool detected,
            string health,
            string validation,
            ComponentOwnership ownership = ComponentOwnership.PARUX_MANAGED,
            bool skipped = false,
            string[]? actions = null)
        {
            return new ComponentStatus
            {
                Id = "llama-server-vulkan",
                DisplayName = "llama.cpp (Vulkan)",
                Ownership = ownership,
                Detected = detected,
                Skipped = skipped,
                Health = health,
                ValidationStatus = validation,
                Version = detected ? "b10549" : null,
                Path = detected ? @"C:\PaRuX\runtimes\llama-server.exe" : null,
                Actions = actions ?? Array.Empty<string>()
            };
        }

        [Fact]
        public void Starting_engine_reports_starting_with_loading_next_action()
        {
            var components = new List<ComponentStatus> { Llama(true, "starting", "unverified") };
            var state = new LocalAiState { ServerProcessPresent = true, Healthy = false };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components, state);

            Assert.False(info.IsHealthy);
            Assert.Equal("Starting", info.HealthState);
            Assert.Equal("Loading local engine…", info.NextAction);
            Assert.Equal("Starting · Loading local engine…", info.BadgeText);
        }

        [Fact]
        public void Paused_for_gaming_reports_needs_attention_with_plain_next_action()
        {
            var components = new List<ComponentStatus> { Llama(true, "healthy", "validated") };
            var state = new LocalAiState { GameActive = true, Healthy = true };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components, state);

            Assert.False(info.IsHealthy);
            Assert.Equal("Needs attention · Paused for gaming", info.BadgeText);
        }

        [Fact]
        public void Local_engine_healthy_reports_ready_even_when_the_inventory_is_unverified()
        {
            var components = new List<ComponentStatus> { Llama(true, "unverified", "unverified") };
            var state = new LocalAiState { Healthy = true };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components, state);

            Assert.True(info.IsHealthy);
            Assert.Equal("Ready · Start chatting in Chat", info.BadgeText);
        }

        [Fact]
        public void Failed_runtime_reports_needs_attention_with_status_next_action()
        {
            var components = new List<ComponentStatus> { Llama(true, "failed", "unhealthy") };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);

            Assert.False(info.IsHealthy);
            Assert.Equal("Needs attention", info.HealthState);
            Assert.Equal("Needs attention · Check runtime status in Settings", info.BadgeText);
        }

        [Fact]
        public void Skipped_runtime_reports_needs_attention_with_reenable_next_action()
        {
            var components = new List<ComponentStatus> { Llama(false, "missing", "missing", skipped: true) };

            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);

            Assert.True(info.IsSkipped);
            Assert.Equal("Needs attention · Re-enable runtime in Settings", info.BadgeText);
        }

        [Fact]
        public void Null_inventory_never_suggests_installing_a_runtime()
        {
            SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(null);

            Assert.DoesNotContain("Install", info.NextAction, StringComparison.Ordinal);
            Assert.Equal("Needs attention · Check runtime status in Settings", info.BadgeText);
        }

        [Fact]
        public void User_managed_installation_is_never_offered_install_or_skip()
        {
            ComponentStatus located = Llama(
                true,
                "healthy",
                "validated",
                ComponentOwnership.USER_MANAGED,
                actions: new[] { "install", "skip", "locate", "relocate", "remove" });

            ComponentDisplayItem item = ComponentLifecyclePresenter.FormatComponent(located);

            Assert.False(item.CanInstall);
            Assert.False(item.CanSkip);
            Assert.True(item.CanLocate);
            Assert.True(item.CanRemove);
            Assert.Equal("User-managed", item.OwnershipLabel);
        }

        [Fact]
        public void IsActionPermitted_refuses_install_and_skip_for_user_managed_only()
        {
            ComponentStatus userManaged = Llama(
                true,
                "healthy",
                "validated",
                ComponentOwnership.USER_MANAGED,
                actions: new[] { "install", "skip", "locate", "remove" });

            Assert.False(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Install));
            Assert.False(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Skip));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Locate));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Remove));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(userManaged, ComponentSetupAction.Detect));

            ComponentStatus paruxManaged = Llama(false, "missing", "missing", actions: new[] { "install", "skip" });
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(paruxManaged, ComponentSetupAction.Install));
            Assert.True(ComponentLifecyclePresenter.IsActionPermitted(paruxManaged, ComponentSetupAction.Skip));
        }
    }
}
