using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record MediaEngineRaceCandidate(
    string EngineId,
    string EngineVersion,
    Func<CancellationToken, Task<MediaAsset>> ExecuteAsync);

public sealed record MediaEngineQualityEvidence(
    bool Accepted,
    double Score,
    string Reason);

public sealed class MediaEngineRaceMeasurement
{
    public string EngineId { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long ElapsedMs { get; set; }
    public MediaAsset? Asset { get; set; }
    public bool QualityEvaluationSucceeded { get; set; }
    public long QualityEvaluationElapsedMs { get; set; }
    public bool QualityAccepted { get; set; }
    public double? QualityScore { get; set; }
    public string? QualityReason { get; set; }
    public string? QualityError { get; set; }
    public string? Error { get; set; }
    public string? FailureClass { get; set; }
}

public sealed class MediaEngineRaceResult
{
    public string Status { get; set; } = "completed";
    public string? WinnerEngineId { get; set; }
    public string? WinnerEngineVersion { get; set; }
    public string SelectionReason { get; set; } = string.Empty;
    public string? FallbackReason { get; set; }
    public MediaAsset? WinnerAsset { get; set; }
    public List<MediaEngineRaceMeasurement> Measurements { get; set; } = new();
}

/// <summary>
/// Engine-neutral media race coordinator. Renderers run sequentially because diffusion/video
/// engines share the workstation GPU and a parallel race would measure VRAM contention rather than
/// engine performance. Every render completes before quality judging so judge work cannot cool the
/// GPU between candidates. Quality is primary, but tiny score noise is treated as an equivalence
/// band; latency decides only among independently accepted outputs that are effectively equal.
/// Renderer and judge failures are separate evidence domains and media payload bytes are excluded
/// from the durable evidence store.
/// </summary>
public sealed class MediaEngineRaceService
{
    public const int MaximumCandidates = 4;
    public const double QualityTieTolerance = 0.01;

