using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingJobDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-training-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Latest_checkpoint_requires_matching_trainer_state_and_optimizer_state()
    {
        string output = Path.Combine(_root, "runs", new string('a', 32));
        CreateCheckpoint(output, 5, globalStep: 5, complete: true);
        CreateCheckpoint(output, 10, globalStep: 10, complete: false);
        CreateCheckpoint(output, 20, globalStep: 19, complete: true);

        string? selected = TrainingJobDurability.FindLatestCompleteCheckpoint(output);

        Assert.NotNull(selected);
        Assert.Equal("checkpoint-5", Path.GetFileName(selected));
    }

    [Fact]
    public void Journal_round_trip_preserves_request_status_and_recovery_fingerprints()
    {
        Directory.CreateDirectory(_root);
        string dataset = Path.Combine(_root, "train.jsonl");
        File.WriteAllText(dataset, "{\"text\":\"durable\"}\n");
        string id = Guid.NewGuid().ToString("N");
        var journal = new TrainingJobDurability.Journal
        {
            Request = new TrainingJobRequest
            {
                TrainJsonlPath = dataset,
                BaseModel = "local/test-model",
                Method = TrainingMethod.ContinuedPretraining,
                MaxSteps = 20
            },
            Status = new TrainingJobStatus
            {
                Id = id,
                Status = "running",
                Stage = "Training",
                Method = TrainingMethod.ContinuedPretraining,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                TotalSteps = 20
            },
            MaxWallClockMinutes = 60,
            DatasetSha256 = TrainingJobDurability.ComputeFileSha256(dataset),
            RuntimeFingerprint = new string('b', 64)
        };

        TrainingJobDurability.Save(_root, journal);
        TrainingJobDurability.LoadResult loaded = Assert.Single(TrainingJobDurability.LoadAll(_root));

        Assert.Null(loaded.Error);
        Assert.NotNull(loaded.Value);
        Assert.Equal(id, loaded.Value!.Status.Id);
        Assert.Equal(dataset, loaded.Value.Request.TrainJsonlPath);
        Assert.Equal(journal.DatasetSha256, loaded.Value.DatasetSha256);
        Assert.Equal(journal.RuntimeFingerprint, loaded.Value.RuntimeFingerprint);
    }

    [Fact]
    public void Dataset_fingerprint_changes_when_training_input_changes()
    {
        Directory.CreateDirectory(_root);
        string dataset = Path.Combine(_root, "train.jsonl");
        File.WriteAllText(dataset, "one");
        string first = TrainingJobDurability.ComputeFileSha256(dataset);
        File.WriteAllText(dataset, "two");
        string second = TrainingJobDurability.ComputeFileSha256(dataset);

        Assert.NotEqual(first, second);
    }

    private static void CreateCheckpoint(string output, int directoryStep, int globalStep, bool complete)
    {
        string checkpoint = Path.Combine(output, "checkpoint-" + directoryStep);
        Directory.CreateDirectory(checkpoint);
        File.WriteAllText(Path.Combine(checkpoint, "trainer_state.json"),
            "{\"global_step\":" + globalStep + "}");
        File.WriteAllText(Path.Combine(checkpoint, "adapter_model.safetensors"), "weights");
        File.WriteAllText(Path.Combine(checkpoint, "optimizer.pt"), "optimizer");
        if (complete) File.WriteAllText(Path.Combine(checkpoint, "scheduler.pt"), "scheduler");
    }
}
