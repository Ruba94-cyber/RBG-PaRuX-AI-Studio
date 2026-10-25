using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Maps the catalogue plus this PC's facts to the Model Hub view data. Pure given its inputs; the live wiring is below.</summary>
internal static class MediaCatalogueView
{
    internal static MediaCatalogueResponse Build(
        MediaCatalogue catalogue, MediaHardware hw, string hardwareSummary, string lane,
        ISet<string> installedFiles, Func<string, double?> lastJobSeconds, Func<string, bool>? eligible = null)
    {
        var response = new MediaCatalogueResponse { CatalogueVersion = catalogue.Version, HardwareSummary = hardwareSummary };
        foreach (MediaCatalogueCapability cap in catalogue.Capabilities)
        {
            MediaSelection sel = MediaSelectionEngine.Select(catalogue, cap.Id, lane, hw, installedFiles, lastJobSeconds, eligible);
            var dto = new MediaCatalogueCapabilityDto
            {
                CapabilityId = cap.Id, Label = cap.Label,
                AutomaticExplanation = sel.Explanation, AutomaticFile = sel.Automatic?.Variant.FileName
            };
            foreach (MediaEvaluatedVariant v in sel.Ranked)
            {
                bool recommended = ReferenceEquals(v, sel.Recommended);
                bool downloadable = v.Family.IsSupported && v.Variant.ListingVerified && !v.Variant.Bundled && v.Variant.DownloadUrl is not null;
                var evidence = new List<string>
                {
                    $"fit: {v.Fit.Reason}",
                    $"size basis: {v.Variant.SizeBasis}",
                    "tier: declared by the catalogue, not measured",
                    $"listing verified: {(v.Variant.ListingVerified ? "yes" : "no")}",
                    $"lane: {v.Variant.Lane}" + (v.Variant.LaneEvidence is { } le ? $" ({le})" : string.Empty)
                };
                if (v.LastJobSeconds is { } s) evidence.Add($"last measured job on this PC: {s:0.#} s");
                dto.Entries.Add(new MediaCatalogueEntryDto
                {
                    VariantId = v.Variant.Id, FamilyId = v.Family.Id, Title = v.Variant.Title, CapabilityId = cap.Id,
                    Lane = v.Variant.Lane, Precision = v.Variant.Precision, Repository = v.Variant.Repository ?? string.Empty,
                    FileName = v.Variant.FileName, DownloadUrl = downloadable ? v.Variant.DownloadUrl! : string.Empty,
                    SizeBytes = v.Variant.SizeBytes ?? v.Fit.WeightBytes, SizeBasis = v.Variant.SizeBasis, License = v.Variant.License,
                    DeclaredTier = v.Variant.Tier, FitClass = v.Fit.Code, FitReason = v.Fit.Reason, EstimatedVramBytes = v.Fit.RequiredVramBytes,
                    Support = v.Family.Support, SupportReason = v.Family.Reason ?? string.Empty,
                    Installed = v.Installed, Downloadable = downloadable, IsRecommended = recommended,
                    UsedAutomatically = ReferenceEquals(v, sel.Automatic),
                    Reason = MediaSelectionEngine.ReasonFor(v, recommended), Evidence = evidence
                });
            }
            response.Capabilities.Add(dto);
        }
        return response;
    }
}

/// <summary>Live inputs for the catalogue view, the Automatic media pick and the maintenance proposals.</summary>
internal static class MediaCatalogueLive
{
    /// <summary>Test seam: replaces hardware detection.</summary>
    [ThreadStatic] private static MediaHardware? _hardware;
    internal static MediaHardware? HardwareOverride { get => _hardware; set => _hardware = value; }
    /// <summary>Test seam: replaces the benchmark store directory.</summary>
    [ThreadStatic] private static string? _benchDir;
    internal static string? BenchmarkDirectoryOverride { get => _benchDir; set => _benchDir = value; }

    internal const string MediaWorkloadPrefix = "media.";

