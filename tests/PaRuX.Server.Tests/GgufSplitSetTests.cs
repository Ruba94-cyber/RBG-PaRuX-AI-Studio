using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Split (multi-part) GGUF sets. A part on its own is never a model: the library lists a complete set
/// once, a partial set is hidden from runtimes, and a download plan refuses to start an incomplete set
/// before any byte moves. The plan is built from a fake Hugging Face listing and a fake resolve endpoint,
/// with no network and no real model files.
/// </summary>
public sealed class GgufSplitSetTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    private static GgufSplitSetGrouper.Entry Entry(string path, long size = 100) => new(path, size, null);

    [Fact]
    public void A_set_whose_every_index_is_present_is_still_incomplete_when_another_file_claims_a_part()
    {
        // Every index is present, but a second spelling of part 1 also exists, so the set cannot be trusted.
        var group = Assert.Single(GgufSplitSetGrouper.Group(new[]
        {
            Entry("m-00001-of-00002.gguf"), Entry("M-00001-of-00002.gguf"), Entry("m-00002-of-00002.gguf")
        }));

        Assert.False(group.Complete);
        Assert.Empty(group.MissingPartIndexes);
        Assert.Single(group.ConflictingPaths);
    }

    [Fact]
    public void Complete_three_part_set_groups_once_with_summed_size_and_the_quant_from_its_name()
    {
        var group = Assert.Single(GgufSplitSetGrouper.Group(new[]
        {
            Entry("qwen-Q4_K_M-00001-of-00003.gguf", 10),
            Entry("qwen-Q4_K_M-00002-of-00003.gguf", 20),
            Entry("qwen-Q4_K_M-00003-of-00003.gguf", 30)
        }));

        Assert.True(group.Complete);
        Assert.Equal(60, group.TotalBytes);
        Assert.Equal("Q4_K_M", group.Quantization);
        Assert.Equal(3, group.PartCount);
        Assert.Empty(group.MissingPartIndexes);
    }

    [Fact]
    public void A_missing_middle_part_makes_the_set_incomplete_and_names_the_index()
    {
        var group = Assert.Single(GgufSplitSetGrouper.Group(new[]
        {
            Entry("m-00001-of-00003.gguf"), Entry("m-00003-of-00003.gguf")
        }));

        Assert.False(group.Complete);
        Assert.Equal(new[] { 2 }, group.MissingPartIndexes);
    }

    [Fact]
    public void Projectors_and_single_files_are_never_counted_as_parts()
    {
        var group = Assert.Single(GgufSplitSetGrouper.Group(new[]
        {
            Entry("mmproj-m-00001-of-00002.gguf"),
            Entry("mmproj-F16.gguf"),
            Entry("single-Q8_0.gguf"),
            Entry("m-00001-of-00002.gguf"),
            Entry("m-00002-of-00002.gguf")
        }));

        Assert.Equal(2, group.Parts.Count);
        Assert.True(group.Complete);
    }

    [Fact]
    public void Two_files_claiming_the_same_index_are_a_conflict_and_the_set_is_not_complete()
    {
        // Same stem in two spellings of one folder: both claim index 1 of a two-part set.
        var group = Assert.Single(GgufSplitSetGrouper.Group(new[]
        {
            Entry("m-00001-of-00002.gguf"), Entry("M-00001-of-00002.gguf")
        }));

        Assert.False(group.Complete);
        Assert.Single(group.ConflictingPaths);
        Assert.Equal(new[] { 2 }, group.MissingPartIndexes);
    }

    [Fact]
    public void Several_quants_in_one_repository_split_into_separate_sets_by_folder()
    {
        var groups = GgufSplitSetGrouper.Group(new[]
        {
            Entry("Q4_K_M/model-00001-of-00002.gguf"), Entry("Q4_K_M/model-00002-of-00002.gguf"),
            Entry("Q8_0/model-00001-of-00002.gguf"), Entry("Q8_0/model-00002-of-00002.gguf")
        });

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "Q4_K_M", "Q8_0" }, groups.Select(g => g.Quantization).ToArray());
        Assert.All(groups, g => Assert.True(g.Complete));
    }

    [Fact]
    public void A_quant_after_the_part_index_still_names_the_set()
    {
        var group = Assert.Single(GgufSplitSetGrouper.Group(new[]
        {
            Entry("model-00001-of-00002.Q5_K_S.gguf"), Entry("model-00002-of-00002.Q5_K_S.gguf")
        }));

        Assert.Equal("Q5_K_S", group.Quantization);
        Assert.True(group.Complete);
    }

    [Fact]
    public void Plan_picks_the_named_quant_and_pins_every_part_to_the_commit()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("acme/split", Revision, new[]
        {
            Entry("Q4_K_M/m-00001-of-00002.gguf", 10), Entry("Q4_K_M/m-00002-of-00002.gguf", 20),
            Entry("Q8_0/m-00001-of-00002.gguf", 30), Entry("Q8_0/m-00002-of-00002.gguf", 40)
        }, "Q4_K_M");

        Assert.True(plan.Complete);
        Assert.Equal(30, plan.TotalBytes);
        Assert.Equal(new[] { "m-00001-of-00002.gguf", "m-00002-of-00002.gguf" }, plan.Parts.Select(p => p.FileName).ToArray());
        Assert.All(plan.Parts, p => Assert.StartsWith(
            "https://huggingface.co/acme/split/resolve/" + Revision + "/Q4_K_M/", p.DownloadUrl, StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_refuses_to_guess_between_quants_and_refuses_an_unknown_one()
    {
        var entries = new[]
        {
            Entry("Q4_K_M/m-00001-of-00002.gguf"), Entry("Q4_K_M/m-00002-of-00002.gguf"),
            Entry("Q8_0/m-00001-of-00002.gguf"), Entry("Q8_0/m-00002-of-00002.gguf")
        };

        Assert.Throws<InvalidDataException>(() => ModelDownloadPlanner.Plan("acme/split", Revision, entries, null));
        Assert.Throws<InvalidDataException>(() => ModelDownloadPlanner.Plan("acme/split", Revision, entries, "IQ2_XS"));
    }

    [Fact]
    public void Plan_selects_an_exact_set_when_a_repository_has_multiple_sets_with_one_quantization()
    {
        var entries = new[]
        {
            Entry("Q4_K_M/first-00001-of-00002.gguf"), Entry("Q4_K_M/first-00002-of-00002.gguf"),
            Entry("Q4_K_M/second-00001-of-00002.gguf"), Entry("Q4_K_M/second-00002-of-00002.gguf")
        };

        ModelDownloadPlan plan = ModelDownloadPlanner.Plan(
            "acme/split", Revision, entries, "Q4_K_M", setName: "second");

        Assert.Equal("second", plan.SetName);
        Assert.All(plan.Parts, part => Assert.Contains("second-", part.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_uses_directory_and_part_count_to_select_duplicate_stems_exactly()
    {
        var entries = new[]
        {
            Entry("Q4_K_M/first/model-00001-of-00002.gguf"), Entry("Q4_K_M/first/model-00002-of-00002.gguf"),
            Entry("Q4_K_M/second/model-00001-of-00002.gguf"), Entry("Q4_K_M/second/model-00002-of-00002.gguf"),
            Entry("Q4_K_M/second/model-00001-of-00003.gguf")
        };

        ModelDownloadPlan plan = ModelDownloadPlanner.Plan(
            "acme/split", Revision, entries, "Q4_K_M", setName: "model", directory: @"Q4_K_M\second", partCount: 2);

        Assert.Equal("Q4_K_M/second", plan.Directory);
        Assert.Equal(2, plan.PartCount);
        Assert.All(plan.Parts, part => Assert.StartsWith("Q4_K_M/second/", part.RepoFilePath, StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_without_a_commit_still_lists_parts_but_has_no_download_url()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("acme/split", null, new[]
        {
            Entry("m-00001-of-00002.gguf"), Entry("m-00002-of-00002.gguf")
        }, null);

        Assert.True(plan.Complete);
        Assert.Null(plan.Revision);
        Assert.All(plan.Parts, p => Assert.Equal(string.Empty, p.DownloadUrl));
    }

    [Fact]
    public void Library_lists_a_complete_set_once_and_hides_a_partial_set_from_runtimes()
    {
        using var library = new IsolatedLibrary();
        library.Write("m-00001-of-00002.gguf", 10);
        library.Write("m-00002-of-00002.gguf", 20);
        library.Write("partial-00001-of-00003.gguf", 5);
        library.Write("single-Q8_0.gguf", 7);
        var service = NewService(new FakeHandler());

        IReadOnlyList<ManagedModelInfo> runnable = service.ListManagedModels();
        ManagedModelInfo set = Assert.Single(runnable, m => m.FileName == "m-00001-of-00002.gguf");
        Assert.Equal(30, set.SizeBytes);
        Assert.NotNull(set.SplitSet);
        Assert.True(set.SplitSet!.Complete);
        Assert.Equal(2, set.SplitSet.PartFileNames.Count);
        Assert.Equal(2, runnable.Count);
        Assert.DoesNotContain(runnable, m => m.FileName.StartsWith("partial-", StringComparison.Ordinal));

        ManagedModelInfo partial = Assert.Single(service.ListManagedLibrary(), m => m.FileName.StartsWith("partial-", StringComparison.Ordinal));
        Assert.NotNull(partial.SplitSet);
        Assert.False(partial.SplitSet!.Complete);
        Assert.Equal(new[] { 2, 3 }, partial.SplitSet.MissingPartIndexes);
    }

    [Fact]
    public void A_partial_set_cannot_be_resolved_for_loading_but_can_be_removed_whole()
    {
        using var library = new IsolatedLibrary();
        library.Write("partial-00001-of-00003.gguf", 5);
        library.Write("partial-00002-of-00003.gguf", 5);
        var service = NewService(new FakeHandler());

        var refused = Assert.Throws<InvalidDataException>(() => ModelDownloadService.ResolveManagedModelPath("partial-00001-of-00003.gguf"));
        Assert.Contains("incomplete", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(service.DeleteManagedModel("partial-00001-of-00003.gguf"));
        Assert.False(File.Exists(Path.Combine(library.Root, "partial-00001-of-00003.gguf")));
        Assert.False(File.Exists(Path.Combine(library.Root, "partial-00002-of-00003.gguf")));
    }

    [Fact]
    public async Task Plan_reads_the_listing_and_start_refuses_an_incomplete_set_before_any_transfer()
    {
        using var library = new IsolatedLibrary();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/m-00001-of-00003.gguf", 10L, Sha(new byte[10])),
            ("Q4_K_M/m-00003-of-00003.gguf", 30L, null)
        });
        var handler = new FakeHandler();
        handler.OnSend = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
            ? Json(listing)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = NewService(handler);

        ModelDownloadPlan plan = await service.PlanModelDownloadAsync("acme/split", "Q4_K_M", CancellationToken.None);

        Assert.False(plan.Complete);
        Assert.Equal(new[] { 2 }, plan.MissingPartIndexes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task Complete_set_downloads_every_part_into_the_one_folder_and_lists_as_one_model()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", (long)first.Length, Sha(first)),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", (long)second.Length, Sha(second))
        });
        var handler = new FakeHandler();
        handler.OnSend = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/models/", StringComparison.Ordinal)) return Task.FromResult(Json(listing));
            byte[]? body = path.EndsWith("split-Q4_K_M-00001-of-00002.gguf", StringComparison.Ordinal) ? first
                : path.EndsWith("split-Q4_K_M-00002-of-00002.gguf", StringComparison.Ordinal) ? second
                : null;
            if (body == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Content.Headers.ContentLength = body.Length;
                return Task.FromResult(head);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        };
        var service = NewService(handler);

        IReadOnlyList<ModelDownloadStatus> started = await service.StartModelDownloadPlanAsync(
            "acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision);

        Assert.Equal(2, started.Count);
        foreach (ModelDownloadStatus status in started)
        {
            ModelDownloadStatus final = await WaitForTerminalAsync(service, status.Id);
            Assert.Equal("completed", final.Status);
        }
        Assert.Equal(first.Length, new FileInfo(Path.Combine(library.Root, "split-Q4_K_M-00001-of-00002.gguf")).Length);
        Assert.Equal(second.Length, new FileInfo(Path.Combine(library.Root, "split-Q4_K_M-00002-of-00002.gguf")).Length);

        ManagedModelInfo model = Assert.Single(service.ListManagedModels());
        Assert.Equal("split-Q4_K_M-00001-of-00002.gguf", model.FileName);
        Assert.Equal(first.Length + second.Length, model.SizeBytes);
        Assert.Equal("Q4_K_M", model.Quantization);
    }

    [Fact]
    public async Task Complete_set_fires_the_completion_callback_once_for_its_first_part_after_the_last_part_lands()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", (long)first.Length, Sha(first)),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", (long)second.Length, Sha(second))
        });
        var completions = new RecordingCompletionHandler();
        var service = new ModelDownloadService(new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: true)),
            new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: true)), completions);

        IReadOnlyList<ModelDownloadStatus> started = await service.StartModelDownloadPlanAsync(
            "acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision);
        foreach (ModelDownloadStatus status in started)
            Assert.Equal("completed", (await WaitForTerminalAsync(service, status.Id)).Status);

        // The set is one model: one callback, naming the first part, which is the path a runtime opens.
        (string Path, string Label) call = Assert.Single(completions.Calls);
        Assert.Equal(Path.Combine(library.Root, "split-Q4_K_M-00001-of-00002.gguf"), call.Path);
        Assert.Equal("split-Q4_K_M", call.Label);
    }

    [Fact]
    public async Task A_set_with_a_part_that_never_lands_never_fires_the_completion_callback()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", (long)first.Length, Sha(first)),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", (long)second.Length, Sha(second))
        });
        var completions = new RecordingCompletionHandler();
        var service = new ModelDownloadService(new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: false)),
            new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: false)), completions);

        IReadOnlyList<ModelDownloadStatus> started = await service.StartModelDownloadPlanAsync(
            "acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision);
        var finals = new List<ModelDownloadStatus>();
        foreach (ModelDownloadStatus status in started) finals.Add(await WaitForTerminalAsync(service, status.Id));

        // The first part landed, the second never did: the set is not whole, so no model is announced.
        Assert.Single(finals, f => f.Status == "completed");
        Assert.Single(finals, f => f.Status == "failed");
        Assert.Empty(completions.Calls);
    }

    [Fact]
    public async Task Resuming_a_partly_landed_set_after_service_restart_recomputes_completion_and_calls_once()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", (long)first.Length, Sha(first)),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", (long)second.Length, Sha(second))
        });

        // Simulate the first Core session landing part 1 before it is restarted.
        library.Write("split-Q4_K_M-00001-of-00002.gguf", first.Length);
        var completions = new RecordingCompletionHandler();
        var restartedService = new ModelDownloadService(
            new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: true)),
            new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: true)), completions);

        IReadOnlyList<ModelDownloadStatus> started = await restartedService.StartModelDownloadPlanAsync(
            "acme/split", "Q4_K_M", null, CancellationToken.None, "split-Q4_K_M", Revision);

        Assert.Single(started);
        Assert.Equal("completed", (await WaitForTerminalAsync(restartedService, started[0].Id)).Status);
        (string Path, string Label) call = Assert.Single(completions.Calls);
        Assert.Equal(Path.Combine(library.Root, "split-Q4_K_M-00001-of-00002.gguf"), call.Path);
        Assert.Equal("split-Q4_K_M", call.Label);
    }

    [Fact]
    public async Task Start_refuses_a_plan_when_the_repository_revision_changed_after_review()
    {
        using var library = new IsolatedLibrary();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", 10L, Sha(new byte[10])),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", 20L, Sha(new byte[20]))
        });
        var handler = new FakeHandler();
        handler.OnSend = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
            ? Json(listing)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = NewService(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.StartModelDownloadPlanAsync(
            "acme/split", "Q4_K_M", null, CancellationToken.None, "split-Q4_K_M", new string('b', 40)));

        Assert.Empty(service.List());
    }

    [Fact]
    public async Task Start_refuses_to_queue_when_no_reviewed_revision_is_supplied()
    {
        using var library = new IsolatedLibrary();
        var service = NewService(new FakeHandler());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.StartModelDownloadPlanAsync(
            "acme/split", "Q4_K_M", null, CancellationToken.None));

        Assert.Empty(service.List());
    }

    [Fact]
    public async Task Start_refuses_a_set_with_a_conflicting_file_before_any_transfer()
    {
        using var library = new IsolatedLibrary();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/m-00001-of-00002.gguf", 10L, null),
            ("Q4_K_M/M-00001-of-00002.gguf", 10L, null),
            ("Q4_K_M/m-00002-of-00002.gguf", 20L, null)
        });
        var handler = new FakeHandler();
        handler.OnSend = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
            ? Json(listing)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = NewService(handler);

        ModelDownloadPlan plan = await service.PlanModelDownloadAsync("acme/split", "Q4_K_M", CancellationToken.None);

        Assert.False(plan.Complete);
        Assert.Empty(plan.MissingPartIndexes);
        Assert.Single(plan.ConflictingPaths);
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision));
        Assert.Contains("conflicting", refused.Message, StringComparison.Ordinal);
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task A_split_part_started_through_the_generic_download_route_never_fires_the_single_model_callback()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        string listing = Listing(new (string Path, long Size, string? Sha)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", (long)first.Length, Sha(first)),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", (long)second.Length, Sha(second))
        });
        var completions = new RecordingCompletionHandler();
        var service = new ModelDownloadService(new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: true)),
            new HttpClient(SplitRepoHandler(listing, first, second, secondPartServed: true)), completions);

        // One part of a two-part set, started as an ordinary file. The second part never lands, so no model is announced.
        ModelDownloadStatus status = await service.CreateDownloadAsync(new ModelDownloadRequest
        {
            RepoId = "acme/split",
            Revision = Revision,
            FileName = "split-Q4_K_M-00001-of-00002.gguf",
            DownloadUrl = "https://huggingface.co/acme/split/resolve/" + Revision + "/Q4_K_M/split-Q4_K_M-00001-of-00002.gguf",
            SizeBytes = first.Length
        }, CancellationToken.None);

        Assert.Equal("completed", (await WaitForTerminalAsync(service, status.Id)).Status);
        Assert.Empty(completions.Calls);
    }

    /// <summary>Serves a two-part repository. The second part answers 404 when it is not served, so its transfer fails.</summary>
    private static FakeHandler SplitRepoHandler(string listing, byte[] first, byte[] second, bool secondPartServed)
    {
        var handler = new FakeHandler();
        handler.OnSend = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/models/", StringComparison.Ordinal)) return Task.FromResult(Json(listing));
            byte[]? body = path.EndsWith("split-Q4_K_M-00001-of-00002.gguf", StringComparison.Ordinal) ? first
                : path.EndsWith("split-Q4_K_M-00002-of-00002.gguf", StringComparison.Ordinal) && secondPartServed ? second
                : null;
            if (body == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Content.Headers.ContentLength = body.Length;
                return Task.FromResult(head);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        };
        return handler;
    }

    private sealed class RecordingCompletionHandler : IModelDownloadCompletionHandler
    {
        public List<(string Path, string Label)> Calls { get; } = new();

        public void OnGgufDownloaded(string modelPath, string label) => Calls.Add((modelPath, label));
    }

    private static ModelDownloadService NewService(FakeHandler handler) =>
        new(new HttpClient(handler), new HttpClient(handler));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Listing(IEnumerable<(string Path, long Size, string? Sha)> files) =>
        JsonSerializer.Serialize(new
        {
            id = "acme/split",
            sha = Revision,
            siblings = files.Select(f => new
            {
                rfilename = f.Path,
                size = f.Size,
                lfs = f.Sha == null ? null : new { size = f.Size, oid = f.Sha }
            })
        });

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status != null && status.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("Split-set part did not reach a terminal state.");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSend(request, cancellationToken);
    }

    /// <summary>A temporary managed-library folder. Each test gets its own, and the scope is restored on dispose.</summary>
    private sealed class IsolatedLibrary : IDisposable
    {
        private readonly IDisposable _scope;

        public IsolatedLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "parux-split-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _scope = ModelDownloadService.UseModelsDirectoryForTests(Root);
        }

        public string Root { get; }

        public void Write(string fileName, int bytes) => File.WriteAllBytes(Path.Combine(Root, fileName), new byte[bytes]);

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
