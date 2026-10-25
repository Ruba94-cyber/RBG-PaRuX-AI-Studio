using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Gradient accumulation exists to fit a batch that does not fit. Using it when the batch does fit
/// serialises work the GPU could do at once: measured on an RTX 5080 with the same Unsloth kernels
/// and the same four samples per optimizer step, 1x4 took 49.1 s where 4x1 took 18.5 s.
/// </summary>
public sealed class TrainingBatchShapeTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void A_card_with_room_batches_on_the_gpu_instead_of_accumulating()
    {
        (int microBatch, int accumulation) = TrainingLabService.PlanBatchShape(1024, 16 * Gb);

        Assert.Equal(4, microBatch);
        Assert.Equal(1, accumulation);
    }

    [Fact]
    public void A_small_card_still_accumulates_rather_than_failing_to_fit()
    {
        (int microBatch, int accumulation) = TrainingLabService.PlanBatchShape(1024, 1 * Gb);

        Assert.Equal(1, microBatch);
        Assert.Equal(4, accumulation);
    }

    [Fact]
    public void With_no_gpu_reported_the_conservative_shape_is_kept()
    {
        (int microBatch, int accumulation) = TrainingLabService.PlanBatchShape(1024, 0);

        Assert.Equal(1, microBatch);
        Assert.Equal(4, accumulation);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void The_effective_batch_never_changes_whatever_the_hardware(int sequenceLength)
    {
        foreach (long vram in new[] { 0, 1 * Gb, 8 * Gb, 16 * Gb, 48 * Gb })
        {
            (int microBatch, int accumulation) = TrainingLabService.PlanBatchShape(sequenceLength, vram);

            // Tuning for the GPU must change how the samples are grouped, never how many of them
            // land in one optimizer step - otherwise it silently changes the training itself.
            Assert.Equal(4, microBatch * accumulation);
            Assert.InRange(microBatch, 1, 4);
        }
    }

    [Fact]
    public void A_longer_sequence_lowers_the_micro_batch_before_it_runs_out_of_memory()
    {
        (int shortMicro, _) = TrainingLabService.PlanBatchShape(1024, 16 * Gb);
        (int longMicro, _) = TrainingLabService.PlanBatchShape(32768, 16 * Gb);

        Assert.True(longMicro <= shortMicro,
            $"A {32768}-token sequence asked for a larger micro-batch ({longMicro}) than a 1,024-token one ({shortMicro}).");
    }
}