    internal static (MediaHardware Hardware, string Summary, string Digest) DetectHardware()
    {
        GpuInventoryStatus? gpu = null;
        try { gpu = new GpuInventoryService().GetStatus(); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        MediaHardware hw = HardwareOverride ?? MediaHardware.From(gpu, ram);
        string summary = hw.VramBytes is { } v
            ? $"GPU memory {v / (1024.0 * 1024 * 1024):0.#} GB, system RAM {(hw.RamBytes ?? 0) / (1024.0 * 1024 * 1024):0.#} GB"
            : "GPU memory not detected";
        string digest = string.Empty;
        try { digest = LocalBenchmarkIdentity.HardwareDigest(gpu, ram, LocalBenchmarkIdentity.CpuLabel()); } catch (Exception) { }
        return (hw, summary, digest);
    }

    /// <summary>Last measured end-to-end job time per checkpoint file for a capability on THIS hardware, from the local
    /// benchmark history (workload "media.&lt;capability&gt;", LoadMs holds the job wall time). Absent when never measured.</summary>
    internal static Func<string, double?> SpeedLookup(string capability, string hardwareDigest)
    {
        Dictionary<string, double> latest = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var store = BenchmarkDirectoryOverride is { } d ? new LocalBenchmarkHistoryStore(d) : new LocalBenchmarkHistoryStore();
            foreach (LocalBenchmarkEntry e in store.List()
                .Where(e => e.Workload == MediaWorkloadPrefix + capability && e.HardwareDigest == hardwareDigest && e.LoadMs is > 0)
                .OrderBy(e => e.TimestampUtc))
                latest[e.ModelId] = e.LoadMs!.Value / 1000.0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return file => latest.TryGetValue(file, out double s) ? s : null;
    }

    internal static HashSet<string> InstalledFiles()
    {
        try { return new HashSet<string>(LocalAiPaths.ListDiffusionModelCheckpoints(), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static Func<string, bool>? EligibilityFor(string capability)
    {
        if (!MediaCapabilityRouting.IsMedia(capability)) return null;
        try
        {
            MediaRoutingFacts facts = (MediaCapabilityRouting.FactsOverride ?? MediaCapabilityRouting.LiveFacts)(MediaCapabilityRouting.TypeFor(capability));
            return file => MediaCapabilityRouting.IsEligible(file, facts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return _ => false; }
    }

    internal static MediaCatalogueResponse BuildResponse(string lane)
    {
        var (hw, summary, digest) = DetectHardware();
        HashSet<string> installed = InstalledFiles();
        var speeds = new Dictionary<string, Func<string, double?>>();
        Func<string, double?> Speed(string cap) => speeds.TryGetValue(cap, out var f) ? f : speeds[cap] = SpeedLookup(cap, digest);
        // Build per capability so each uses its own speed table.
        MediaCatalogueResponse response = new() { CatalogueVersion = MediaCatalogue.Shipped.Version, HardwareSummary = summary };
        foreach (MediaCatalogueCapability cap in MediaCatalogue.Shipped.Capabilities)
        {
            MediaCatalogueResponse one = MediaCatalogueView.Build(MediaCatalogue.Shipped, hw, summary, lane, installed, Speed(cap.Id), EligibilityFor(cap.Id));
            response.Capabilities.Add(one.Capabilities.First(c => c.CapabilityId == cap.Id));
        }
        return response;
    }
}

/// <summary>Automatic capability mode for media: picks the best installed, runnable, fitting catalogue model.</summary>
internal static class MediaAutomaticPicker
{
    /// <summary>Returns the checkpoint file Automatic would use, or null to keep the workflow default. Never throws.</summary>
    internal static MediaSelection? Pick(string capability, MediaRoutingFacts facts, string lane = "standard")
    {
        try
        {
            var (hw, _, digest) = MediaCatalogueLive.DetectHardware();
            var installed = new HashSet<string>(facts.InstalledFiles, StringComparer.OrdinalIgnoreCase);
            return MediaSelectionEngine.Select(MediaCatalogue.Shipped, capability, lane, hw, installed,
                MediaCatalogueLive.SpeedLookup(capability, digest),
                file => MediaCapabilityRouting.IsEligible(file, facts));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}

internal static class MediaCatalogueEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/media/catalogue", (string? lane) =>
        {
            string l = string.Equals(lane, "uncensored", StringComparison.OrdinalIgnoreCase) ? "uncensored" : "standard";
            try { return Results.Ok(MediaCatalogueLive.BuildResponse(l)); }
            catch (InvalidDataException) { return Results.Problem(detail: "The media catalogue could not be loaded.", statusCode: 500); }
        });
    }
}
