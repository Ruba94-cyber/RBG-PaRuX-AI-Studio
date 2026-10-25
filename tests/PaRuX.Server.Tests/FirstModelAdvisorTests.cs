using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The first thing a new user needs is an answer to "which model do I download?", and the cost of
/// getting it wrong is concrete: a 16 GB download that swaps to disk, and a conclusion that local AI
/// is slow. So these tests pin the direction the arithmetic errs in, not just that it produces a
/// number - a machine that cannot run something must be told so before the download, and a first
/// suggestion must be a model somebody can actually hold a conversation with.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class FirstModelAdvisorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Theory]
    // 4 GB of weights on a 16 GB card: fits with room for the KV cache and compute buffers.
    [InlineData(4, 16, 32, ModelSuitability.FitsOnGpu)]
    // 16 GB of weights on a 16 GB card does NOT fit - overhead is real, and this is the single most
    // common way a first download disappoints someone.
    [InlineData(16, 16, 32, ModelSuitability.SplitWithRam)]
    // No GPU, plenty of RAM: runs on the processor.
    [InlineData(4, 0, 32, ModelSuitability.CpuOnly)]
    // No GPU and not enough RAM either.
    [InlineData(30, 0, 16, ModelSuitability.TooLarge)]
    // Bigger than video memory and system RAM together.
    [InlineData(60, 8, 16, ModelSuitability.TooLarge)]
    public void A_model_is_classified_against_the_memory_that_will_actually_hold_it(
        int modelGb, int vramGb, int ramGb, ModelSuitability expected)
    {
        Assert.Equal(expected, FirstModelAdvisor.Classify(modelGb * Gb, vramGb * Gb, ramGb * Gb));
    }

    [Fact]
    public void Overhead_is_counted_so_a_model_the_size_of_the_card_is_not_promised_as_fitting()
    {
        // 12 GB of weights needs ~15 GB in practice. On a 16 GB card that fits; on a 14 GB one it
        // does not, and saying otherwise is the promise that breaks first.
        Assert.Equal(ModelSuitability.FitsOnGpu, FirstModelAdvisor.Classify(12 * Gb, 16 * Gb, 32 * Gb));
        Assert.NotEqual(ModelSuitability.FitsOnGpu, FirstModelAdvisor.Classify(12 * Gb, 14 * Gb, 32 * Gb));
    }

    [Fact]
    public void System_ram_is_not_counted_to_the_last_byte()
    {
        // 30 GB of weights on a 32 GB machine is not "fits in RAM": Windows, the browser and the
        // app itself are also in there.
        Assert.Equal(ModelSuitability.TooLarge, FirstModelAdvisor.Classify(30 * Gb, 0, 32 * Gb));
    }

    [Fact]
    public void A_zero_sized_model_is_unknown_rather_than_a_perfect_fit()
    {
        Assert.Equal(ModelSuitability.Unknown, FirstModelAdvisor.Classify(0, 16 * Gb, 32 * Gb));
    }

    // ---- the advice a person is shown ----

    [Fact]
    public void A_strong_gpu_gets_a_flagship_recommendation_that_names_the_hardware()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(16 * Gb, 32 * Gb, "NVIDIA GeForce RTX 5080");

        Assert.NotNull(advice.RecommendedId);
        Assert.Equal("qwen3-8-27b", advice.RecommendedId);
        Assert.True(advice.GpuDetected);
        Assert.Contains("RTX 5080", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("RTX 5080", advice.Headline, StringComparison.Ordinal);

        FirstModelCandidate best = advice.Candidates.First();
        Assert.Equal(advice.RecommendedId, best.Model.Id);
        Assert.Equal(nameof(ModelSuitability.FitsOnGpu), best.Suitability);
        Assert.Contains("13.26 GiB", best.Model.QuantHint, StringComparison.Ordinal);
        Assert.Contains("fit estimate, not a task-quality winner", advice.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_laptop_with_no_gpu_is_pointed_at_something_it_can_actually_run()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(0, 16 * Gb, string.Empty);

        Assert.NotNull(advice.RecommendedId);
        Assert.False(advice.GpuDetected);
        Assert.Contains("processor", advice.HardwareSummary, StringComparison.OrdinalIgnoreCase);

        FirstModelCandidate best = advice.Candidates.First(c => c.Model.Id == advice.RecommendedId);
        Assert.NotEqual(nameof(ModelSuitability.TooLarge), best.Suitability);
        // Small enough to be sane on a CPU: the flagship is not the answer here.
        Assert.True(best.Model.EstimatedSizeBytes < 8 * Gb,
            "Recommended " + best.Model.Title + " to a machine with no GPU.");
    }

    [Fact]
    public void An_uncensored_build_is_never_the_first_thing_suggested()
    {
        // It is a deliberate later choice. Leading with it on first run is a product deciding
        // something about its user that its user has not said.
        foreach (long vram in new[] { 0, 8 * Gb, 16 * Gb, 24 * Gb })
        {
            FirstModelAdvice advice = FirstModelAdvisor.Advise(vram, 32 * Gb, "GPU");
            RecommendedModel recommended = advice.Candidates.First(c => c.Model.Id == advice.RecommendedId).Model;
            Assert.DoesNotContain("uncensored", recommended.Category, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_general_chat_model_outranks_a_specialist_at_the_same_fit()
    {
        // Handing somebody a coding-only model as their first download teaches them that local AI
        // cannot hold a conversation.
        FirstModelAdvice advice = FirstModelAdvisor.Advise(24 * Gb, 64 * Gb, "GPU");
        RecommendedModel recommended = advice.Candidates.First(c => c.Model.Id == advice.RecommendedId).Model;

        Assert.DoesNotContain("Coding", recommended.Category, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Models_this_machine_cannot_run_are_still_listed_so_the_limit_is_visible()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(4 * Gb, 8 * Gb, "GPU");

        Assert.Equal(RecommendedModelsService.List.Count, advice.Candidates.Count);
        Assert.Contains(advice.Candidates, c => c.Suitability == nameof(ModelSuitability.TooLarge));
        // And they sort last, so the list still opens on something usable.
        Assert.NotEqual(nameof(ModelSuitability.TooLarge), advice.Candidates.First().Suitability);
    }

    [Fact]
    public void Every_candidate_explains_itself_in_a_sentence()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(16 * Gb, 32 * Gb, "NVIDIA GeForce RTX 5080");

        Assert.All(advice.Candidates, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Reason), c.Model.Id + " has no reason.");
            Assert.Contains("GB", c.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_primary_adapter_is_the_largest_real_one_whatever_order_it_arrives_in()
    {
        // Every caller that pairs a VRAM figure with a GPU name has to agree on which adapter that
        // is, or the two halves of the sentence describe different cards. The first-run advice and
        // the /api/v1/models/first-advice endpoint had each picked their own answer.
        var adapters = new List<GpuAdapterStatus>
        {
            new() { Name = "Microsoft Basic Render Driver", DedicatedVideoMemoryBytes = 32 * Gb, IsSoftware = true },
            new() { Name = "AMD Radeon 780M Graphics", DedicatedVideoMemoryBytes = 512L * 1024 * 1024 },
            new() { Name = "NVIDIA GeForce RTX 5080", DedicatedVideoMemoryBytes = 16 * Gb }
        };

        GpuAdapterStatus? primary = FirstModelAdvisor.SelectPrimaryAdapter(adapters);

        Assert.NotNull(primary);
        Assert.Equal("NVIDIA GeForce RTX 5080", primary!.Name);
        Assert.Null(FirstModelAdvisor.SelectPrimaryAdapter(new List<GpuAdapterStatus>()));
        Assert.Null(FirstModelAdvisor.SelectPrimaryAdapter(null));
    }

    [Fact]
    public void The_named_gpu_is_the_one_whose_video_memory_is_quoted()
    {
        // A desktop with a discrete card and an integrated one enumerates both, and the iGPU can
        // come first. Naming that adapter while quoting the discrete card's memory told a real
        // RTX 5080 machine it had an "AMD Radeon 780M Graphics with 15.6 GB of video memory".
        var hardware = new RuntimeManagerHardwareStatus
        {
            GpuAdapters =
            {
                new GpuAdapterStatus { Name = "AMD Radeon 780M Graphics", DedicatedVideoMemoryBytes = 512L * 1024 * 1024 },
                new GpuAdapterStatus { Name = "NVIDIA GeForce RTX 5080", DedicatedVideoMemoryBytes = 16 * Gb }
            },
            GpuLargestDedicatedVideoMemoryBytes = 16 * Gb,
            RamTotalBytes = 32 * Gb
        };

        FirstModelAdvice advice = FirstModelAdvisor.Advise(hardware);

        Assert.Contains("RTX 5080", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("780M", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.All(advice.Candidates, c =>
            Assert.DoesNotContain("780M", c.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void First_run_advice_uses_the_shared_memory_profile_as_one_RAM_pool()
    {
        var hardware = new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = 32 * Gb,
            GpuAdapters =
            {
                new GpuAdapterStatus
                {
                    Index = 0,
                    Name = "AMD Radeon 780M Graphics",
                    VendorId = 0x1002,
                    DedicatedVideoMemoryBytes = 512L * 1024 * 1024,
                    SharedSystemMemoryBytes = 15 * Gb
                }
            },
            AcceleratorProfile = new RuntimeAcceleratorProfile
            {
                GpuPresent = true,
                AcceleratorEligible = true,
                Backend = "text-vulkan",
                Vendor = "amd",
                AdapterName = "AMD Radeon 780M Graphics",
                DedicatedVideoMemoryBytes = 0,
                SharedSystemMemoryLimitBytes = 15 * Gb,
                SystemRamBytes = 32 * Gb,
                SharedMemoryPrimary = true,
                InventorySource = "dxgi"
            }
        };

        FirstModelAdvice advice = FirstModelAdvisor.Advise(hardware);

        Assert.True(advice.GpuDetected);
        Assert.Contains("AMD Radeon 780M", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("15 GB of shared system memory", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("not extra VRAM", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.NotNull(advice.RecommendedId);
        Assert.All(advice.Candidates, candidate =>
        {
            if (candidate.Suitability != nameof(ModelSuitability.Unknown))
                Assert.DoesNotContain("of video memory", candidate.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Production_first_run_advice_uses_the_runtime_profile_and_same_conservative_UMA_RAM_budget()
    {
        var status = new RuntimeManagerStatus
        {
            RamTotalBytes = 16 * Gb,
            Backends = new List<RuntimeBackendStatus>
            {
                new() { Backend = "text-vulkan", Installed = true }
            }
        };
        var inventory = new GpuInventoryStatus
        {
            Available = true,
            Source = "test-dxgi",
            Adapters = new List<GpuAdapterStatus>
            {
                new()
                {
                    Index = 0,
                    Name = "AMD Radeon 780M Graphics",
                    VendorId = 0x1002,
                    DedicatedVideoMemoryBytes = 512L * 1024 * 1024,
                    SharedSystemMemoryBytes = 15 * Gb
                }
            }
        };

        FirstModelAdvice advice = FirstModelAdvisor.Advise(status, inventory);

        Assert.True(advice.GpuDetected);
        Assert.Equal(8 * Gb, advice.VramBytes);
        Assert.Contains("AMD Radeon 780M", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("shared system memory", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("one shared pool", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(advice.Candidates, candidate =>
            candidate.Reason.Contains("of video memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void First_run_advice_reports_a_physical_GPU_without_claiming_it_is_executable()
    {
        var status = new RuntimeManagerStatus { RamTotalBytes = 16 * Gb };
        var inventory = new GpuInventoryStatus
        {
            Available = true,
            Source = "test-dxgi",
            Adapters = new List<GpuAdapterStatus>
            {
                new() { Name = "AMD discrete", VendorId = 0x1002, DedicatedVideoMemoryBytes = 8 * Gb }
            }
        };

        FirstModelAdvice advice = FirstModelAdvisor.Advise(status, inventory);

        Assert.True(advice.GpuDetected);
        Assert.Equal(0, advice.VramBytes);
        Assert.Contains("AMD discrete detected", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("no compatible PaRuX text backend", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains("CPU/RAM-only", advice.HardwareSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ineligible_shared_GPU_keeps_CPU_RAM_candidates_instead_of_marking_every_model_too_large()
    {
        var status = new RuntimeManagerStatus { RamTotalBytes = 16 * Gb };
        var inventory = new GpuInventoryStatus
        {
            Available = true,
            Source = "test-dxgi",
            Adapters = new List<GpuAdapterStatus>
            {
                new()
                {
                    Name = "AMD Radeon 780M Graphics",
                    VendorId = 0x1002,
                    DedicatedVideoMemoryBytes = 512L * 1024 * 1024,
                    SharedSystemMemoryBytes = 15 * Gb
                }
            }
        };

        FirstModelAdvice advice = FirstModelAdvisor.Advise(status, inventory);

        Assert.True(advice.GpuDetected);
        Assert.Equal(0, advice.VramBytes);
        Assert.Contains("no compatible PaRuX text backend", advice.HardwareSummary, StringComparison.Ordinal);
        Assert.Contains(advice.Candidates, candidate => candidate.Suitability == nameof(ModelSuitability.CpuOnly));
        Assert.DoesNotContain(advice.Candidates, candidate => candidate.Suitability == nameof(ModelSuitability.FitsOnGpu));
    }

    [Fact]
    public void A_split_model_is_never_described_as_larger_than_a_number_it_is_smaller_than()
    {
        // The verdict is reached on weights plus overhead, so quoting the raw weight size against
        // VRAM produced "About 13.4 GB, more than ... 15.6 GB of video memory" - a sentence that
        // contradicts itself and leaves the reader unsure which half to believe.
        FirstModelAdvice advice = FirstModelAdvisor.Advise(16 * Gb, 32 * Gb, "NVIDIA GeForce RTX 5080");

        foreach (FirstModelCandidate candidate in advice.Candidates
                     .Where(c => c.Suitability == nameof(ModelSuitability.SplitWithRam)))
        {
            double factor = candidate.Model.EstimatedRuntimeMemoryFactor ?? 1.25;
            long quoted = (long)(candidate.Model.EstimatedSizeBytes * factor);
            Assert.True(quoted > 16 * Gb,
                candidate.Model.Title + " is called 'more than' the video memory it is smaller than: " + candidate.Reason);
            Assert.Contains("KV cache", candidate.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unknown_hardware_still_produces_suggestions_and_says_they_are_generic()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(0, 0, string.Empty);

        Assert.NotEmpty(advice.Candidates);
        Assert.Contains("generic", advice.HardwareSummary, StringComparison.OrdinalIgnoreCase);
        // Nothing can be sized, so nothing is recommended - rather than recommending blindly.
        Assert.Null(advice.RecommendedId);
        Assert.Contains("Model Hub", advice.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void First_model_advice_is_scoped_by_task_and_never_claims_fit_is_quality_evidence()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(16 * Gb, 32 * Gb, "NVIDIA GeForce RTX 5080");

        FirstModelCapabilityGroup Group(string capability, string profile = "") =>
            advice.CapabilityGroups.Single(group => group.CapabilityId == capability
                && group.ContentProfileId == profile);

        FirstModelCapabilityGroup general = Group(ModelCapabilityIds.GeneralChat, ModelContentProfileIds.Standard);
        Assert.Contains(general.TextCandidates, candidate => candidate.Model.Id == "qwen3-8-27b");
        Assert.Contains(general.TextCandidates, candidate => candidate.Model.Id == "qwen3-4b");
        Assert.DoesNotContain(general.TextCandidates,
            candidate => candidate.Model.ContentProfileId == ModelContentProfileIds.Uncensored);

        Assert.Contains(Group(ModelCapabilityIds.Reasoning, ModelContentProfileIds.Standard).TextCandidates,
            candidate => candidate.Model.Id == "deepseek-r1-distill-qwen-32b");
        Assert.Contains(Group(ModelCapabilityIds.Coding, ModelContentProfileIds.Standard).TextCandidates,
            candidate => candidate.Model.Id == "qwen2-5-coder-32b");
        Assert.Contains(Group(ModelCapabilityIds.Vision, ModelContentProfileIds.Standard).TextCandidates,
            candidate => candidate.Model.Id == "qwen3-vl-32b");

        FirstModelCapabilityGroup uncensored = Group(ModelCapabilityIds.GeneralChat, ModelContentProfileIds.Uncensored);
        Assert.Single(uncensored.TextCandidates);
        Assert.Equal("qwen3-8-27b-uncensored", uncensored.TextCandidates[0].Model.Id);
        Assert.NotEqual(uncensored.TextCandidates[0].Model.Id, advice.RecommendedId);

        Assert.Contains(Group(ModelCapabilityIds.ImageGenerate).MediaCandidates,
            candidate => candidate.Id == "z-image-turbo-int8");
        Assert.Contains(Group(ModelCapabilityIds.ImageEdit).MediaCandidates,
            candidate => candidate.Id == "flux2-klein-base-4b");
        FirstModelCapabilityGroup video = Group(ModelCapabilityIds.VideoGenerate);
        Assert.Empty(video.MediaCandidates);
        Assert.Contains("No supported video candidate", video.EvidenceNote, StringComparison.Ordinal);

        Assert.All(advice.CapabilityGroups, group =>
            Assert.Equal(ModelAdviceEvidenceStates.Provisional, group.EvidenceState));
        Assert.Contains("not a task-quality winner", advice.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locally_available_models_are_ranked_higher_than_uninstalled_seed_downloads()
    {
        var localModels = new List<ManagedModelInfo>
        {
            new()
            {
                FileName = "qwen2.5-7b-instruct-q4_k_m.gguf",
                FilePath = "C:\\fake\\models\\qwen2.5-7b-instruct-q4_k_m.gguf",
                SizeBytes = 4L * Gb,
                Quantization = "Q4_K_M"
            }
        };

        FirstModelAdvice advice = FirstModelAdvisor.Advise(16 * Gb, 32 * Gb, "NVIDIA GeForce RTX 4070", localModels);

        Assert.NotNull(advice.RecommendedId);
        Assert.StartsWith("local:", advice.RecommendedId);
        Assert.Contains("Locally available model ready to use", advice.Headline);
        Assert.Contains(advice.Candidates, c => c.Model.Id == "local:qwen2.5-7b-instruct-q4_k_m" && c.Rank >= 100);
    }

    [Fact]
    public void Infer_capabilities_and_content_profile_from_local_filenames()
    {
        var codeCaps = FirstModelAdvisor.InferCapabilities("qwen2.5-coder-7b-instruct.gguf");
        Assert.Contains(ModelCapabilityIds.Coding, codeCaps);
        Assert.Contains(ModelCapabilityIds.GeneralChat, codeCaps);

        var reasonCaps = FirstModelAdvisor.InferCapabilities("DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf");
        Assert.Contains(ModelCapabilityIds.Reasoning, reasonCaps);

        var visionCaps = FirstModelAdvisor.InferCapabilities("qwen2-vl-7b-instruct.gguf");
        Assert.Contains(ModelCapabilityIds.Vision, visionCaps);

        var uncensoredProfile = FirstModelAdvisor.InferContentProfile("llama3-8b-abliterated.gguf");
        Assert.Equal(ModelContentProfileIds.Uncensored, uncensoredProfile);

        var standardProfile = FirstModelAdvisor.InferContentProfile("mistral-7b-instruct.gguf");
        Assert.Equal(ModelContentProfileIds.Standard, standardProfile);

        Assert.Equal("14B", FirstModelAdvisor.ExtractParamHint("DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf"));
    }

    [Fact]
    public void ModelRoleAutoAssigner_assigns_appropriate_roles_from_local_models()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "parux_autoassign_test_" + Guid.NewGuid().ToString("N"));
        string previousDataRoot = PaRuXDataRoot.Path;
        Directory.CreateDirectory(tempDir);
        try
        {
            PaRuXDataRoot.UseForTests(Path.Combine(tempDir, "parux-data"));
            using IDisposable localAiRoot = LocalAiPaths.UseRootForTests(tempDir);

            string generalFile = Path.Combine(tempDir, "general-chat-model.gguf");
            string coderFile = Path.Combine(tempDir, "qwen2.5-coder-7b.gguf");
            string r1File = Path.Combine(tempDir, "deepseek-r1-7b.gguf");
            string vlFile = Path.Combine(tempDir, "qwen-vl-7b.gguf");
            string uncensoredFile = Path.Combine(tempDir, "hermes-uncensored-7b.gguf");
            string smallFile = Path.Combine(tempDir, "tiny-1b.gguf");

            File.WriteAllText(generalFile, "dummy");
            File.WriteAllText(coderFile, "dummy");
            File.WriteAllText(r1File, "dummy");
            File.WriteAllText(vlFile, "dummy");
            File.WriteAllText(uncensoredFile, "dummy");
            File.WriteAllText(smallFile, "dummy");

            var localModels = new List<ManagedModelInfo>
            {
                new() { FileName = Path.GetFileName(generalFile), FilePath = generalFile, SizeBytes = 4L * Gb },
                new() { FileName = Path.GetFileName(coderFile), FilePath = coderFile, SizeBytes = 4L * Gb },
                new() { FileName = Path.GetFileName(r1File), FilePath = r1File, SizeBytes = 4L * Gb },
                new() { FileName = Path.GetFileName(vlFile), FilePath = vlFile, SizeBytes = 4L * Gb },
                new() { FileName = Path.GetFileName(uncensoredFile), FilePath = uncensoredFile, SizeBytes = 4L * Gb },
                new() { FileName = Path.GetFileName(smallFile), FilePath = smallFile, SizeBytes = 1L * Gb }
            };

            var status = new RuntimeManagerStatus { RamTotalBytes = 32L * Gb };
            var result = ModelRoleAutoAssigner.AutoAssignRoles(localModels, status, null);

            Assert.True(result.Success);
            Assert.NotEmpty(result.Assignments);

            Assert.Contains(result.Assignments, a => a.Role == "standard");
            Assert.Contains(result.Assignments, a => a.Role == "coding" && a.ModelPath == coderFile);
            Assert.Contains(result.Assignments, a => a.Role == "reasoning" && a.ModelPath == r1File);
            Assert.Contains(result.Assignments, a => a.Role == "vision" && a.ModelPath == vlFile);
            Assert.Contains(result.Assignments, a => a.Role == "uncensored" && a.ModelPath == uncensoredFile);
            Assert.Contains(result.Assignments, a => a.Role == "fast" && a.ModelPath == smallFile);
            Assert.Contains(result.Assignments, a => a.Role == "planner" && a.ModelPath == r1File);
            Assert.Contains(result.Assignments, a => a.Role == "reviewer" && a.ModelPath == coderFile);
        }
        finally
        {
            PaRuXDataRoot.UseForTests(previousDataRoot);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
