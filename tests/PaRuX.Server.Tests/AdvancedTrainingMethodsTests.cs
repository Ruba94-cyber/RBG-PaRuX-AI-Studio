using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AdvancedTrainingMethodsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-advanced-training-" + Guid.NewGuid().ToString("N"));

    public AdvancedTrainingMethodsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Continued_pretraining_dataset_and_plan_are_recognized()
    {
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(Enumerable.Range(0, 100)
            .Select(i => "{\"text\":\"domain corpus " + i + "\"}"));
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(shape, TrainingMethod.ContinuedPretraining,
            "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", 16, 100, 5e-5, 24);

        Assert.True(shape.SupportsContinuedPretraining);
        Assert.True(plan.CanStart);
        Assert.True(plan.Recommended.LearningRate < 2e-4);
    }

    [Fact]
    public void Grpo_reference_rewards_require_reference_but_format_rewards_do_not()
    {
        string dataset = WriteJsonl("grpo.jsonl", Enumerable.Range(0, 60)
            .Select(i => "{\"prompt\":\"question " + i + "\"}"));
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDatasetForMethod(
            dataset, TrainingMethod.Grpo, GrpoRewardMode.ExactReference));

        TrainingLabService.ValidateDatasetForMethod(dataset, TrainingMethod.Grpo, GrpoRewardMode.JsonFormat);

        string referenceDataset = WriteJsonl("grpo-ref.jsonl", Enumerable.Range(0, 60)
            .Select(i => "{\"prompt\":\"question\",\"reference\":\"answer\"}"));
        TrainingLabService.ValidateDatasetForMethod(referenceDataset, TrainingMethod.Grpo, GrpoRewardMode.ContainsReference);
    }

    [Fact]
    public void Vision_dataset_requires_real_media_and_vision_model()
    {
        string image = Path.Combine(_root, "sample.png");
        File.WriteAllBytes(image, new byte[] { 1, 2, 3 });
        string dataset = WriteJsonl("vision.jsonl", Enumerable.Range(0, 60).Select(_ =>
            "{\"image\":\"sample.png\",\"messages\":[{\"role\":\"user\",\"content\":\"what?\"},{\"role\":\"assistant\",\"content\":\"answer\"}]}"));
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(dataset);
        Assert.True(shape.SupportsVisionFineTuning);
        Assert.Equal(0, shape.MissingMediaFiles);

        TrainingRunPlanner.RunPlan textModel = TrainingRunPlanner.Plan(shape, TrainingMethod.VisionSft,
            "unsloth/Qwen2.5-7B-Instruct-bnb-4bit", 16, 100, 1e-4, 48);
        TrainingRunPlanner.RunPlan visionModel = TrainingRunPlanner.Plan(shape, TrainingMethod.VisionSft,
            "unsloth/Qwen2.5-VL-7B-Instruct-bnb-4bit", 16, 100, 1e-4, 48);
        Assert.False(textModel.CanStart);
        Assert.True(visionModel.CanStart);
    }

    [Fact]
    public void Missing_media_is_a_real_dataset_blocker()
    {
        string dataset = WriteJsonl("missing-vision.jsonl", Enumerable.Range(0, 60).Select(_ =>
            "{\"image\":\"does-not-exist.png\",\"prompt\":\"q\",\"response\":\"a\"}"));
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(dataset);
        Assert.False(shape.SupportsVisionFineTuning);
        Assert.Equal(60, shape.MissingMediaFiles);
    }

    [Fact]
    public void Tts_dataset_is_detected_but_planner_and_runner_only_advertise_real_speecht5_adapter()
    {
        string audio = Path.Combine(_root, "sample.wav");
        File.WriteAllBytes(audio, new byte[] { 82, 73, 70, 70, 0, 0, 0, 0 });
        string dataset = WriteJsonl("tts.jsonl", Enumerable.Range(0, 60)
            .Select(_ => "{\"audio\":\"sample.wav\",\"text\":\"hello\"}"));
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(dataset);
        Assert.True(shape.SupportsTtsFineTuning);

        TrainingRunPlanner.RunPlan unsupported = TrainingRunPlanner.Plan(shape, TrainingMethod.TtsSft,
            "some/Parler-TTS", 16, 100, 2e-5, 48);
        TrainingRunPlanner.RunPlan supported = TrainingRunPlanner.Plan(shape, TrainingMethod.TtsSft,
            "microsoft/speecht5_tts", 16, 100, 2e-5, 48);
        Assert.False(unsupported.CanStart);
        Assert.True(supported.CanStart);
    }

    [Theory]
    [InlineData("sft")]
    [InlineData("dpo")]
    [InlineData("continued-pretraining")]
    [InlineData("grpo")]
    [InlineData("vision-sft")]
    [InlineData("tts-sft")]
    public void Every_declared_training_method_normalizes(string method)
    {
        Assert.Equal(method, TrainingLabService.NormalizeMethod(method));
    }

    [Fact]
    public void Unknown_training_method_is_rejected_not_silently_sft()
    {
        Assert.Throws<InvalidDataException>(() => TrainingLabService.NormalizeMethod("made-up"));
    }

    private string WriteJsonl(string name, IEnumerable<string> lines)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
