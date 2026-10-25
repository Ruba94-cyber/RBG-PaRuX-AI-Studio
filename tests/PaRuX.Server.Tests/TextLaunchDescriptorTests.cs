using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TextLaunchDescriptorTests
{
    private static readonly string[] Base =
    {
        "--model", "C:/models/a.gguf", "--alias", "local-compare", "--host", "127.0.0.1", "--port", "1234",
        "--jinja", "--reasoning", "auto", "--ctx-size", "32768", "--parallel", "1", "--gpu-layers", "-1",
        "--cache-type-k", "q8_0", "--cache-type-v", "q8_0", "--flash-attn", "on", "--temp", "0.8",
        "--ui", "--ui-config-file", "C:/some dir/ui.json", "--sleep-idle-seconds", "600"
    };

    private static TextLaunchDescriptor Describe(IReadOnlyList<string> args, string backend = "text-cuda",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        TextLaunchDescriptorResult result = TextLaunchDescriptor.Create(backend, args, environment);
        Assert.Null(result.Reason);
        return result.Descriptor!;
    }

    private static string? Reason(IReadOnlyList<string> args, string backend = "text-cuda",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        TextLaunchDescriptorResult result = TextLaunchDescriptor.Create(backend, args, environment);
        Assert.Null(result.Descriptor);
        return result.Reason;
    }

    private static string[] Replace(string flag, string value)
    {
        string[] copy = (string[])Base.Clone();
        copy[Array.IndexOf(copy, flag) + 1] = value;
        return copy;
    }

    [Fact]
    public void Ephemeral_model_alias_port_and_ui_values_and_argument_order_do_not_change_identity()
    {
        TextLaunchDescriptor original = Describe(Base);
        Assert.True(TextLaunchDescriptor.IsActualLaunchFingerprint(original.Fingerprint));
        Assert.Equal(1, original.Version);

        string[] moved = Replace("--model", "D:/other place/b.gguf");
        moved[Array.IndexOf(moved, "--alias") + 1] = "parux-quality-1";
        moved[Array.IndexOf(moved, "--port") + 1] = "45555";
        moved[Array.IndexOf(moved, "--ui-config-file") + 1] = "E:/x.json";
        Assert.Equal(original.Fingerprint, Describe(moved).Fingerprint);

        // Pairwise reorder of flag/value groups.
        var groups = new List<string[]>();
        for (int i = 0; i < Base.Length;)
        {
            bool takesValue = i + 1 < Base.Length && !Base[i + 1].StartsWith("--", StringComparison.Ordinal);
            groups.Add(takesValue ? new[] { Base[i], Base[i + 1] } : new[] { Base[i] });
            i += takesValue ? 2 : 1;
        }
        groups.Reverse();
        Assert.Equal(original.Fingerprint, Describe(groups.SelectMany(g => g).ToArray()).Fingerprint);
    }

    [Theory]
    [InlineData("--ctx-size", "2048")]
    [InlineData("--parallel", "4")]
    [InlineData("--gpu-layers", "20")]
    [InlineData("--cache-type-k", "f16")]
    [InlineData("--flash-attn", "off")]
    [InlineData("--temp", "0.0")]
    [InlineData("--reasoning", "off")]
    [InlineData("--sleep-idle-seconds", "-1")]
    public void Every_semantic_value_change_changes_identity(string flag, string value)
    {
        Assert.NotEqual(Describe(Base).Fingerprint, Describe(Replace(flag, value)).Fingerprint);
    }

    [Fact]
    public void Backend_switches_and_launch_environment_are_semantic()
    {
        string baseline = Describe(Base).Fingerprint;
        Assert.NotEqual(baseline, Describe(Base, "text-vulkan").Fingerprint);
        Assert.NotEqual(baseline, Describe(Base.Where(a => a != "--jinja").ToArray()).Fingerprint);
        Assert.NotEqual(baseline, Describe(Base.Append("--no-kv-offload").ToArray()).Fingerprint);
        Assert.NotEqual(baseline, Describe(Base, environment: new Dictionary<string, string>
            { ["GGML_CUDA_P2P"] = "1" }).Fingerprint);
        Assert.NotEqual(baseline, Describe(Base, environment: new Dictionary<string, string>
            { ["LLAMA_ARG_ENDPOINT_SLOTS"] = "1" }).Fingerprint);
        Assert.NotEqual(baseline, Describe(Base, environment: new Dictionary<string, string>
            { ["CUDA_VISIBLE_DEVICES"] = "1" }).Fingerprint);
        // Case-insensitive Windows names cannot split the identity.
        Assert.Equal(
            Describe(Base, environment: new Dictionary<string, string> { ["ggml_cuda_p2p"] = "1" }).Fingerprint,
            Describe(Base, environment: new Dictionary<string, string> { ["GGML_CUDA_P2P"] = "1" }).Fingerprint);
        // Unrelated variables and credentials are neither identity nor copied into the record.
        TextLaunchDescriptor withSecrets = Describe(Base, environment: new Dictionary<string, string>
        {
            ["PATH"] = "C:/x", ["LLAMA_API_KEY"] = "secret-value", ["LLAMA_ARG_API_KEY"] = "secret-value"
        });
        Assert.Equal(baseline, withSecrets.Fingerprint);
        Assert.DoesNotContain(withSecrets.Environment, entry => entry.Contains("secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("VK_ICD_FILENAMES", "C:/drivers/other_icd.json")]
    [InlineData("CUDA_DEVICE_ORDER", "PCI_BUS_ID")]
    [InlineData("CUDA_MODULE_LOADING", "EAGER")]
    [InlineData("OMP_NUM_THREADS", "4")]
    [InlineData("HSA_OVERRIDE_GFX_VERSION", "11.0.0")]
    [InlineData("LLAMA_ARG_CTX_SIZE", "4096")]
    [InlineData("LLAMA_ARG_N_GPU_LAYERS", "10")]
    [InlineData("LLAMA_ARG_IMAGE_MIN_TOKENS", "64")]
    [InlineData("LLAMA_ARG_ENDPOINT_SLOTS", "1")]
    [InlineData("LLAMA_CACHE", "C:/cache dir")]
    public void Device_driver_math_and_argument_environment_is_hashed(string name, string value)
    {
        Assert.NotEqual(Describe(Base).Fingerprint,
            Describe(Base, environment: new Dictionary<string, string> { [name] = value }).Fingerprint);
    }

    [Theory]
    [InlineData("LLAMA_ARG_MMPROJ", "unbound_file_or_router_environment")]
    [InlineData("LLAMA_ARG_MODEL_DRAFT", "unbound_file_or_router_environment")]
    [InlineData("LLAMA_ARG_CHAT_TEMPLATE_FILE", "unbound_file_or_router_environment")]
    [InlineData("LLAMA_ARG_MODELS_PRESET", "unbound_file_or_router_environment")]
    [InlineData("LLAMA_ARG_MODELS_DIR", "unbound_file_or_router_environment")]
    [InlineData("LLAMA_ARG_HF_REPO", "unknown_argument_environment")]
    [InlineData("LLAMA_ARG_MODEL", "unknown_argument_environment")]
    [InlineData("LLAMA_ARG_SOMETHING_NEW", "unknown_argument_environment")]
    [InlineData("GGML_VENDOR_SECRET", "credential_environment")]
    [InlineData("CUDA_AUTH_TOKEN", "credential_environment")]
    public void File_binding_router_unknown_or_credential_environment_is_refused(string name, string reason)
    {
        Assert.Equal(reason, Reason(Base, environment: new Dictionary<string, string> { [name] = "x" }));
    }

    [Fact]
    public void Verified_descriptor_is_withheld_on_pid_reuse_or_image_change()
    {
        TextLaunchDescriptor descriptor = Describe(Base);
        try
        {
            LocalAiProcessManager.RecordTextLaunchIdentity(Environment.ProcessId, descriptor);
            Assert.True(LocalAiProcessManager.TryGetTextLaunchIdentity(out ExpectedPeerIdentity recorded));
            Assert.Equal(descriptor.Fingerprint, LocalAiProcessManager.GetVerifiedTextLaunchDescriptor(
                new Inspector(recorded.ProcessId, recorded.StartTimeUtcTicks, recorded.ImagePath))!.Fingerprint);
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor(
                new Inspector(recorded.ProcessId, recorded.StartTimeUtcTicks + 1, recorded.ImagePath)));
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor(
                new Inspector(recorded.ProcessId, recorded.StartTimeUtcTicks, "C:/elsewhere/llama-server.exe")));
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor(new Inspector(-1, 0, "")));
            LocalAiProcessManager.RecordTextLaunchIdentity(Environment.ProcessId);
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor());
        }
        finally { LocalAiProcessManager.ClearTextLaunchIdentity(); }
        Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor());
    }

    private sealed class Inspector(int pid, long start, string image) : IPeerProcessInspector
    {
        public bool TryOpen(int processId, out IPeerProcessLease lease)
        {
            lease = new Lease(start, image);
            return processId == pid;
        }

        private sealed class Lease(long start, string image) : IPeerProcessLease
        {
            public long StartTimeUtcTicks => start;
            public string ImagePath => image;
            public bool HasExited => false;
            public void Dispose() { }
        }
    }

    [Fact]
    public void Unsupported_ambiguous_or_unbound_launches_are_refused_not_approximated()
    {
        Assert.Equal("unknown_argument", Reason(Base.Append("--mystery-flag").ToArray()));
        Assert.Equal("duplicate_argument", Reason(Base.Concat(new[] { "--ctx-size", "2048" }).ToArray()));
        Assert.Equal("unbound_file_or_router_argument", Reason(Base.Concat(new[] { "--mmproj", "C:/p.gguf" }).ToArray()));
        Assert.Equal("unbound_file_or_router_argument", Reason(Base.Concat(new[] { "--lora", "C:/a.gguf" }).ToArray()));
        Assert.Equal("unbound_file_or_router_argument", Reason(Base.Concat(new[] { "--spec-draft-model", "C:/d.gguf" }).ToArray()));
        Assert.Equal("unbound_file_or_router_argument", Reason(Base.Concat(new[] { "--models-preset", "C:/p.ini" }).ToArray()));
        Assert.Equal("non_loopback_host", Reason(Replace("--host", "0.0.0.0")));
        Assert.Equal("missing_value", Reason(new[] { "--model", "C:/a.gguf", "--ctx-size", "--jinja" }));
        Assert.Equal("missing_value", Reason(new[] { "--model", "C:/a.gguf", "--ctx-size" }));
        Assert.Equal("single_model_launch_required", Reason(Base.Skip(2).ToArray()));
        Assert.Equal("invalid_value", Reason(Replace("--temp", "0.8 --evil")));
        Assert.Equal("backend_unsupported", Reason(Base, "text-cpu"));
        Assert.Equal("arguments_unavailable", Reason(Array.Empty<string>()));
        Assert.Equal("invalid_environment", Reason(Base, environment: new Dictionary<string, string>
            { ["GGML_X"] = "line\nbreak" }));
    }

    [Fact]
    public void Historical_fixed_evaluator_profile_is_never_an_actual_launch_identity()
    {
        Assert.False(TextLaunchDescriptor.IsActualLaunchFingerprint(LlamaTextQualitySession.ConfigurationFingerprint));
        Assert.False(TextLaunchDescriptor.IsActualLaunchFingerprint("ld1-sha256:" + new string('A', 64)));
        Assert.False(TextLaunchDescriptor.IsActualLaunchFingerprint("ld2-sha256:" + new string('a', 64)));
        Assert.False(TextLaunchDescriptor.IsActualLaunchFingerprint(null));
    }

    public static TheoryData<string> ProfileVariants() => new()
    {
        "adaptive-vulkan", "cuda-layer-split", "speculative-ngram", "rope-and-ram-kv", "numa-strict-threads",
        "per-request-context", "backend-sampling-p2p", "cpu-fallback"
    };

    /// <summary>
    /// Regression guard: every flag the production builders can emit must be classified. If someone adds
    /// a new llama-server argument to RuntimeLaunchOptimizer or the arbitrary loader without teaching the
    /// descriptor, that launch silently loses all recommendation authority - this test makes it loud.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfileVariants))]
    public void Every_production_builder_variant_is_describable(string variant)
    {
        var profile = new LlamaPerformanceProfileDto { BatchSize = 512, UBatchSize = 128, ParallelSlots = 0 };
        bool cuda = true;
        switch (variant)
        {
            case "adaptive-vulkan": cuda = false; profile.Device = "Vulkan0"; break;
            case "cuda-layer-split": profile.SplitMode = "layer"; profile.Device = "CUDA0,CUDA1"; profile.TensorSplit = "16000,8000"; break;
            case "speculative-ngram": profile.SpeculativeType = "ngram-simple"; profile.SpeculativeDraftMax = 16; profile.SpeculativeDraftDevice = "CUDA0"; break;
            case "rope-and-ram-kv": profile.RopeScale = 4; profile.DisableKvOffload = true; break;
            case "numa-strict-threads": profile.NumaMode = "distribute"; profile.CpuStrict = true; profile.Threads = 6; profile.ThreadsBatch = 8; break;
            case "per-request-context": profile.ContextSize = 65536; profile.ContextIsPerRequest = true; profile.ParallelSlots = 2; break;
            case "backend-sampling-p2p": profile.BackendSampling = true; profile.EnableCudaP2P = true; break;
            case "cpu-fallback": cuda = false; break;
        }
        List<string> args = LocalAiProcessManager.BuildArbitraryTextArguments("C:/m.gguf", cuda,
            RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, cuda), adapterPaths: null);
        if (variant == "cpu-fallback") args = RuntimeLaunchOptimizer.ToCpuFallbackArguments(args);
        TextLaunchDescriptor descriptor = Describe(args, cuda ? "text-cuda" : "text-vulkan",
            RuntimeLaunchOptimizer.LaunchEnvironment(profile));
        Assert.DoesNotContain(descriptor.Arguments, a => a.StartsWith("--model", StringComparison.Ordinal)
            || a.StartsWith("--port", StringComparison.Ordinal) || a.StartsWith("--alias", StringComparison.Ordinal));
    }

    [Fact]
    public void Lora_adapters_and_draft_model_paths_are_unbound_files()
    {
        var profile = new LlamaPerformanceProfileDto { BatchSize = 512, UBatchSize = 128 };
        List<string> withAdapter = LocalAiProcessManager.BuildArbitraryTextArguments("C:/m.gguf", true,
            RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, true), new[] { "C:/adapter.gguf" });
        Assert.Equal("unbound_file_or_router_argument", Reason(withAdapter));
        profile.SpeculativeType = "draft-simple";
        profile.SpeculativeDraftModelPath = "C:/draft.gguf";
        List<string> withDraft = LocalAiProcessManager.BuildArbitraryTextArguments("C:/m.gguf", true,
            RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, true), null);
        Assert.Equal("unbound_file_or_router_argument", Reason(withDraft));
    }
}