    public async Task<MediaEngineRaceResult> RunAsync(
        IReadOnlyList<MediaEngineRaceCandidate> candidates,
        Func<MediaEngineRaceCandidate, MediaAsset, CancellationToken, Task<MediaEngineQualityEvidence>> qualityEvaluator,
        CancellationToken cancellationToken,
        string? preferredEngineId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(qualityEvaluator);
        if (candidates.Count < 2)
            throw new InvalidDataException("Media Engine Race requires at least two candidates.");
        if (candidates.Count > MaximumCandidates)
            throw new InvalidDataException($"Media Engine Race is bounded to {MaximumCandidates} candidates.");
        if (candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.EngineId)
            || string.IsNullOrWhiteSpace(candidate.EngineVersion)
            || candidate.ExecuteAsync == null))
            throw new InvalidDataException("Every media race candidate requires engine id, version and an executor.");
        if (candidates.Select(candidate => candidate.EngineId + "@" + candidate.EngineVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count)
            throw new InvalidDataException("Media Engine Race candidates must have distinct engine identities.");

        var result = new MediaEngineRaceResult();
        var candidateByIdentity = new Dictionary<string, MediaEngineRaceCandidate>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: render every candidate with no judge work between candidates.
        foreach (MediaEngineRaceCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var measurement = new MediaEngineRaceMeasurement
            {
                EngineId = candidate.EngineId.Trim(),
                EngineVersion = candidate.EngineVersion.Trim()
            };
            candidateByIdentity[Identity(measurement.EngineId, measurement.EngineVersion)] = candidate;

            var renderWatch = Stopwatch.StartNew();
            try
            {
                MediaAsset asset = await candidate.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                renderWatch.Stop();
                ValidateAsset(asset);
                measurement.Success = true;
                measurement.ElapsedMs = Math.Max(0, renderWatch.ElapsedMilliseconds);
                measurement.Asset = asset;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                renderWatch.Stop();
                measurement.Success = false;
                measurement.ElapsedMs = Math.Max(0, renderWatch.ElapsedMilliseconds);
                measurement.Error = Trim(ex.Message, 500);
                measurement.FailureClass = MediaJobProvenanceFactory.Create(
                    measurement.EngineId, measurement.EngineVersion, null, failure: ex).FailureClass;
            }
            result.Measurements.Add(measurement);
        }

        // Phase 2: judge only after every renderer has relinquished its measured execution slot.
        foreach (MediaEngineRaceMeasurement measurement in result.Measurements.Where(item => item.Success && item.Asset != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaEngineRaceCandidate candidate = candidateByIdentity[Identity(measurement.EngineId, measurement.EngineVersion)];
            var qualityWatch = Stopwatch.StartNew();
            try
            {
                MediaEngineQualityEvidence quality = await qualityEvaluator(
                    candidate, measurement.Asset!, cancellationToken).ConfigureAwait(false);
                qualityWatch.Stop();
                measurement.QualityEvaluationSucceeded = true;
                measurement.QualityEvaluationElapsedMs = Math.Max(0, qualityWatch.ElapsedMilliseconds);
                measurement.QualityAccepted = quality.Accepted;
                measurement.QualityScore = double.IsFinite(quality.Score) ? quality.Score : null;
                measurement.QualityReason = Trim(quality.Reason, 500);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                qualityWatch.Stop();
                measurement.QualityEvaluationSucceeded = false;
                measurement.QualityEvaluationElapsedMs = Math.Max(0, qualityWatch.ElapsedMilliseconds);
                measurement.QualityError = Trim(ex.Message, 500);
            }
        }

        List<MediaEngineRaceMeasurement> eligible = result.Measurements
            .Where(item => item.Success
                && item.QualityEvaluationSucceeded
                && item.QualityAccepted
                && item.Asset != null
                && item.QualityScore.HasValue)
            .ToList();
        if (eligible.Count == 0)
        {
            bool anyRender = result.Measurements.Any(item => item.Success);
            bool anyJudge = result.Measurements.Any(item => item.Success && item.QualityEvaluationSucceeded);
            result.Status = !anyRender
                ? "failed"
                : !anyJudge
                    ? "quality-evaluation-failed"
                    : "no-promotable-winner";
            result.SelectionReason = result.Status switch
            {
                "failed" => "All media engine candidates failed to render a valid asset.",
                "quality-evaluation-failed" => "At least one engine rendered successfully, but no independent quality evaluation completed; renderer success was preserved and no unjudged winner was promoted.",
                _ => "At least one candidate rendered and was evaluated, but none passed independent quality acceptance; no speed-only promotion was allowed."
            };
            PersistEvidenceBestEffort(result);
            return result;
        }

        double bestQuality = eligible.Max(item => item.QualityScore!.Value);
        List<MediaEngineRaceMeasurement> qualityEquivalent = eligible
            .Where(item => bestQuality - item.QualityScore!.Value <= QualityTieTolerance)
            .ToList();
        MediaEngineRaceMeasurement winner = qualityEquivalent
            .OrderBy(item => item.ElapsedMs)
            .ThenByDescending(item => item.QualityScore!.Value)
            .First();

        result.WinnerEngineId = winner.EngineId;
        result.WinnerEngineVersion = winner.EngineVersion;
        result.WinnerAsset = winner.Asset;
        result.SelectionReason = qualityEquivalent.Count == 1
            ? $"Selected materially highest accepted quality score ({winner.QualityScore:0.###}); next accepted candidates were more than {QualityTieTolerance:0.###} lower. Render elapsed {winner.ElapsedMs} ms."
            : $"{qualityEquivalent.Count} candidates were within the {QualityTieTolerance:0.###} accepted-quality equivalence band of best score {bestQuality:0.###}; selected fastest cold render ({winner.ElapsedMs} ms, quality {winner.QualityScore:0.###}). Judge latency {winner.QualityEvaluationElapsedMs} ms is excluded from renderer latency.";
        result.FallbackReason = FallbackReasonFor(preferredEngineId, winner, result.SelectionReason);
        PersistEvidenceBestEffort(result);
        return result;
    }

    internal static string? FallbackReasonFor(
        string? preferredEngineId,
        MediaEngineRaceMeasurement winner,
        string selectionReason)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(selectionReason);
        return string.IsNullOrWhiteSpace(preferredEngineId)
            || string.Equals(winner.EngineId, preferredEngineId.Trim(), StringComparison.OrdinalIgnoreCase)
                ? null
                : selectionReason;
    }

    internal static void ValidateAsset(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.Base64Data))
            throw new InvalidDataException("Media engine returned an empty artifact payload.");
        if (string.IsNullOrWhiteSpace(asset.MediaType))
            throw new InvalidDataException("Media engine returned no media type.");
        try
        {
            if (Convert.FromBase64String(asset.Base64Data).Length == 0)
                throw new InvalidDataException("Media engine returned an empty decoded artifact.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Media engine returned invalid Base64 artifact data.");
        }
    }

    private static string Identity(string engineId, string engineVersion) => engineId + "\n" + engineVersion;

    private static void PersistEvidenceBestEffort(MediaEngineRaceResult result)
    {
        try { _ = EngineRaceEvidenceStore.Current.RecordMedia(result); }
        catch { }
    }

    private static string Trim(string? value, int maximum)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }
}
