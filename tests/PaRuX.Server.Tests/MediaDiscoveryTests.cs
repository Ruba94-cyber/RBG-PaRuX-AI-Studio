using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Media discovery from Hugging Face: parsers, policy, lane evidence, refresh with a fake provider (no network), the merged
/// catalogue, best-proposed ranking, routing of installed discovered models, proposals and maintenance gating.</summary>
public sealed class MediaDiscoveryTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-media-disc-" + Guid.NewGuid().ToString("N"));
    private static string Rev(char c) => new(c, 40);
    private static string Sha(char c) => new(c, 64);

    public MediaDiscoveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ---- real-shaped JSON (captured from the live API) ----

    private const string SearchJson = """
        [{"_id":"64bfcd5ff462a99a04fd1ec8","id":"stabilityai/stable-diffusion-xl-base-1.0","cardData":{"license":"openrail++","tags":["text-to-image"]},"gated":false,"lastModified":"2023-10-30T16:03:47.000Z","likes":8395,"private":false,"sha":"462165984030d82259a11f4367a4eed129e94a7b","downloads":4282392,"tags":["diffusers","safetensors","text-to-image","license:openrail++"],"pipeline_tag":"text-to-image"},
         {"_id":"x","id":"someone/gated-thing","cardData":{"license":"other","license_name":"custom"},"gated":"manual","likes":3,"private":false,"sha":"ac93e0dda1f6d448cae19bbfab8c5e720a5e48bc","downloads":9,"tags":["license:other"]},
         {"id":"../evil","downloads":1}]
        """;

    private const string TreeJson = """
        [{"type":"directory","oid":"3aa7","size":0,"path":"split_files"},
         {"type":"file","oid":"a634","size":1519,"path":".gitattributes"},
         {"type":"file","oid":"7b3f","size":12309866400,"lfs":{"oid":"2407613050b809ffdff18a4ac99af83ea6b95443ecebdf80e064a79c825574a6","size":12309866400,"pointerSize":136},"path":"split_files/diffusion_models/z_image_turbo_bf16.safetensors"},
         {"type":"file","oid":"1787","size":158826336,"lfs":{"oid":"NOT-HEX","size":158826336},"path":"split_files/loras/x.safetensors"}]
        """;

    [Fact]
    public void Search_parser_reads_live_shape_and_skips_unsafe_ids()
    {
        List<MediaRepoSummary> repos = HuggingFaceMediaDiscoveryProvider.ParseSearch(SearchJson);
        Assert.Equal(2, repos.Count);
        MediaRepoSummary sdxl = repos[0];
        Assert.Equal("openrail++", sdxl.License);
        Assert.Equal("false", sdxl.Gated);
        Assert.Equal(4282392, sdxl.Downloads);
        Assert.Equal(8395, sdxl.Likes);
        Assert.Equal(new DateTime(2023, 10, 30, 16, 3, 47, DateTimeKind.Utc), sdxl.LastModifiedUtc);
        Assert.Equal("462165984030d82259a11f4367a4eed129e94a7b", sdxl.Sha);
        Assert.Equal("manual", repos[1].Gated);
        Assert.Throws<MediaDiscoveryFetchException>(() => HuggingFaceMediaDiscoveryProvider.ParseSearch("{not json"));
    }

    [Fact]
    public void Tree_parser_keeps_files_and_only_valid_lfs_sha()
    {
        List<MediaRepoFile> files = HuggingFaceMediaDiscoveryProvider.ParseTree(TreeJson);
        Assert.Equal(3, files.Count);
        MediaRepoFile big = files.Single(f => f.Path.EndsWith("bf16.safetensors"));
        Assert.Equal(12309866400, big.SizeBytes);
        Assert.Equal("2407613050b809ffdff18a4ac99af83ea6b95443ecebdf80e064a79c825574a6", big.Sha256);
        Assert.Null(files.Single(f => f.Path == ".gitattributes").Sha256);
        Assert.Null(files.Single(f => f.Path == "split_files/loras/x.safetensors").Sha256);
    }

    // ---- policy ----

    [Theory]
    [InlineData("apache-2.0", null, null)]
    [InlineData("openrail++", null, null)]
    [InlineData("creativeml-openrail-m", null, null)]
    [InlineData(null, null, "unknown_license")]
    [InlineData("", null, "unknown_license")]
    [InlineData("other", null, "unknown_license")]
    [InlineData("other", "flux-1-dev-non-commercial-license", null)]
    [InlineData("other", "research-only-no-redistribution", "non_redistributable_license")]
    [InlineData("unknown", null, "non_redistributable_license")]
    public void License_policy(string? license, string? name, string? expected) =>
        Assert.Equal(expected, MediaDiscoveryPolicy.LicenseRefusal(license, name));

    [Fact]
    public void Gated_private_and_disabled_repositories_are_refused()
    {
        Assert.Equal("gated", MediaDiscoveryPolicy.RepoRefusal(new MediaRepoSummary { Gated = "auto", License = "mit" }));
        Assert.Equal("gated", MediaDiscoveryPolicy.RepoRefusal(new MediaRepoSummary { Gated = "true", License = "mit" }));
        Assert.Equal("private", MediaDiscoveryPolicy.RepoRefusal(new MediaRepoSummary { Private = true, License = "mit" }));
        Assert.Equal("disabled", MediaDiscoveryPolicy.RepoRefusal(new MediaRepoSummary { Disabled = true, License = "mit" }));
        Assert.Null(MediaDiscoveryPolicy.RepoRefusal(new MediaRepoSummary { License = "mit" }));
    }

    [Theory]
    [InlineData("model.safetensors", true)]
    [InlineData("diffusion_pytorch_model.fp16.safetensors", true)]
    [InlineData("unet.safetensors", true)]
    [InlineData("z_image_turbo_bf16.safetensors", false)]
    [InlineData("Qwen-Image-2.1-fp8.safetensors", false)]
    public void Generic_file_names_are_not_offered(string name, bool generic) =>
        Assert.Equal(generic, MediaDiscoveryPolicy.IsGenericFileName(name));

    [Fact]
    public void File_selection_skips_fragments_oversize_and_generic_names()
    {
        var files = new[]
        {
            new MediaRepoFile("vae/ae.safetensors", 300L * 1024 * 1024 / 2, null),
            new MediaRepoFile("model.safetensors", 6 * Gib, null),
            new MediaRepoFile("huge.safetensors", 60 * Gib, null),
            new MediaRepoFile("good-bf16.safetensors", 10 * Gib, null),
            new MediaRepoFile("good-fp8.safetensors", 5 * Gib, null)
        };
        var picked = MediaDiscoveryPolicy.SelectCheckpointFiles(files, out string? reason);
        Assert.Null(reason);
        Assert.Equal(new[] { "good-bf16.safetensors", "good-fp8.safetensors" }, picked.Select(f => f.Path));
        MediaDiscoveryPolicy.SelectCheckpointFiles(new[] { new MediaRepoFile("huge.safetensors", 60 * Gib, null) }, out string? r2);
        Assert.Equal("file_too_large", r2);
    }

    [Fact]
    public void Sharded_checkpoints_are_not_offered_because_a_single_shard_cannot_run()
    {
        var shards = new[]
        {
            new MediaRepoFile("transformer/diffusion_pytorch_model-00001-of-00009.safetensors", 5 * Gib, null),
            new MediaRepoFile("transformer/diffusion_pytorch_model-00002-of-00009.safetensors", 5 * Gib, null)
        };
        Assert.Empty(MediaDiscoveryPolicy.SelectCheckpointFiles(shards, out string? reason));
        Assert.Equal("sharded_checkpoint", reason);
        var mixed = shards.Append(new MediaRepoFile("qwen_image_fp8.safetensors", 20 * Gib, null));
        Assert.Equal("qwen_image_fp8.safetensors", Assert.Single(MediaDiscoveryPolicy.SelectCheckpointFiles(mixed, out _)).Path);
    }

    [Fact]
    public void Capability_follows_the_pipeline_tag_and_what_the_family_runs()
    {
        RegistryFamily z = MediaCompanionRegistry.Shipped.Families.First(f => f.Id == "z-image");
        Assert.Equal("image.generate", MediaDiscoveryPolicy.ResolveCapability(z, "text-to-image"));
        Assert.Null(MediaDiscoveryPolicy.ResolveCapability(z, "image-to-image")); // z-image has no edit pipeline
        Assert.Null(MediaDiscoveryPolicy.ResolveCapability(z, "text-to-video"));
        Assert.Null(MediaDiscoveryPolicy.ResolveCapability(z, "feature-extraction"));
    }

    // ---- lane evidence ----

    [Fact]
    public void Lane_needs_explicit_affirmative_evidence_and_stores_it()
    {
        var (lane, ev) = MediaLaneClassifier.Classify(new[] { "diffusers", "uncensored" }, null, "o/n");
        Assert.Equal("uncensored", lane);
        Assert.Contains("uncensored", ev);

        (lane, ev) = MediaLaneClassifier.Classify(Array.Empty<string>(), null, "abenzerps/Qwen-Image-2.1-Uncensored-Quantized");
        Assert.Equal("uncensored", lane);
        Assert.Contains("uncensored", ev);

        (lane, ev) = MediaLaneClassifier.Classify(Array.Empty<string>(), "# Model\nThis fine-tune ships with no safety filter and was trained on NSFW art.", "o/n");
        Assert.Equal("uncensored", lane);
        Assert.Contains("no safety filter", ev);

        (lane, ev) = MediaLaneClassifier.Classify(Array.Empty<string>(), "A general model.", "o/n");
        Assert.Equal(("standard", (string?)null), (lane, ev));
    }

    [Theory]
    [InlineData("This model does not generate NSFW content.")]
    [InlineData("NSFW content is filtered by the safety checker.")]
    [InlineData("We never ship NSFW outputs. Safe for work.")]
    [InlineData("Output is SFW only; no nsfw.")]
    public void Negated_mentions_are_not_evidence(string card) =>
        Assert.Equal("standard", MediaLaneClassifier.Classify(Array.Empty<string>(), card, "o/n").Lane);

    // ---- fake provider ----

    private static byte[] HeaderBytes(IEnumerable<string> tensorNames, string dtype = "BF16")
    {
        var sb = new StringBuilder("{");
        long off = 0;
        bool first = true;
        foreach (string n in tensorNames)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"\"{n}\":{{\"dtype\":\"{dtype}\",\"shape\":[4,8],\"data_offsets\":[{off},{off + 64}]}}");
            off += 64;
        }
        sb.Append('}');
        byte[] json = Encoding.UTF8.GetBytes(sb.ToString());
        return json;
    }

    private static readonly string[] ZImageTensors =
        { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w", "layers.0.w" };
    private static readonly string[] UnknownTensors = { "mystery.a", "mystery.b" };

    private sealed class FakeProvider : IMediaDiscoveryProvider
    {
        public Dictionary<string, List<MediaRepoSummary>> SearchByTag = new();
        public Dictionary<string, List<MediaRepoFile>> Trees = new();
        public Dictionary<string, string[]> HeaderTensors = new();
        public Dictionary<string, SafetensorsHeader> Headers = new();
        public Dictionary<string, string?> Cards = new();
        public string? ETag = "W/\"abc\"";
        public Exception? SearchFailure;
        public int SearchCalls, TreeCalls, HeaderCalls, CardCalls, NotModifiedServed;
        public List<string> TreeRepos = new();

        public Task<MediaSearchPage> SearchAsync(MediaSearchQuery query, string? knownETag, CancellationToken ct)
        {
            SearchCalls++;
            if (SearchFailure is not null) throw SearchFailure;
            if (knownETag is not null && knownETag == ETag) { NotModifiedServed++; return Task.FromResult(new MediaSearchPage(true, knownETag, Array.Empty<MediaRepoSummary>())); }
            List<MediaRepoSummary> repos = query.Sort == "downloads" && query.Filters.Length == 1 && SearchByTag.TryGetValue(query.PipelineTag, out var l) ? l : new();
            return Task.FromResult(new MediaSearchPage(false, ETag, repos));
        }

        public Task<IReadOnlyList<MediaRepoFile>> ListFilesAsync(string repoId, string revision, CancellationToken ct)
        {
            TreeCalls++; TreeRepos.Add(repoId);
            return Task.FromResult<IReadOnlyList<MediaRepoFile>>(Trees.GetValueOrDefault(repoId) ?? new List<MediaRepoFile>());
        }

        public Task<string?> GetModelCardAsync(string repoId, string revision, CancellationToken ct)
        { CardCalls++; return Task.FromResult(Cards.GetValueOrDefault(repoId)); }

        public Task<SafetensorsHeader> ReadSafetensorsHeaderAsync(string repoId, string revision, string path, long fileBytes, CancellationToken ct)
        {
            HeaderCalls++;
            if (Headers.TryGetValue(repoId + ":" + path, out SafetensorsHeader? header)
                || Headers.TryGetValue(repoId, out header)) return Task.FromResult(header);
            string[] names = HeaderTensors.GetValueOrDefault(repoId + ":" + path) ?? HeaderTensors.GetValueOrDefault(repoId) ?? UnknownTensors;
            byte[] json = HeaderBytes(names);
            byte[] prefix = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)json.Length);
            return Task.FromResult(HuggingFaceMediaDiscoveryProvider.ReadHeader(prefix, json, fileBytes));
        }
    }

    private static MediaRepoSummary Repo(string id, long downloads, string license = "apache-2.0", string gated = "false", char sha = 'a',
        long likes = 10, params string[] tags) => new()
    {
        Id = id, Sha = Rev(sha), Downloads = downloads, Likes = likes, License = license, Gated = gated,
        LastModifiedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), PipelineTag = "text-to-image", Tags = tags.ToList()
    };

    private static List<MediaRepoFile> OneFile(string name, long size = 10 * Gib, char sha = 'b') =>
        new() { new MediaRepoFile("split_files/diffusion_models/" + name, size, Sha(sha)) };

    private (MediaDiscoveryService Svc, FakeProvider Fake, MediaDiscoveryStore Store, Func<DateTime> Clock, Action<TimeSpan> Advance) Make(bool gaming = false)
    {
        DateTime t = Now;
        var fake = new FakeProvider();
        var store = new MediaDiscoveryStore(_dir);
        var svc = new MediaDiscoveryService(fake, store, MediaCompanionRegistry.Shipped, () => t, () => gaming, (_, _) => Task.CompletedTask);
        return (svc, fake, store, () => t, d => t += d);
    }

    private static FakeProvider Seed(FakeProvider f)
    {
        f.SearchByTag["text-to-image"] = new()
        {
            Repo("good/z-image-fine", 50_000, "apache-2.0", sha: 'a', likes: 100),
            Repo("gate/z-image-gated", 90_000, "apache-2.0", "manual", 'c'),
            Repo("nolic/z-image-nolic", 80_000, "", sha: 'd'),
            Repo("wild/Z-Image-Uncensored", 20_000, "mit", sha: 'e'),
            Repo("odd/mystery-model", 70_000, "mit", sha: 'f'),
            Repo("generic/named-model", 60_000, "mit", sha: '1')
        };
        f.Trees["good/z-image-fine"] = OneFile("z_fine_bf16.safetensors");
        f.Trees["wild/Z-Image-Uncensored"] = OneFile("z_wild_fp8.safetensors", 6 * Gib, 'c');
        f.Trees["odd/mystery-model"] = OneFile("mystery_bf16.safetensors");
        f.Trees["generic/named-model"] = new() { new MediaRepoFile("model.safetensors", 10 * Gib, Sha('d')) };
        f.HeaderTensors["good/z-image-fine"] = ZImageTensors;
        f.HeaderTensors["wild/Z-Image-Uncensored"] = ZImageTensors;
        f.Cards["good/z-image-fine"] = "A fine general model.";
        return f;
    }

    [Fact]
    public async Task Refresh_keeps_only_runnable_ungated_licensed_repositories_and_records_why_others_were_refused()
    {
        var (svc, fake, store, _, _) = Make();
        Seed(fake);
        MediaDiscoveryRefreshResultDto r = await svc.RefreshAsync(false, default);

        Assert.Equal("ok", r.Outcome);
        MediaDiscoveryCatalog cat = store.Load();
        Assert.Equal(2, cat.Candidates.Count);
        MediaDiscoveredCandidate fine = cat.Candidates.Single(c => c.RepoId == "good/z-image-fine");
        Assert.Equal("image.generate", fine.Capability);
        Assert.Equal("z-image", fine.RegistryFamilyId);
        Assert.Equal("standard", fine.Lane);
        Assert.Equal(Rev('a'), fine.Revision);
        Assert.Equal(Sha('b'), fine.Sha256);
        Assert.Equal(10 * Gib, fine.SizeBytes);
        Assert.Equal("bf16", fine.Precision);
        Assert.Equal(50_000, fine.Downloads);
        MediaDiscoveredCandidate wild = cat.Candidates.Single(c => c.RepoId == "wild/Z-Image-Uncensored");
        Assert.Equal("uncensored", wild.Lane);
        Assert.Contains("uncensored", wild.LaneEvidence);

        // Gated and unlicensed repositories never reached the tree/header calls.
        Assert.DoesNotContain("gate/z-image-gated", fake.TreeRepos);
        Assert.DoesNotContain("nolic/z-image-nolic", fake.TreeRepos);
        string Outcome(string id) => cat.Inspected.Single(i => i.RepoId == id).Outcome;
        Assert.Equal("rejected:gated", Outcome("gate/z-image-gated"));
        Assert.Equal("rejected:unknown_license", Outcome("nolic/z-image-nolic"));
        Assert.Equal("rejected:family_unidentified", Outcome("odd/mystery-model"));
        Assert.Equal("rejected:generic_or_unsafe_file_name", Outcome("generic/named-model"));
        Assert.Equal("accepted", Outcome("good/z-image-fine"));
        Assert.NotNull(cat.LastSuccessUtc);
        Assert.True(cat.NextRefreshUtc > Now);
    }

    [Fact]
    public async Task Popular_image_repositories_do_not_starve_live_video_discovery()
    {
        var (svc, fake, store, _, _) = Make();
        List<MediaRepoSummary> images = Enumerable.Range(0, 40)
            .Select(i => Repo($"image/model-{i:00}", 1_000_000 - i, sha: (char)('a' + i % 20)))
            .ToList();
        fake.SearchByTag["text-to-image"] = images;

        MediaRepoSummary video = Repo("video/ltx-video", 1, sha: '9');
        video.PipelineTag = "text-to-video";
        fake.SearchByTag["text-to-video"] = new() { video };
        fake.Trees[video.Id] = OneFile("ltx-video-2b.safetensors");
        fake.Headers[video.Id] = new SafetensorsHeader(1, 1, new[]
        {
            new SafetensorsTensor("model.diffusion_model.transformer_blocks.0.weight", "BF16", new long[] { 4, 8 }, 0, 64),
            new SafetensorsTensor("model.diffusion_model.patchify_proj.weight", "BF16", new long[] { 2048, 128 }, 64, 128),
            new SafetensorsTensor("model.diffusion_model.caption_projection.weight", "BF16", new long[] { 4, 8 }, 128, 192),
            new SafetensorsTensor("vae.decoder.weight", "BF16", new long[] { 4, 8 }, 192, 256)
        }, new Dictionary<string, string>());

        MediaDiscoveryRefreshResultDto result = await svc.RefreshAsync(true, default);

        Assert.Equal("ok", result.Outcome);
        Assert.Contains(video.Id, fake.TreeRepos);
        MediaDiscoveredCandidate discovered = Assert.Single(store.Load().Candidates, c => c.RepoId == video.Id);
        Assert.Equal("video.generate", discovered.Capability);
        Assert.Equal("ltx-video", discovered.RegistryFamilyId);
    }

    [Fact]
    public async Task Unchanged_repositories_are_not_inspected_again_and_searches_use_the_etag()
    {
        var (svc, fake, store, _, advance) = Make();
        Seed(fake);
        await svc.RefreshAsync(false, default);
        int trees = fake.TreeCalls, headers = fake.HeaderCalls;
        advance(MediaDiscoveryService.SuccessCooldown + TimeSpan.FromMinutes(1));
        MediaDiscoveryRefreshResultDto r = await svc.RefreshAsync(false, default);
        Assert.Equal("ok", r.Outcome);
        Assert.Equal(trees, fake.TreeCalls);
        Assert.Equal(headers, fake.HeaderCalls);
        Assert.True(fake.NotModifiedServed > 0);
        Assert.Equal(2, store.Load().Candidates.Count);
    }

    [Fact]
    public async Task A_new_commit_is_inspected_again_and_replaces_the_pinned_revision()
    {
        var (svc, fake, store, _, advance) = Make();
        Seed(fake);
        await svc.RefreshAsync(false, default);
        fake.ETag = "W/\"changed\"";
        fake.SearchByTag["text-to-image"][0].Sha = Rev('9');
        advance(MediaDiscoveryService.SuccessCooldown + TimeSpan.FromMinutes(1));
        await svc.RefreshAsync(false, default);
        Assert.Equal(Rev('9'), store.Load().Candidates.Single(c => c.RepoId == "good/z-image-fine").Revision);
    }

    [Fact]
    public async Task Offline_keeps_serving_the_last_catalogue_and_backs_off()
    {
        var (svc, fake, store, _, advance) = Make();
        Seed(fake);
        await svc.RefreshAsync(false, default);
        fake.SearchFailure = new MediaDiscoveryFetchException("offline");
        advance(MediaDiscoveryService.SuccessCooldown + TimeSpan.FromMinutes(1));
        MediaDiscoveryRefreshResultDto r = await svc.RefreshAsync(false, default);
        Assert.Equal("offline_cached", r.Outcome);
        Assert.Equal("offline", r.ErrorCode);
        Assert.Equal(2, r.CandidateCount);
        Assert.Equal(2, store.Load().Candidates.Count);
        Assert.Equal("offline", store.Load().LastErrorCode);
        Assert.Equal("offline_cached", MediaDiscoveryLiveStatus(store));
    }

    private static string MediaDiscoveryLiveStatus(MediaDiscoveryStore store)
    {
        MediaDiscoveryLive.DirectoryOverride = Path.GetDirectoryName(store.FilePath);
        try { return MediaDiscoveryLive.Status().Status; } finally { MediaDiscoveryLive.DirectoryOverride = null; }
    }

    [Fact]
    public async Task Gaming_skips_and_cooldown_prevents_hammering()
    {
        var gaming = Make(gaming: true);
        Seed(gaming.Fake);
        Assert.Equal("skipped_gaming", (await gaming.Svc.RefreshAsync(true, default)).Outcome);
        Assert.Equal(0, gaming.Fake.SearchCalls);

        var (svc, fake, _, _, advance) = Make();
        Seed(fake);
        await svc.RefreshAsync(false, default);
        int calls = fake.SearchCalls;
        Assert.Equal("cooldown", (await svc.RefreshAsync(false, default)).Outcome);
        advance(TimeSpan.FromMinutes(5));
        Assert.Equal("cooldown", (await svc.RefreshAsync(false, default)).Outcome); // automatic: next refresh is hours away
        Assert.Equal(calls, fake.SearchCalls);
        Assert.Equal("ok", (await svc.RefreshAsync(true, default)).Outcome); // an explicit user refresh may go earlier
    }

    [Fact]
    public async Task Corrupt_store_is_reported_not_reset()
    {
        var (svc, fake, store, _, _) = Make();
        Seed(fake);
        File.WriteAllText(store.FilePath, "{ nope");
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.RefreshAsync(true, default));
        Assert.Equal("{ nope", File.ReadAllText(store.FilePath));
    }

    // ---- merged catalogue, ranking, routing, proposals ----

    private MediaCatalogue MergedFor(Action<MediaDiscoveryCatalog> arrange)
    {
        var cat = new MediaDiscoveryCatalog();
        arrange(cat);
        return MediaDiscoveryCatalogue.Merge(MediaCatalogue.Shipped, cat, MediaCompanionRegistry.Shipped);
    }

    private static MediaDiscoveredCandidate Cand(string repo, string file, long downloads, long size = 10 * Gib, string lane = "standard",
        string? evidence = null, long likes = 5, string capability = "image.generate", string family = "z-image", DateTime? modified = null, char rev = 'a') => new()
    {
        Key = MediaDiscoveredCandidate.MakeKey(repo, Rev(rev), "split/" + file), RepoId = repo, Revision = Rev(rev), Path = "split/" + file, FileName = file,
        SizeBytes = size, Sha256 = Sha('7'), Capability = capability, Lane = lane, LaneEvidence = evidence, RegistryFamilyId = family, Precision = "bf16",
        ParametersBillion = 6, License = "apache-2.0", Downloads = downloads, Likes = likes, LastModifiedUtc = modified ?? Now,
        FirstSeenUtc = Now, LastSeenUtc = Now
    };

    private static MediaHardware Hw(double vram = 24, double ram = 64) => new((long)(vram * Gib), (long)(ram * Gib));

    private static MediaCatalogueResponse View(MediaCatalogue c, MediaHardware hw, params string[] installed) =>
        MediaCatalogueView.Build(c, hw, "test", "standard", new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase), _ => null, null);

    [Fact]
    public void Embedded_entries_win_over_a_discovered_one_with_the_same_file_or_path()
    {
        MediaCatalogue merged = MergedFor(c =>
        {
            c.Candidates.Add(Cand("rando/clone", "z_image_turbo_bf16.safetensors", 999_999)); // same file name as the curated one
            c.Candidates.Add(Cand("rando/new-one", "brand_new.safetensors", 10));
        });
        var all = merged.Families.SelectMany(f => f.Variants).ToList();
        Assert.DoesNotContain(all, v => v.Repository == "rando/clone");
        MediaCatalogueVariant added = Assert.Single(all, v => v.Repository == "rando/new-one");
        Assert.True(added.IsDiscovered);
        Assert.Equal(1, added.Tier);
        Assert.Equal(Rev('a'), added.Revision);
        Assert.Contains("/resolve/" + Rev('a') + "/", added.DownloadUrl);
        Assert.Equal(Sha('7'), added.Sha256);
        Assert.Equal(MediaCatalogue.Shipped.Families.Count + 1, merged.Families.Count);
    }

    [Fact]
    public void Best_proposed_is_fit_then_popularity_then_recency_and_never_too_large_or_installed()
    {
        MediaCatalogue merged = MergedFor(c =>
        {
            c.Candidates.Add(Cand("a/popular", "popular_bf16.safetensors", 901, size: 10 * Gib));
            c.Candidates.Add(Cand("b/less", "less_bf16.safetensors", 100, size: 10 * Gib));
            c.Candidates.Add(Cand("c/huge", "huge_bf16.safetensors", 5_000_000, size: 40 * Gib)); // too large on 24 GB / 64 GB? 40 GB spills; still ranked after fit
            c.Candidates.Add(Cand("f/wild", "wild_bf16.safetensors", 50, lane: "uncensored", evidence: "tag \"uncensored\""));
        });
        MediaCatalogueCapabilityDto cap = View(merged, Hw()).Capabilities.Single(c => c.CapabilityId == "image.generate");
        var best = cap.Entries.Where(e => e.IsBestProposed).ToList();
        Assert.Equal(2, best.Count); // one per lane
        MediaCatalogueEntryDto std = best.Single(e => e.Lane == "standard");
        Assert.Equal("popular_bf16.safetensors", std.FileName); // fits + most downloads (900, 5 likes) vs tie group; ties resolved below
        Assert.True(std.Discovered);
        Assert.Equal("discovered, not curated", std.SourceLabel);
        Assert.Equal(0, std.DeclaredTier);
        Assert.DoesNotContain("declared tier", std.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("f/wild", best.Single(e => e.Lane == "uncensored").Repository);
        Assert.Equal("tag \"uncensored\"", best.Single(e => e.Lane == "uncensored").LaneEvidence);
        Assert.False(cap.Entries.Single(e => e.Repository == "c/huge").IsBestProposed);
        Assert.All(cap.Entries.Where(e => !e.Discovered), e => Assert.False(e.IsBestProposed));

        // Between equal downloads and likes the more recent repository wins; an installed one is no longer a proposal.
        MediaCatalogue tie = MergedFor(c =>
        {
            c.Candidates.Add(Cand("d/tie-newer", "tie_new.safetensors", 900, modified: Now));
            c.Candidates.Add(Cand("e/tie-older", "tie_old.safetensors", 900, modified: Now.AddDays(-30)));
        });
        Assert.Equal("tie_new.safetensors", View(tie, Hw()).Capabilities.Single(c => c.CapabilityId == "image.generate").Entries.Single(e => e.IsBestProposed).FileName);
        Assert.Equal("tie_old.safetensors", View(tie, Hw(), "tie_new.safetensors").Capabilities.Single(c => c.CapabilityId == "image.generate").Entries.Single(e => e.IsBestProposed).FileName);
    }

    [Fact]
    public void A_better_fit_beats_popularity_and_too_large_is_never_proposed()
    {
        MediaCatalogue merged = MergedFor(c =>
        {
            c.Candidates.Add(Cand("big/popular", "big_pop.safetensors", 9_000_000, size: 28 * Gib)); // spills on 24 GB: offload
            c.Candidates.Add(Cand("small/fits", "small_fit.safetensors", 10, size: 8 * Gib));
        });
        Assert.Equal("small_fit.safetensors", View(merged, Hw(24, 64)).Capabilities.Single(c => c.CapabilityId == "image.generate")
            .Entries.Single(e => e.IsBestProposed).FileName);
        // On a 2 GB GPU nothing fits: no best-proposed claim at all.
        Assert.DoesNotContain(View(merged, Hw(2, 16)).Capabilities.SelectMany(c => c.Entries), e => e.IsBestProposed);
    }

    [Fact]
    public void Discovered_entries_expose_pinned_download_and_listed_sha_and_label()
    {
        MediaCatalogue merged = MergedFor(c => c.Candidates.Add(Cand("x/y", "xy_bf16.safetensors", 5)));
        MediaCatalogueEntryDto e = View(merged, Hw()).Capabilities.SelectMany(c => c.Entries).Single(x => x.Repository == "x/y");
        Assert.True(e.Downloadable);
        Assert.Equal("exact_listing", e.SizeBasis);
        Assert.Equal(10 * Gib, e.SizeBytes);
        Assert.Equal(Rev('a'), e.Revision);
        Assert.Equal(Sha('7'), e.Sha256);
        Assert.Equal("https://huggingface.co/x/y/resolve/" + Rev('a') + "/split/xy_bf16.safetensors", e.DownloadUrl);
        Assert.Contains(e.Evidence, s => s.Contains("not curated"));
    }

    [Fact]
    public void An_installed_discovered_model_is_routed_like_a_catalogue_variant_and_lanes_stay_separate()
    {
        MediaCatalogue merged = MergedFor(c =>
        {
            c.Candidates.Add(Cand("x/anime-z", "animez_bf16.safetensors", 5, lane: "standard"));
            c.Candidates.Add(Cand("w/wild-z", "wildz_bf16.safetensors", 5, lane: "uncensored", evidence: "model card says \"uncensored\""));
        });
        var none = new Func<string, double?>(_ => null);
        var installed = new HashSet<string>(new[] { "animez_bf16.safetensors", "wildz_bf16.safetensors" }, StringComparer.OrdinalIgnoreCase);

        MediaRouteResult std = MediaModelRouter.Route(merged, "image.generate", "a cat", "Standard", Hw(), installed, none, _ => true);
        Assert.Equal("ok", std.Code);
        Assert.Equal("animez_bf16.safetensors", std.ChosenFile);

        MediaRouteResult unc = MediaModelRouter.Route(merged, "image.generate", "a cat", "Uncensored", Hw(), installed, none, _ => true);
        Assert.Equal("wildz_bf16.safetensors", unc.ChosenFile);

        // Not installed: a discovered model is only a proposal, never routed.
        MediaRouteResult nothing = MediaModelRouter.Route(merged, "image.generate", "a cat", "Standard", Hw(), new HashSet<string>(), none, _ => true);
        Assert.Equal("no_installed_model", nothing.Code);
    }

    [Fact]
    public void Proposals_are_made_only_for_discovered_models_that_fit_better_and_never_download()
    {
        MediaCatalogue merged = MergedFor(c => c.Candidates.Add(Cand("x/y", "xy_bf16.safetensors", 1234, likes: 77)));
        Func<string, Func<string, double?>> speed = _ => _ => null;
        // The curated catalogue has proposals of its own for image.generate; covering the capability leaves only discovered gaps.
        var all = MediaDiscoveryProposals.Propose(merged, Hw(), new HashSet<string>(), speed, "standard", new HashSet<string>(), new HashSet<string>());
        MaintenanceProposal p = Assert.Single(all, x => x.Capability == "image.generate");
        Assert.Equal("media_candidate", p.Kind);
        Assert.Contains("Better image generation model available for this PC", p.Reason);
        Assert.Contains("1,234 downloads", p.Reason, StringComparison.Ordinal);
        Assert.Contains("discovered, not curated", p.Detail);
        Assert.Contains("nothing is downloaded", p.Detail);
        Assert.Equal("x/y", p.Repo);
        Assert.Equal(Rev('a'), p.Revision);

        Assert.Empty(MediaDiscoveryProposals.Propose(merged, Hw(), new HashSet<string>(), speed, "standard", new HashSet<string> { "image.generate" }, new HashSet<string>()));
        Assert.Empty(MediaDiscoveryProposals.Propose(merged, Hw(), new HashSet<string>(), speed, "uncensored", new HashSet<string>(), new HashSet<string>()));
        // Already installed: no proposal.
        Assert.DoesNotContain(MediaDiscoveryProposals.Propose(merged, Hw(), new HashSet<string> { "xy_bf16.safetensors" }, speed, "standard", new HashSet<string>(), new HashSet<string>()),
            x => x.Capability == "image.generate");
        // An installed model that already fits as well is not "better".
        var installedFits = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "z_image_turbo_nvfp4.safetensors" };
        Assert.DoesNotContain(MediaDiscoveryProposals.Propose(merged, Hw(), installedFits, speed, "standard", new HashSet<string>(), new HashSet<string>()),
            x => x.Capability == "image.generate");
    }

    // ---- effective catalogue + store ----

    [Fact]
    public void Live_catalogue_is_curated_only_without_a_file_and_degrades_on_corruption()
    {
        MediaDiscoveryLive.DirectoryOverride = _dir;
        try
        {
            Assert.Same(MediaCatalogue.Shipped, MediaDiscoveryLive.Effective());
            var store = new MediaDiscoveryStore(_dir);
            var cat = new MediaDiscoveryCatalog { LastSuccessUtc = Now };
            cat.Candidates.Add(Cand("x/y", "xy_bf16.safetensors", 5));
            store.Save(cat);
            Assert.Contains(MediaDiscoveryLive.Effective().Families.SelectMany(f => f.Variants), v => v.Repository == "x/y");
            File.WriteAllText(store.FilePath, "{ nope");
            Assert.Same(MediaCatalogue.Shipped, MediaDiscoveryLive.Effective());
        }
        finally { MediaDiscoveryLive.DirectoryOverride = null; }
    }

    [Fact]
    public void Store_rejects_uncensored_without_evidence_and_unknown_schema()
    {
        var store = new MediaDiscoveryStore(_dir);
        var cat = new MediaDiscoveryCatalog();
        cat.Candidates.Add(Cand("x/y", "xy_bf16.safetensors", 5, lane: "uncensored", evidence: null));
        Assert.Throws<InvalidDataException>(() => store.Save(cat));
        Assert.Throws<InvalidDataException>(() => store.Save(new MediaDiscoveryCatalog { SchemaVersion = 2 }));
    }

    // ---- real provider against a stub transport (still no network) ----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests.Add(request); return Task.FromResult(respond(request)); }
    }

    [Fact]
    public async Task Provider_reads_only_the_safetensors_header_with_two_range_requests_and_follows_a_hf_redirect()
    {
        byte[] json = HeaderBytes(ZImageTensors);
        byte[] prefix = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)json.Length);
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.Host == "huggingface.co")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://us.aws.cdn.hf.co/xet/abc");
                return redirect;
            }
            Assert.Equal("us.aws.cdn.hf.co", req.RequestUri.Host);
            var range = req.Headers.Range!.Ranges.Single();
            byte[] all = prefix.Concat(json).ToArray();
            byte[] slice = all.Skip((int)range.From!.Value).Take((int)(range.To!.Value - range.From.Value + 1)).ToArray();
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
        });
        var provider = new HuggingFaceMediaDiscoveryProvider(new HttpClient(handler), "https://huggingface.co");
        SafetensorsHeader header = await provider.ReadSafetensorsHeaderAsync("good/z-image-fine", Rev('a'), "split_files/z.safetensors", 10 * Gib, default);
        Assert.Equal("z-image", MediaFamilyDetector.Detect(header, MediaCompanionRegistry.Shipped).FamilyId);
        var ranges = handler.Requests.Where(r => r.Headers.Range != null).Select(r => r.Headers.Range!.Ranges.Single()).ToList();
        Assert.Equal(new long[] { 0, 8 }, ranges.Select(r => r.From!.Value).Distinct().ToArray());
        Assert.Equal(7, ranges[0].To);
        Assert.Contains(handler.Requests, r => r.RequestUri!.Host == "us.aws.cdn.hf.co");
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Provider_refuses_a_redirect_to_a_foreign_host_and_a_server_that_ignores_range()
    {
        var evil = new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/payload");
            return r;
        });
        var provider = new HuggingFaceMediaDiscoveryProvider(new HttpClient(evil), "https://huggingface.co");
        var ex = await Assert.ThrowsAsync<MediaDiscoveryFetchException>(() => provider.ReadSafetensorsHeaderAsync("a/b", Rev('a'), "x.safetensors", 1000, default));
        Assert.Equal("unsafe_redirect", ex.Code);

        var noRange = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) });
        var p2 = new HuggingFaceMediaDiscoveryProvider(new HttpClient(noRange), "https://huggingface.co");
        ex = await Assert.ThrowsAsync<MediaDiscoveryFetchException>(() => p2.ReadSafetensorsHeaderAsync("a/b", Rev('a'), "x.safetensors", 1000, default));
        Assert.Equal("range_not_supported", ex.Code);
    }

    [Fact]
    public async Task Provider_search_sends_conditional_request_and_follows_the_next_link_with_a_page_cap()
    {
        int page = 0;
        var handler = new StubHandler(req =>
        {
            if (req.Headers.TryGetValues("If-None-Match", out var inm) && inm.Single() == "W/\"v1\"" && !req.RequestUri!.Query.Contains("cursor"))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            page++;
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchJson) };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v" + page + "\"", true);
            resp.Headers.TryAddWithoutValidation("Link", "<https://huggingface.co/api/models?cursor=zzz>; rel=\"next\"");
            return resp;
        });
        var provider = new HuggingFaceMediaDiscoveryProvider(new HttpClient(handler), "https://huggingface.co");
        var q = new MediaSearchQuery("text-to-image", "downloads", new[] { "safetensors" }, 2);
        MediaSearchPage first = await provider.SearchAsync(q, null, default);
        Assert.False(first.NotModified);
        Assert.Equal(4, first.Repos.Count); // two pages, two valid rows each
        Assert.Equal(2, handler.Requests.Count); // hard page cap
        Assert.Equal("/api/models", handler.Requests[0].RequestUri!.AbsolutePath); // no doubled slash: the live API 404s on "//api"
        Assert.Contains("pipeline_tag=text-to-image", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("limit=50", handler.Requests[0].RequestUri!.Query);
        MediaSearchPage second = await provider.SearchAsync(q, "W/\"v1\"", default);
        Assert.True(second.NotModified);
    }

    [Fact]
    public void Provider_rejects_an_unsafe_endpoint_override()
    {
        Assert.Throws<MediaDiscoveryFetchException>(() => new HuggingFaceMediaDiscoveryProvider(new HttpClient(), "http://example.com"));
        _ = new HuggingFaceMediaDiscoveryProvider(new HttpClient(), "http://127.0.0.1:5999");
    }

    // ---- maintenance gating ----

    private AutoMaintenanceService Maintenance(Func<CancellationToken, Task<MediaDiscoveryRefreshResultDto>> refresh, bool gaming, bool? battery, bool enabled = true)
    {
        var svc = new AutoMaintenanceService(new AutoMaintenanceStore(_dir), new AutoMaintenanceDependencies
        {
            Probe = _ => Task.FromException<HardwareSnapshot>(new InvalidOperationException("no probe in test")),
            ListInstalledModels = () => Array.Empty<ManagedModelInfo>(),
            AutoAssign = (_, _) => new RoleAutoAssignResponse(),
            HasRoleAssignment = () => true,
            RefreshDiscovery = _ => Task.FromResult(new ModelDiscoveryRefreshResult { Outcome = "refreshed" }),
            DiscoveryCandidates = () => Array.Empty<ModelDiscoveryCandidate>(),
            CheckRuntimeUpdates = _ => Task.FromResult<IReadOnlyList<RuntimeUpdateObservation>>(Array.Empty<RuntimeUpdateObservation>()),
            PendingManagedRuntimeUpdates = () => new List<(string, string)>(),
            IsGamingActive = () => gaming,
            UtcNow = () => Now,
            RefreshMediaDiscovery = refresh,
            IsOnBattery = () => battery,
            SuggestMedia = () => new[] { new MaintenanceProposal { Key = "k", Detail = "d", Capability = "image.generate" } }
        });
        if (!enabled) svc.UpdateSettings(false, null);
        return svc;
    }

    [Fact]
    public async Task Maintenance_refreshes_discovery_but_only_proposes_and_honours_the_gates()
    {
        int calls = 0;
        Task<MediaDiscoveryRefreshResultDto> Refresh(CancellationToken _) { calls++; return Task.FromResult(new MediaDiscoveryRefreshResultDto { Outcome = "ok" }); }

        MaintenanceReport ok = await Maintenance(Refresh, gaming: false, battery: false).RunAsync("interval", default);
        Assert.Equal(1, calls);
        Assert.Contains(ok.Proposals, p => p.Kind == "media_candidate");

        await Maintenance(Refresh, gaming: false, battery: true).RunAsync("interval", default);
        Assert.Equal(1, calls); // on battery: skipped
        await Maintenance(Refresh, gaming: false, battery: true).RunAsync("manual", default);
        Assert.Equal(2, calls); // the user asked for this check

        await Maintenance(Refresh, gaming: true, battery: false).RunAsync("manual", default);
        Assert.Equal(2, calls); // Gaming hard-off
        await Maintenance(Refresh, gaming: false, battery: false, enabled: false).RunAsync("interval", default);
        Assert.Equal(2, calls); // setting disabled
    }

    [Fact]
    public async Task Offline_discovery_never_fails_the_maintenance_pass()
    {
        var report = await Maintenance(_ => Task.FromResult(new MediaDiscoveryRefreshResultDto { Outcome = "offline_cached", ErrorCode = "offline" }), false, false)
            .RunAsync("interval", default);
        Assert.Contains(report.Changes, c => c.StartsWith("media_discovery: offline_cached"));
        Assert.DoesNotContain(report.Errors, e => e.Contains("media_discovery"));
        var thrown = await Maintenance(_ => throw new HttpRequestException("boom"), false, false).RunAsync("interval", default);
        Assert.DoesNotContain(thrown.Errors, e => e.Contains("media_discovery"));
    }

    // ---- live smoke (read-only, off by default) ----

    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARUX_LIVE_HF_SMOKE") != "1")
                Skip = "Live Hugging Face smoke test; set PARUX_LIVE_HF_SMOKE=1 to run (read-only, a handful of GETs).";
        }
    }

    [LiveFact]
    public async Task Live_smoke_parses_real_hugging_face_json_and_reads_a_real_header()
    {
        var provider = new HuggingFaceMediaDiscoveryProvider();
        MediaSearchPage page = await provider.SearchAsync(new MediaSearchQuery("text-to-image", "downloads", new[] { "safetensors" }, 1), null, default);
        Assert.NotEmpty(page.Repos);
        Assert.All(page.Repos, r => Assert.False(string.IsNullOrEmpty(r.Id)));
        Console.WriteLine($"LIVE search: {page.Repos.Count} repos, etag={page.ETag}, first={page.Repos[0].Id} dl={page.Repos[0].Downloads} sha={page.Repos[0].Sha} lic={page.Repos[0].License}");

        const string repo = "Comfy-Org/Z-Image-Turbo", rev = "6fc90a3b1b653e935a0d175e260736de25b84df5";
        IReadOnlyList<MediaRepoFile> tree = await provider.ListFilesAsync(repo, rev, default);
        MediaRepoFile file = tree.Single(f => f.Path.EndsWith("z_image_turbo_nvfp4.safetensors"));
        Assert.Equal(4509509600, file.SizeBytes);
        Assert.Equal("a553c889dbcb910de4c98293237573219a37007c1074a3f04576646a088bd5c8", file.Sha256);
        SafetensorsHeader header = await provider.ReadSafetensorsHeaderAsync(repo, rev, file.Path, file.SizeBytes, default);
        MediaFamilyDetection det = MediaFamilyDetector.Detect(header, MediaCompanionRegistry.Shipped);
        Console.WriteLine($"LIVE header: {header.Tensors.Count} tensors, family={det.FamilyId}, precision={MediaDiscoveryService.DominantPrecision(header)}, params={MediaDiscoveryService.ParameterBillions(header)}B");
        Assert.Equal("z-image", det.FamilyId);
    }

    [LiveFact]
    public async Task Live_full_refresh_into_a_temp_store()
    {
        var store = new MediaDiscoveryStore(_dir);
        var svc = new MediaDiscoveryService(new HuggingFaceMediaDiscoveryProvider(), store, MediaCompanionRegistry.Shipped);
        MediaDiscoveryRefreshResultDto r = await svc.RefreshAsync(true, default);
        Console.WriteLine($"LIVE refresh: {r.Outcome} candidates={r.CandidateCount} inspected={r.RepositoriesInspected} rejected={r.RepositoriesRejected} error={r.ErrorCode}");
        MediaDiscoveryCatalog cat = store.Load();
        foreach (var o in cat.Inspected.GroupBy(i => i.Outcome).OrderByDescending(g => g.Count()))
            Console.WriteLine($"LIVE outcome {o.Key}: {o.Count()}");
        foreach (var c in cat.Candidates.OrderBy(c => c.Capability).ThenBy(c => c.Lane).ThenByDescending(c => c.Downloads))
            Console.WriteLine($"LIVE cand {c.Capability} {c.Lane} {c.RegistryFamilyId} {c.RepoId} {c.FileName} {c.SizeBytes / 1e9:0.0}GB {c.Precision} dl={c.Downloads} lic={c.License} ev={c.LaneEvidence}");
        MediaCatalogue merged = MediaDiscoveryCatalogue.Merge(MediaCatalogue.Shipped, cat, MediaCompanionRegistry.Shipped);
        MediaCatalogueResponse view = MediaCatalogueView.Build(merged, Hw(), "test", "standard", new HashSet<string>(), _ => null, null);
        foreach (var e in view.Capabilities.SelectMany(c => c.Entries).Where(e => e.IsBestProposed))
            Console.WriteLine($"LIVE best {e.CapabilityId} {e.Lane}: {e.Title} fit={e.FitClass}");
        Assert.Equal("ok", r.Outcome);
    }
}
