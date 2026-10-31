using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class BundledRuntimeOverviewPresenterTests
    {
        [Fact]
        public void BuildOverview_NullOrEmptyComponents_ReturnsWorkingTextPathFalseAndDefaults()
        {
            FirstInstallEffortOverview overview = BundledRuntimeOverviewPresenter.BuildOverview(null, null);

            Assert.NotNull(overview);
            Assert.False(overview.HasWorkingTextPathWithoutDownload);
            Assert.Contains("Checking the included text engine", overview.Headline);
            Assert.Contains("Vulkan engine", overview.Subhead);
            Assert.DoesNotContain("ready", overview.Headline, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(3, overview.BundledItems.Count);
            BundledComponentItem vulkan = overview.BundledItems.First(b => b.Id == BundledRuntimeOverviewPresenter.BundledVulkanId);
            Assert.False(vulkan.IsStaged);
            Assert.Equal(0L, vulkan.DownloadSizeBytes);
            Assert.Contains("0 B", vulkan.DownloadSizeText);
            Assert.Contains("45 MB", vulkan.BundledSizeText);

            BundledComponentItem uv = overview.BundledItems.First(b => b.Id == BundledRuntimeOverviewPresenter.BundledUvId);
            Assert.False(uv.IsStaged);
            Assert.Equal(0L, uv.DownloadSizeBytes);

            BundledComponentItem app = overview.BundledItems.First(b => b.Id == BundledRuntimeOverviewPresenter.BundledAppId);
            Assert.True(app.IsStaged);

            Assert.Equal(3, overview.DownloadableItems.Count);
            DownloadableComponentItem model = overview.DownloadableItems.First(d => d.Id == BundledRuntimeOverviewPresenter.DownloadableModelId);
            Assert.True(model.IsRequired);
            Assert.Equal("Compatible Chat Model", model.DisplayName);
            Assert.Equal(0, model.EstimatedSizeBytes);
            Assert.Equal("Varies by model", model.EstimatedSizeText);
            Assert.Contains("choose a compatible model in Model Hub", model.WhenDownloaded, StringComparison.OrdinalIgnoreCase);

            DownloadableComponentItem cuda = overview.DownloadableItems.First(d => d.Id == BundledRuntimeOverviewPresenter.DownloadableCudaId);
            Assert.False(cuda.IsRequired);
            Assert.True(cuda.EstimatedSizeBytes > 0);

            DownloadableComponentItem comfy = overview.DownloadableItems.First(d => d.Id == BundledRuntimeOverviewPresenter.DownloadableComfyUiId);
            Assert.False(comfy.IsRequired);
            Assert.True(comfy.EstimatedSizeBytes > 0);
        }

        [Fact]
        public void BuildOverview_WhenVulkanDetected_MarksWorkingTextPathTrueAndStaged()
        {
            var components = new List<ComponentStatus>
            {
                new()
                {
                    Id = "llama-server-vulkan",
                    DisplayName = "llama.cpp (Vulkan)",
                    Detected = true,
                    Health = "healthy",
                    Ownership = ComponentOwnership.PARUX_MANAGED
                },
                new()
                {
                    Id = "uv",
                    DisplayName = "uv package manager",
                    Detected = true,
                    Health = "healthy",
                    Ownership = ComponentOwnership.PARUX_MANAGED
                }
            };

            FirstInstallEffortOverview overview = BundledRuntimeOverviewPresenter.BuildOverview(components, null);

            Assert.True(overview.HasWorkingTextPathWithoutDownload);
            Assert.Contains("Included text engine detected", overview.Headline);
            Assert.Contains("choose a compatible chat model in Model Hub", overview.Headline, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("verify a real local answer", overview.Subhead);

            BundledComponentItem vulkan = overview.BundledItems.First(b => b.Id == BundledRuntimeOverviewPresenter.BundledVulkanId);
            Assert.True(vulkan.IsStaged);
            BundledComponentItem uv = overview.BundledItems.First(b => b.Id == BundledRuntimeOverviewPresenter.BundledUvId);
            Assert.True(uv.IsStaged);
        }

        [Fact]
        public void BuildOverview_WhenVulkanSkippedOrMissing_MarksWorkingTextPathFalse()
        {
            var components = new List<ComponentStatus>
            {
                new()
                {
                    Id = "llama-server-vulkan",
                    DisplayName = "llama.cpp (Vulkan)",
                    Detected = false,
                    Skipped = true,
                    Health = "skipped for now",
                    Ownership = ComponentOwnership.PARUX_MANAGED
                }
            };

            FirstInstallEffortOverview overview = BundledRuntimeOverviewPresenter.BuildOverview(components, null);

            Assert.False(overview.HasWorkingTextPathWithoutDownload);
            Assert.Contains("not detected", overview.Headline);
            BundledComponentItem vulkan = overview.BundledItems.First(b => b.Id == BundledRuntimeOverviewPresenter.BundledVulkanId);
            Assert.False(vulkan.IsStaged);
        }

        [Fact]
        public void BuildOverview_WithModelAdvice_DoesNotPromoteARecommendedModel()
        {
            var advice = new FirstModelAdvice
            {
                Candidates = new List<FirstModelCandidate>
                {
                    new()
                    {
                        Model = new RecommendedModel
                        {
                            Id = "qwen-7b",
                            Title = "Qwen 2.5 7B Instruct",
                            ApproxParams = "7B",
                            QuantHint = "Q4_K_M",
                            EstimatedSizeBytes = 3_800_000_000L
                        },
                        Suitability = "FitsOnGpu",
                        Rank = 1
                    }
                }
            };

            FirstInstallEffortOverview overview = BundledRuntimeOverviewPresenter.BuildOverview(null, advice);

            DownloadableComponentItem model = overview.DownloadableItems.First(d => d.Id == BundledRuntimeOverviewPresenter.DownloadableModelId);
            Assert.Equal("Compatible Chat Model", model.DisplayName);
            Assert.Equal(0, model.EstimatedSizeBytes);
            Assert.Equal("Varies by model", model.EstimatedSizeText);
            Assert.DoesNotContain("Qwen", model.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(-5, "0 B")]
        [InlineData(500, "500 B")]
        [InlineData(1024, "~1.0 KB")]
        [InlineData(47_185_920, "~45.0 MB")]
        [InlineData(2_362_232_012, "~2.2 GB")]
        public void FormatSize_ConvertsBytesCorrectly(long bytes, string expected)
        {
            string actual = BundledRuntimeOverviewPresenter.FormatSize(bytes);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void HashVerificationGuidance_ContainsPowerShellAndCertUtilAndAuthenticode()
        {
            FirstInstallEffortOverview overview = BundledRuntimeOverviewPresenter.BuildOverview(null, null);

            Assert.Contains("Get-FileHash -Algorithm SHA256", overview.PowerShellHashCommand);
            Assert.Contains("certutil -hashfile", overview.CmdHashCommand);
            Assert.Contains("Get-AuthenticodeSignature", overview.AuthenticodeCheckCommand);
            Assert.Contains("Get-FileHash", overview.HashCheckGuidance);
            Assert.Contains("SHA256SUMS.txt", overview.HashCheckGuidance);
            Assert.Equal("docs/AI_LOCAL_SETUP.md", overview.DocReference);
        }

        [Fact]
        public void OnboardingStateMachine_HasBundledTextRuntime_IdentifiesVulkanPresence()
        {
            var sm = new OnboardingStateMachine();
            Assert.False(sm.HasBundledTextRuntime);

            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-cuda", Detected = true },
                new() { Id = "llama-server-vulkan", Detected = false }
            });
            Assert.False(sm.HasBundledTextRuntime);

            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-vulkan", Detected = true, Health = "healthy" }
            });
            Assert.True(sm.HasBundledTextRuntime);
        }

        [Fact]
        public void OnboardingStateMachine_EvaluateReadiness_WhenBundledVulkanHealthy_SummaryReflectsBundledTextRuntime()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new List<ComponentState>
            {
                new() { Id = "llama-server-vulkan", Detected = true, Health = "healthy" }
            });

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "llama.cpp Vulkan healthy",
                modelAvailable: true,
                modelSummary: "TinyLlama-1.1B.gguf",
                probeSucceeded: true,
                probeSummary: "Probe succeeded in 8ms",
                chatAnswerVerified: true);

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.True(result.IsReady);
            Assert.Contains("Chat is fully configured", result.Summary);
            Assert.Contains("bundled local text runtime (Vulkan) is healthy and ready without extra download", result.Summary);
        }

        [Fact]
        public void Included_runtime_and_model_without_a_real_chat_answer_are_not_ready()
        {
            var sm = new OnboardingStateMachine();
            sm.SetComponents(new[]
            {
                new ComponentState { Id = "llama-server-vulkan", Detected = true, Health = "healthy" }
            });

            ReadinessAssessment readiness = sm.EvaluateReadiness(ChatReadinessEvidence.Create(
                runtimeHealthy: true,
                runtimeSummary: "Detected",
                modelAvailable: true,
                modelSummary: "installed-model.gguf",
                probeSucceeded: true,
                probeSummary: "Backend health check succeeded"));

            Assert.False(readiness.IsReady);
            Assert.False(sm.CanComplete());
            Assert.Contains("verified chat answer", readiness.FailureReason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OnboardingStateMachine_EvaluateReadiness_WhenVulkanMissing_NextActionMentionsBundledInstaller()
        {
            var sm = new OnboardingStateMachine();
            sm.ConfigureRoot(@"C:\LocalAI");

            var evidence = ChatReadinessEvidence.Create(
                runtimeHealthy: false,
                runtimeSummary: "No process",
                modelAvailable: true,
                modelSummary: "TinyLlama",
                probeSucceeded: false,
                probeSummary: null);

            ReadinessAssessment result = sm.EvaluateReadiness(evidence);

            Assert.False(result.IsReady);
            Assert.Contains("No text runtime", result.FailureReason);
            Assert.Contains("bundles the Vulkan runtime in the installer", result.NextAction);
        }

        [Fact]
        public void BuildOverview_BundledCudaEngineAloneCountsAsADetectedTextEngine()
        {
            // Real clean install on an RTX 5080: only the bundled CUDA 13.4 build is installed, and the page said
            // "Text engine not detected" because it only looked for Vulkan, right under "PaRuX is ready".
            var components = new List<ComponentStatus>
            {
                new() { Id = "llama-server-cuda", DisplayName = "llama.cpp CUDA", Detected = true, Health = "healthy",
                    Ownership = ComponentOwnership.PARUX_MANAGED },
                new() { Id = "uv", DisplayName = "uv", Detected = true, Health = "healthy",
                    Ownership = ComponentOwnership.PARUX_MANAGED }
            };

            FirstInstallEffortOverview overview = BundledRuntimeOverviewPresenter.BuildOverview(components, null);

            Assert.True(overview.HasWorkingTextPathWithoutDownload);
            Assert.Contains("Included text engine detected", overview.Headline);
            Assert.Contains("CUDA", overview.Subhead);
        }
    }
}
