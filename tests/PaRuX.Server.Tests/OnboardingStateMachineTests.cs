using System;
using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class OnboardingStateMachineTests
    {
        [Fact]
        public void InitialState_StartsAtRuntimeLocation_NotReadyAndNotCompleted()
        {
            var sm = new OnboardingStateMachine();

            Assert.Equal(OnboardingStep.RuntimeLocation, sm.CurrentStep);
            Assert.False(sm.IsInModelHubHandoff);
            Assert.False(sm.IsCompleted);
            Assert.False(sm.CanComplete());
            Assert.False(sm.CurrentReadiness.IsReady);
            Assert.NotNull(sm.CurrentReadiness.FailureReason);
            Assert.NotNull(sm.CurrentReadiness.NextAction);
        }

        [Fact]
        public void ConfigureRoot_SetsRootAndAdvancesToComponents()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");

            Assert.Equal(@"D:\fixtures\LocalAI", sm.LocalAiRootPath);
            Assert.Equal(OnboardingStep.Components, sm.CurrentStep);
        }

        [Fact]
        public void ConfigureRoot_ThrowsOnEmptyOrNull()
        {
            var sm = new OnboardingStateMachine();
            Assert.Throws<ArgumentException>(() => sm.ConfigureRoot("   "));
        }

        [Fact]
        public void SetComponents_StoresComponentsAndAdvancesToFirstModel()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");

            var components = new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", DisplayName = "llama.cpp (CUDA)", Detected = true, Health = "healthy" },
                new() { Id = "comfyui", DisplayName = "ComfyUI", Detected = false, Health = "not configured" }
            };

            sm.SetComponents(components);

            Assert.Equal(2, sm.Components.Count);
            Assert.Equal(OnboardingStep.FirstModel, sm.CurrentStep);
        }

        [Fact]
        public void ApplyComponentAction_Locate_UpdatesOwnershipAndDetected()
        {
            var sm = new OnboardingStateMachine();
            sm.ApplyComponentAction("llama-server-vulkan", ComponentSetupAction.Locate, @"D:\fixtures\Tools\llama-server.exe");

            Assert.Single(sm.Components);
            ComponentState comp = sm.Components[0];
            Assert.True(comp.Detected);
            Assert.Equal("USER_MANAGED", comp.Ownership);
            Assert.Equal(@"D:\fixtures\Tools\llama-server.exe", comp.Path);
            Assert.False(comp.Skipped);
        }

        [Fact]
        public void ApplyComponentAction_Install_SetsParuxManaged()
        {
            var sm = new OnboardingStateMachine();
            sm.ApplyComponentAction("llama-server-cuda", ComponentSetupAction.Install);

            Assert.Single(sm.Components);
            ComponentState comp = sm.Components[0];
            Assert.True(comp.Detected);
            Assert.Equal("PARUX_MANAGED", comp.Ownership);
            Assert.False(comp.Skipped);
        }

        [Fact]
        public void ApplyComponentAction_SkipAndUnskip_TransitionsCorrectly()
        {
            var sm = new OnboardingStateMachine();
            sm.ApplyComponentAction("comfyui", ComponentSetupAction.Skip);

            Assert.Single(sm.Components);
            Assert.True(sm.Components[0].Skipped);

            sm.ApplyComponentAction("comfyui", ComponentSetupAction.Unskip);
            Assert.False(sm.Components[0].Skipped);
        }

        [Fact]
        public void HandoffToModelHub_DoesNotCompleteOnboarding_SetsHandoffState()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.HandoffToModelHub("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF");

            Assert.True(sm.IsInModelHubHandoff);
            Assert.Equal(OnboardingStep.ModelHubHandoff, sm.CurrentStep);
            Assert.Equal("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF", sm.SelectedModelRepoId);
            Assert.False(sm.IsCompleted);
            Assert.False(sm.CanComplete());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t ")]
        public void TryHandoffToModelHub_WhenRepoIdIsMissing_ReturnsFalseWithoutChangingState(string? repoId)
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.SetComponents(Array.Empty<ComponentState>());

            Assert.False(sm.TryHandoffToModelHub(repoId));

            Assert.Equal(OnboardingStep.FirstModel, sm.CurrentStep);
            Assert.Null(sm.SelectedModelRepoId);
            Assert.False(sm.IsInModelHubHandoff);
        }

        [Fact]
        public void ReturnFromModelHub_ClearsHandoffState_TransitionsToReadyCheck()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.HandoffToModelHub("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF");

            sm.ReturnFromModelHub();

            Assert.False(sm.IsInModelHubHandoff);
            Assert.Equal(OnboardingStep.ReadyCheck, sm.CurrentStep);
            Assert.False(sm.IsCompleted);
        }

        [Fact]
        public void EvaluateReadiness_WhenRuntimeMissing_ReturnsNotReadyWithAction()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: false,
                runtimeSummary: "No process running",
                modelAvailable: true,
                modelSummary: "TinyLlama",
                probeSucceeded: false,
                probeSummary: "Connection refused");

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.False(result.IsReady);
            Assert.Equal(OnboardingStep.Components, result.FailedStep);
            Assert.Contains("No text runtime", result.FailureReason);
            Assert.Contains("Components", result.NextAction);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void EvaluateReadiness_WhenRuntimeSkippedAndNoExternalProvider_ReturnsNotReady()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Skipped = true, Detected = false },
                new() { Id = "llama-server-vulkan", Skipped = true, Detected = false }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: false,
                runtimeSummary: null,
                modelAvailable: true,
                modelSummary: "TestModel",
                probeSucceeded: false,
                probeSummary: null);

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.False(result.IsReady);
            Assert.Contains("skipped", result.FailureReason);
        }

        [Fact]
        public void EvaluateReadiness_WhenModelMissing_ReturnsNotReadyWithModelHubAction()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "CUDA runtime ready",
                modelAvailable: false,
                modelSummary: null,
                probeSucceeded: false,
                probeSummary: "No model loaded");

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.False(result.IsReady);
            Assert.Equal(OnboardingStep.FirstModel, result.FailedStep);
            Assert.Contains("No chat model", result.FailureReason);
            Assert.Contains("Model Hub", result.NextAction);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void EvaluateReadiness_WhenProbeFails_ReturnsNotReadyWithRetryAction()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "Runtime healthy",
                modelAvailable: true,
                modelSummary: "TinyLlama",
                probeSucceeded: false,
                probeSummary: null,
                errorMessage: "Timeout waiting for localhost:8080");

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.False(result.IsReady);
            Assert.Equal(OnboardingStep.ReadyCheck, result.FailedStep);
            Assert.Contains("probe failed", result.FailureReason);
            Assert.Contains("Timeout", result.FailureReason);
            Assert.Contains("retry", result.NextAction, StringComparison.OrdinalIgnoreCase);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void EvaluateReadiness_ProbeSuccessAlone_IsNotVerifiedChatAnswer()
        {
            // A health probe is not a chat answer. With no explicit verified answer, readiness must not pass
            // and completion stays gated (Create no longer treats probe success as verification).
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var unverified = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "llama.cpp CUDA healthy",
                modelAvailable: true,
                modelSummary: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
                probeSucceeded: true,
                probeSummary: "Round-trip ping responded in 12ms");

            Assert.False(unverified.ChatAnswerVerified);
            ReadinessAssessment result = sm.EvaluateReadiness(unverified);

            Assert.False(result.IsReady);
            Assert.Contains("verified chat answer", result.FailureReason);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void EvaluateReadiness_WhenRuntimeHealthyAndModelAvailableAndProbeSucceeded_ReturnsReady()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "llama.cpp CUDA healthy",
                modelAvailable: true,
                modelSummary: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
                probeSucceeded: true,
                probeSummary: "Round-trip ping responded in 12ms",
                chatAnswerVerified: true);

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.True(result.IsReady);
            Assert.Null(result.FailureReason);
            Assert.Contains("Chat is fully configured", result.Summary);
            Assert.Contains("Get Started", result.NextAction);
            Assert.True(sm.CanComplete());

            bool completed = sm.Complete();
            Assert.True(completed);
            Assert.True(sm.IsCompleted);
            Assert.Equal(OnboardingStep.Completed, sm.CurrentStep);
        }

        [Fact]
        public void Complete_FailsWhenNotReady()
        {
            var sm = new OnboardingStateMachine();
            Assert.False(sm.CanComplete());
            Assert.False(sm.Complete());
            Assert.False(sm.IsCompleted);
        }

        [Fact]
        public void EvaluateReadiness_WhenModelOnDiskButChatAnswerNotVerified_ReturnsNotReadyAndCannotComplete()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            // Model file is on disk and probe responded, but chat turn has NOT yet produced a verified answer
            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "llama.cpp CUDA healthy",
                modelAvailable: true,
                modelSummary: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
                probeSucceeded: true,
                probeSummary: "Ping succeeded",
                errorMessage: null,
                chatAnswerVerified: false);

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.False(result.IsReady);
            Assert.Equal(OnboardingStep.ReadyCheck, result.FailedStep);
            Assert.Contains("verified chat answer", result.FailureReason);
            Assert.False(sm.CanComplete());
            Assert.False(sm.Complete());
            Assert.False(sm.IsCompleted);
        }

        [Fact]
        public void HandleDownloadState_Completed_TransitionsFromModelHubToReadyCheck()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.HandoffToModelHub("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF");

            Assert.True(sm.IsInModelHubHandoff);
            Assert.Equal(OnboardingStep.ModelHubHandoff, sm.CurrentStep);

            // Download completion triggers return to setup
            OnboardingReturnAction action = sm.HandleDownloadState(OnboardingDownloadState.Completed);

            Assert.Equal(OnboardingReturnAction.ReturnToSetup, action);
            Assert.False(sm.IsInModelHubHandoff);
            Assert.Equal(OnboardingStep.ReadyCheck, sm.CurrentStep);
        }

        [Fact]
        public void HandleDownloadState_Failed_ReturnsShowRetryAndKeepsHandoff()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.HandoffToModelHub("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF");

            OnboardingReturnAction action = sm.HandleDownloadState(OnboardingDownloadState.Failed);

            Assert.Equal(OnboardingReturnAction.ShowRetry, action);
            Assert.True(sm.IsInModelHubHandoff);
            Assert.Equal(OnboardingStep.ModelHubHandoff, sm.CurrentStep);
        }

        [Theory]
        [InlineData(OnboardingDownloadState.Downloading)]
        [InlineData(OnboardingDownloadState.Queued)]
        [InlineData(OnboardingDownloadState.Paused)]
        public void HandleDownloadState_InFlightOrPaused_ReturnsStayAndKeepsHandoff(OnboardingDownloadState state)
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.HandoffToModelHub("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF");

            OnboardingReturnAction action = sm.HandleDownloadState(state);

            Assert.Equal(OnboardingReturnAction.Stay, action);
            Assert.True(sm.IsInModelHubHandoff);
        }

        [Fact]
        public void HandleDownloadState_WhenChatAnswerVerified_ReturnsShowSuccess()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.HandoffToModelHub("TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF");

            sm.RecordVerifiedChatAnswer();
            Assert.True(sm.ChatAnswerVerified);

            OnboardingReturnAction action = sm.HandleDownloadState(OnboardingDownloadState.Completed);
            Assert.Equal(OnboardingReturnAction.ShowSuccess, action);
        }

        [Fact]
        public void RecordVerifiedChatAnswer_EnablesReadinessCompletion()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var unverified = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "Runtime healthy",
                modelAvailable: true,
                modelSummary: "Model.gguf",
                probeSucceeded: true,
                probeSummary: "Probe OK",
                errorMessage: null,
                chatAnswerVerified: false);

            sm.EvaluateReadiness(unverified);
            Assert.False(sm.CanComplete());

            sm.RecordVerifiedChatAnswer();
            sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "Runtime healthy",
                modelAvailable: true,
                modelSummary: "Model.gguf",
                probeSucceeded: true,
                probeSummary: "Probe OK",
                errorMessage: null,
                chatAnswerVerified: true));

            Assert.True(sm.CanComplete());
            Assert.True(sm.Complete());
            Assert.True(sm.IsCompleted);
        }

        [Fact]
        public void ForceCompleteWithWarning_WhenReadinessUnproven_RecordsUnprovenItems()
        {
            var sm = new OnboardingStateMachine();
            Assert.False(sm.CanComplete());

            sm.ForceCompleteWithWarning();

            Assert.True(sm.IsCompleted);
            Assert.Equal(OnboardingStep.Completed, sm.CurrentStep);
            Assert.NotEmpty(sm.UnprovenItems);
            Assert.Contains(sm.UnprovenItems, item => item.Contains("text runtime", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sm.UnprovenItems, item => item.Contains("chat model", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sm.UnprovenItems, item => item.Contains("probe", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sm.UnprovenItems, item => item.Contains("verified", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ForceCompleteWithWarning_WithExplicitList_RecordsSuppliedUnprovenItems()
        {
            var sm = new OnboardingStateMachine();
            var customWarnings = new[] { "Local text runtime is missing", "No model downloaded" };

            sm.ForceCompleteWithWarning(customWarnings);

            Assert.True(sm.IsCompleted);
            Assert.Equal(OnboardingStep.Completed, sm.CurrentStep);
            Assert.Equal(2, sm.UnprovenItems.Count);
            Assert.Equal("Local text runtime is missing", sm.UnprovenItems[0]);
            Assert.Equal("No model downloaded", sm.UnprovenItems[1]);
        }

        [Fact]
        public void GetUnprovenItems_WhenModelOnDiskButAnswerUnverified_ReportsOnlyUnverifiedAnswer()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "llama.cpp CUDA healthy",
                modelAvailable: true,
                modelSummary: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
                probeSucceeded: true,
                probeSummary: "Ping succeeded",
                errorMessage: null,
                chatAnswerVerified: false);

            sm.EvaluateReadiness(evidence);
            Assert.False(sm.CanComplete());

            IReadOnlyList<string> unproven = sm.GetUnprovenItems();
            Assert.Single(unproven);
            Assert.Contains("verified", unproven[0], StringComparison.OrdinalIgnoreCase);

            sm.ForceCompleteWithWarning();
            Assert.True(sm.IsCompleted);
            Assert.Single(sm.UnprovenItems);
            Assert.Contains("verified", sm.UnprovenItems[0], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ComponentChange_InvalidatesCachedReadiness_RequiringReEvaluation()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "CUDA healthy",
                modelAvailable: true,
                modelSummary: "Model.gguf",
                probeSucceeded: true,
                probeSummary: "OK",
                errorMessage: null,
                chatAnswerVerified: true);

            sm.EvaluateReadiness(evidence);
            Assert.True(sm.CanComplete());
            Assert.True(sm.CurrentReadiness.IsReady);

            // A component mutation invalidates cached readiness
            sm.ApplyComponentAction("llama-server-cuda", ComponentSetupAction.Skip);
            Assert.False(sm.CurrentReadiness.IsReady);
            Assert.False(sm.CanComplete());
            Assert.False(sm.Complete());
            Assert.False(sm.IsCompleted);

            // Re-evaluating after resolving the component restores CanComplete
            sm.ApplyComponentAction("llama-server-cuda", ComponentSetupAction.Unskip);
            sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "CUDA healthy",
                modelAvailable: true,
                modelSummary: "Model.gguf",
                probeSucceeded: true,
                probeSummary: "OK",
                errorMessage: null,
                chatAnswerVerified: true));
            Assert.True(sm.CanComplete());
            Assert.True(sm.Complete());
            Assert.True(sm.IsCompleted);
            Assert.Empty(sm.UnprovenItems);
        }

        [Fact]
        public void SetComponents_InvalidatesCachedReadiness()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "ready",
                modelAvailable: true,
                modelSummary: "Model.gguf",
                probeSucceeded: true,
                probeSummary: "ok",
                chatAnswerVerified: true));

            Assert.True(sm.CanComplete());

            // Updating component list invalidates cached readiness
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "comfyui", Detected = true, Health = "healthy" }
            });

            Assert.False(sm.CurrentReadiness.IsReady);
            Assert.False(sm.CanComplete());
            Assert.False(sm.Complete());
            Assert.False(sm.IsCompleted);
        }

        [Fact]
        public void Complete_WhenReady_LeavesUnprovenItemsEmpty()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true, Health = "healthy" }
            });

            sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "llama.cpp CUDA healthy",
                modelAvailable: true,
                modelSummary: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
                probeSucceeded: true,
                probeSummary: "OK",
                chatAnswerVerified: true));

            Assert.True(sm.CanComplete());
            bool ok = sm.Complete();

            Assert.True(ok);
            Assert.True(sm.IsCompleted);
            Assert.Equal(OnboardingStep.Completed, sm.CurrentStep);
            Assert.Empty(sm.UnprovenItems);
        }

        [Fact]
        public void RepeatedComponentDiscovery_PreservesActualChatVerification()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.SetComponents(new[] { new ComponentState
            {
                Id = "llama-server-vulkan", DisplayName = "Vulkan", Detected = true,
                Ownership = "PARUX_MANAGED", Health = "healthy"
            } });
            sm.RecordVerifiedChatAnswer();

            sm.SetComponents(new[] { new ComponentState
            {
                Id = "llama-server-vulkan", DisplayName = "Vulkan", Detected = true,
                Ownership = "PARUX_MANAGED", Health = "healthy"
            } });

            Assert.True(sm.ChatAnswerVerified);
            ReadinessAssessment result = sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                true, "Healthy", true, "Model.gguf", true, "Probe completed"));
            Assert.True(result.IsReady);
            Assert.True(sm.CanComplete());
        }

        [Fact]
        public void ComponentChange_InvalidatesPriorChatVerification()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new[] { new ComponentState
            {
                Id = "llama-server-vulkan", Detected = true, Health = "healthy"
            } });
            sm.RecordVerifiedChatAnswer();
            sm.SetComponents(new[] { new ComponentState
            {
                Id = "llama-server-vulkan", Detected = false, Health = "missing"
            } });

            Assert.False(sm.ChatAnswerVerified);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void RootChange_InvalidatesPriorChatVerification()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"D:\fixtures\LocalAI");
            sm.RecordVerifiedChatAnswer();
            sm.ConfigureRoot(@"E:\fixtures\OtherLocalAI");

            Assert.False(sm.ChatAnswerVerified);
        }

        [Fact]
        public void ModelHubReturn_InvalidatesPriorChatVerification()
        {
            var sm = new OnboardingStateMachine();
            sm.RecordVerifiedChatAnswer();
            sm.ReturnFromModelHub();

            Assert.False(sm.ChatAnswerVerified);
            Assert.Equal(OnboardingStep.ReadyCheck, sm.CurrentStep);
        }


        [Fact]
        public void ChangingConfiguredModel_InvalidatesAnActuallyVerifiedChat()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new[] { new ComponentState
            {
                Id = "llama-server-vulkan", Detected = true, Health = "healthy"
            } });
            sm.ObserveTextModelConfiguration(new[] { ("standard", @"D:\models\chat-one.gguf", true, false) });
            sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                true, "Healthy", true, "chat-one.gguf", true, "Healthy"));
            sm.RecordVerifiedChatAnswer();
            sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                true, "Healthy", true, "chat-one.gguf", true, "Healthy"));
            Assert.True(sm.CanComplete());
            int revision = sm.ConfigurationRevision;

            sm.ObserveTextModelConfiguration(new[] { ("standard", @"D:\models\chat-two.gguf", true, true) });

            Assert.Equal(revision + 1, sm.ConfigurationRevision);
            Assert.False(sm.ChatAnswerVerified);
            Assert.Null(sm.LastEvidence);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void IdenticalModelInventory_InNewOrderOrPathCasing_KeepsValidProof()
        {
            var sm = new OnboardingStateMachine();
            sm.ObserveTextModelConfiguration(new[]
            {
                ("standard", @"D:\Models\Chat.gguf", true, false),
                ("coding", @"D:\Models\Code.gguf", true, true)
            });
            sm.RecordVerifiedChatAnswer();
            int revision = sm.ConfigurationRevision;

            sm.ObserveTextModelConfiguration(new[]
            {
                ("CODING", @"d:\models\code.gguf", true, true),
                ("STANDARD", @"d:\models\chat.gguf", true, false)
            });

            Assert.Equal(revision, sm.ConfigurationRevision);
            Assert.True(sm.ChatAnswerVerified);
        }

        [Fact]
        public void MissingModelFileOrRoleEndpoint_RevokesPriorProof()
        {
            var sm = new OnboardingStateMachine();
            sm.ObserveTextModelConfiguration(new[] { ("standard", @"D:\models\chat.gguf", true, false) });
            sm.RecordVerifiedChatAnswer();
            sm.ObserveTextModelConfiguration(null);
            Assert.False(sm.ChatAnswerVerified);
            int unavailableRevision = sm.ConfigurationRevision;
            sm.ObserveTextModelConfiguration(null);
            Assert.Equal(unavailableRevision, sm.ConfigurationRevision);

            sm.ObserveTextModelConfiguration(new[] { ("standard", @"D:\models\chat.gguf", true, false) });
            sm.RecordVerifiedChatAnswer();
            sm.ObserveTextModelConfiguration(new[] { ("standard", @"D:\models\chat.gguf", false, false) });
            Assert.False(sm.ChatAnswerVerified);
            Assert.False(sm.CanComplete());
        }

        [Fact]
        public void Complete_WhenUnready_FailsAndDoesNotCompleteSilently()
        {
            var sm = new OnboardingStateMachine();

            Assert.False(sm.CanComplete());
            bool ok = sm.Complete();

            Assert.False(ok);
            Assert.False(sm.IsCompleted);
            Assert.NotEqual(OnboardingStep.Completed, sm.CurrentStep);
            Assert.Empty(sm.UnprovenItems);
        }
    }
}
