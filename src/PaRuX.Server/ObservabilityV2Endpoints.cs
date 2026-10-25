using PaRuX.Contracts;

namespace PaRuX.Server;

public static class ObservabilityV2Endpoints
{
    public static void MapObservabilityV2Endpoints(this WebApplication app)
    {
        app.MapGet("/api/v2/observability/dashboard", (
            int? recentLimit, int? benchmarkLimit, ObservabilityService observability) =>
        {
            ObservabilityDashboardResponse dashboard = observability.GetDashboard(recentLimit ?? 30, benchmarkLimit ?? 12);
            dashboard.EngineRaces = EngineRaceEvidenceStore.Current.List(20).ToList();
            dashboard.Metrics.Add(new ObservabilityMetric
            {
                Id = "engine_races",
                Label = "Engine Race evidence",
                Value = dashboard.EngineRaces.Count.ToString(),
                Detail = dashboard.EngineRaces.Count == 0
                    ? "No persisted runtime/media race evidence yet"
                    : string.Join(" · ", dashboard.EngineRaces.GroupBy(item => item.Kind)
                        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.Key + " " + group.Count()))
            });

            AddMediaRaceMetrics(dashboard);

            foreach (EngineRaceEvidenceDto race in dashboard.EngineRaces.Take(20))
            {
                string winner = string.IsNullOrWhiteSpace(race.WinnerEngineId)
                    ? "no winner" : race.WinnerEngineId + (string.IsNullOrWhiteSpace(race.WinnerEngineVersion) ? string.Empty : "@" + race.WinnerEngineVersion);
                string candidateSummary = race.Kind == "media"
                    ? BuildMediaCandidateSummary(race.Candidates)
                    : string.Empty;
                dashboard.RecentActivity.Add(new TimelineEntry
                {
                    Category = "engine_race",
                    Title = race.Kind + " Engine Race · " + winner,
                    Detail = race.Status
                        + (string.IsNullOrWhiteSpace(race.SelectionReason) ? string.Empty : " · " + race.SelectionReason)
                        + (string.IsNullOrWhiteSpace(candidateSummary) ? string.Empty : " · " + candidateSummary),
                    RelatedId = race.Id,
                    Timestamp = race.MeasuredUtc
                });
            }
            dashboard.RecentActivity = dashboard.RecentActivity
                .OrderByDescending(item => item.Timestamp)
                .Take(Math.Clamp(recentLimit ?? 30, 5, 200))
                .ToList();
            return Results.Ok(dashboard);
        });

        app.MapGet("/api/v2/observability/engine-races", (int? limit) =>
            Results.Ok(EngineRaceEvidenceStore.Current.List(limit ?? 100)));
    }

    private static void AddMediaRaceMetrics(ObservabilityDashboardResponse dashboard)
    {
        List<EngineRaceEvidenceDto> media = dashboard.EngineRaces
            .Where(item => string.Equals(item.Kind, "media", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (media.Count == 0) return;

        List<EngineRaceCandidateEvidenceDto> candidates = media.SelectMany(item => item.Candidates).ToList();
        int completed = media.Count(item => string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase));
        int renderFailures = candidates.Count(item => !item.Success);
        int judgeFailures = candidates.Count(item => item.Success && item.QualityEvaluationSucceeded == false);
        List<double> acceptedScores = candidates
            .Where(item => item.Success && item.QualityAccepted == true && item.QualityScore.HasValue)
            .Select(item => item.QualityScore!.Value)
            .ToList();
        List<long> renderLatencies = candidates
            .Where(item => item.Success && item.ElapsedMs.HasValue && item.ElapsedMs.Value >= 0)
            .Select(item => item.ElapsedMs!.Value)
            .ToList();

        dashboard.Metrics.Add(new ObservabilityMetric
        {
            Id = "media_race_completion",
            Label = "Media race completion",
            Value = completed + "/" + media.Count,
            Detail = "Completed with a quality-approved winner in the persisted recent race window."
        });
        dashboard.Metrics.Add(new ObservabilityMetric
        {
            Id = "media_race_failures",
            Label = "Media race failures",
            Value = (renderFailures + judgeFailures).ToString(),
            Detail = $"Renderer failures {renderFailures} · independent judge failures {judgeFailures}; kept separate so judge outages never falsify renderer reliability."
        });
        dashboard.Metrics.Add(new ObservabilityMetric
        {
            Id = "media_race_quality",
            Label = "Media race accepted quality",
            Value = acceptedScores.Count == 0 ? "n/a" : acceptedScores.Average().ToString("0.000"),
            Detail = renderLatencies.Count == 0
                ? "No successful renderer latency evidence in the recent window."
                : $"Mean accepted combined quality score; mean cold render latency {renderLatencies.Average():0} ms across {renderLatencies.Count} successful candidates."
        });
    }

    private static string BuildMediaCandidateSummary(IEnumerable<EngineRaceCandidateEvidenceDto> candidates)
    {
        return string.Join(" | ", candidates.Take(4).Select(candidate =>
        {
            if (!candidate.Success)
                return candidate.EngineId + " render-failed";
            if (candidate.QualityEvaluationSucceeded == false)
                return candidate.EngineId + " judge-failed";
            string score = candidate.QualityScore.HasValue ? candidate.QualityScore.Value.ToString("0.###") : "n/a";
            string latency = candidate.ElapsedMs.HasValue ? candidate.ElapsedMs.Value + "ms" : "n/a";
            return candidate.EngineId + " q=" + score + " t=" + latency;
        }));
    }
}
