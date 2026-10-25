using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class EngineRaceEvidenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-engine-race-evidence-" + Guid.NewGuid().ToString("N"));

    public EngineRaceEvidenceStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Media_race_persists_measurements_without_media_payloads()
    {
        var store = new EngineRaceEvidenceStore(_root);
        var result = new MediaEngineRaceResult
        {
            Status = "completed",
            WinnerEngineId = "engine-b",
            WinnerEngineVersion = "2",
            SelectionReason = "quality won",
            WinnerAsset = new MediaAsset { Base64Data = "SHOULD-NOT-BE-PERSISTED", MediaType = "image/png" },
            Measurements = new List<MediaEngineRaceMeasurement>
            {
                new()
                {
                    EngineId = "engine-a", EngineVersion = "1", Success = true, ElapsedMs = 1000,
                    QualityEvaluationSucceeded = true, QualityEvaluationElapsedMs = 80,
                    QualityAccepted = true, QualityScore = 0.7, QualityReason = "accepted-a",
                    Asset = new MediaAsset { Base64Data = "AAA", MediaType = "image/png" }
                },
                new()
                {
                    EngineId = "engine-b", EngineVersion = "2", Success = true, ElapsedMs = 1300,
                    QualityEvaluationSucceeded = true, QualityEvaluationElapsedMs = 95,
                    QualityAccepted = true, QualityScore = 0.9, QualityReason = "accepted-b",
                    Asset = new MediaAsset { Base64Data = "BBB", MediaType = "image/png" }
                }
            }
        };

        EngineRaceEvidenceDto saved = store.RecordMedia(result);
        EngineRaceEvidenceDto loaded = Assert.Single(store.List());

        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("media", loaded.Kind);
        Assert.Equal("engine-b", loaded.WinnerEngineId);
        Assert.Equal(2, loaded.Candidates.Count);
        EngineRaceCandidateEvidenceDto winner = loaded.Candidates.Single(c => c.EngineId == "engine-b");
        Assert.Equal(0.9, winner.QualityScore);
        Assert.True(winner.QualityEvaluationSucceeded);
        Assert.Equal(95, winner.QualityEvaluationElapsedMs);
        Assert.Null(winner.GenerationError);
        Assert.Null(winner.QualityError);
        string compactJson = System.Text.Json.JsonSerializer.Serialize(loaded);
        Assert.DoesNotContain("SHOULD-NOT-BE-PERSISTED", compactJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AAA", compactJson, StringComparison.Ordinal);
        Assert.DoesNotContain("BBB", compactJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_race_keeps_generation_and_quality_failures_distinct()
    {
        var store = new EngineRaceEvidenceStore(_root);
        store.RecordMedia(new MediaEngineRaceResult
        {
            Status = "quality-evaluation-failed",
            Measurements = new List<MediaEngineRaceMeasurement>
            {
                new()
                {
                    EngineId = "rendered", EngineVersion = "1", Success = true,
                    QualityEvaluationSucceeded = false, QualityError = "judge offline"
                },
                new()
                {
                    EngineId = "broken", EngineVersion = "1", Success = false,
                    Error = "renderer crashed"
                }
            }
        });

        EngineRaceEvidenceDto loaded = Assert.Single(store.List());
        EngineRaceCandidateEvidenceDto rendered = loaded.Candidates.Single(c => c.EngineId == "rendered");
        EngineRaceCandidateEvidenceDto broken = loaded.Candidates.Single(c => c.EngineId == "broken");

        Assert.True(rendered.Success);
        Assert.False(rendered.QualityEvaluationSucceeded);
        Assert.Equal("judge offline", rendered.QualityError);
        Assert.Null(rendered.GenerationError);

        Assert.False(broken.Success);
        Assert.Equal("renderer crashed", broken.GenerationError);
        Assert.Null(broken.QualityError);
    }

    [Fact]
    public void Evidence_history_is_newest_first_and_limit_is_enforced()
    {
        var store = new EngineRaceEvidenceStore(_root);
        for (int i = 0; i < 4; i++)
        {
            store.RecordMedia(new MediaEngineRaceResult
            {
                Status = "completed",
                WinnerEngineId = "e" + i,
                Measurements = new List<MediaEngineRaceMeasurement>
                {
                    new()
                    {
                        EngineId = "e" + i, EngineVersion = "1", Success = true,
                        QualityEvaluationSucceeded = true, QualityAccepted = true, QualityScore = i
                    }
                }
            });
            Thread.Sleep(2);
        }

        IReadOnlyList<EngineRaceEvidenceDto> latest = store.List(2);
        Assert.Equal(2, latest.Count);
        Assert.Equal("e3", latest[0].WinnerEngineId);
        Assert.Equal("e2", latest[1].WinnerEngineId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
