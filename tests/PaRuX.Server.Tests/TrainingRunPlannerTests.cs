using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The checks that run before a fine-tune starts.
///
/// Every case here is a way a run fails minutes in, for a reason that was knowable in milliseconds:
/// the file is the wrong shape for the method, the base model will not fit, the step count cannot
/// see the data. What is being pinned is the difference between blocking and warning - a wizard that
/// refuses on advice, or lets a certain failure through as advice, is one users learn to click past.
/// </summary>
public sealed class TrainingRunPlannerTests
{
    private static string ChatLine(string content) =>
        "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"" + content + "\"}]}";

    private static string PreferenceLine(string content) =>
        "{\"prompt\":\"q\",\"chosen\":\"" + content + "\",\"rejected\":\"no\"}";

    private static TrainingDatasetShape.Shape ChatDataset(int examples) =>
        TrainingDatasetShape.Inspect(Enumerable.Range(0, examples).Select(index => ChatLine("a" + index.ToString())));

    private static TrainingDatasetShape.Shape PreferenceDataset(int examples) =>
        TrainingDatasetShape.Inspect(Enumerable.Range(0, examples).Select(index => PreferenceLine("a" + index.ToString())));

    // ---- Dataset shape ----

    [Fact]
    public void A_chat_file_is_recognised_as_supervised_fine_tuning_material()
    {
        TrainingDatasetShape.Shape shape = ChatDataset(200);

        Assert.True(shape.SupportsSupervisedFineTuning);
        Assert.False(shape.SupportsPreferenceTuning);
        Assert.Equal(200, shape.ValidChatRecords);
        Assert.Contains("supervised fine-tuning", shape.Summary);
    }

    [Fact]
    public void A_preference_file_is_recognised_as_preference_tuning_material()
    {
        TrainingDatasetShape.Shape shape = PreferenceDataset(200);

        Assert.True(shape.SupportsPreferenceTuning);
        Assert.False(shape.SupportsSupervisedFineTuning);
        Assert.Contains("preference tuning", shape.Summary);
    }

    [Fact]
    public void A_preference_record_with_an_empty_rejected_field_does_not_count()
    {
        // The common export bug. Counting it would train the model towards nothing.
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(new[]
        {
            "{\"prompt\":\"q\",\"chosen\":\"good\",\"rejected\":\"\"}"
        });

        Assert.False(shape.SupportsPreferenceTuning);
        Assert.Equal(0, shape.ValidPreferenceRecords);
    }

    [Fact]
    public void Blank_lines_are_not_treated_as_broken_records()
    {
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(new[]
        {
            ChatLine("one"), "", "   ", ChatLine("two")
        });

        Assert.Equal(2, shape.LinesRead);
        Assert.Equal(0, shape.UnparseableLines);
        Assert.True(shape.SupportsSupervisedFineTuning);
    }

    [Fact]
    public void A_file_that_is_not_JSON_at_all_says_so_rather_than_reporting_zero_records()
    {
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(new[] { "role,content", "user,hello" });

        Assert.Equal(2, shape.LinesRead);
        Assert.Equal(2, shape.UnparseableLines);
        Assert.Contains("one JSON object per line", shape.Summary);
    }

    // ---- Method against dataset ----

    [Fact]
    public void Choosing_preference_tuning_for_a_chat_file_is_blocking_and_names_the_method_that_fits()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(500), "dpo", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 200, learningRate: 2e-4, availableGib: 24);

        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blocking, finding => finding.Field == "dataset");
        Assert.Contains(plan.Blocking, finding => finding.Message.Contains("supervised fine-tuning would accept it"));
    }

    [Fact]
    public void A_matching_dataset_and_method_can_start()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            PreferenceDataset(2_000), "dpo", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 750, learningRate: 2e-6, availableGib: 24);

        Assert.True(plan.CanStart);
        Assert.Empty(plan.Blocking);
    }

    // ---- Hardware ----

    [Fact]
    public void A_base_model_too_large_for_the_machine_is_blocking()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(1_000), "sft", "unsloth/Qwen2.5-72B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 300, learningRate: 2e-4, availableGib: 8);

        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blocking, finding => finding.Field == "baseModel");
        Assert.True(plan.EstimatedGib > plan.AvailableGib);
    }

    [Fact]
    public void An_unrecognisable_model_size_is_a_warning_that_it_was_not_checked_not_a_silent_pass()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(1_000), "sft", "someone/my-custom-model-4bit",
            loraRank: 16, maxSteps: 300, learningRate: 2e-4, availableGib: 8);

        Assert.True(plan.CanStart);
        Assert.Contains(plan.Warnings, finding => finding.Message.Contains("has not checked"));
    }

    [Theory]
    [InlineData("unsloth/Qwen2.5-7B-Instruct-bnb-4bit", 7.0)]
    [InlineData("meta-llama/Llama-3.2-1B", 1.0)]
    [InlineData("google/gemma-2-270m-it", 0.27)]
    [InlineData("unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", 0.5)]
    public void A_parameter_count_stated_in_the_model_name_is_read_from_it(string modelId, double expected)
    {
        double? parsed = TrainingRunPlanner.ParameterBillionsFrom(modelId);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value, 3);
    }

    [Fact]
    public void A_name_with_no_parameter_count_returns_null_rather_than_a_guess()
    {
        Assert.Null(TrainingRunPlanner.ParameterBillionsFrom("someone/my-model"));
        Assert.Null(TrainingRunPlanner.ParameterBillionsFrom(""));
    }

    // ---- Hyperparameters ----

    [Fact]
    public void Impossible_hyperparameters_are_blocking()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(500), "sft", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            loraRank: 0, maxSteps: 0, learningRate: 0, availableGib: 24);

        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blocking, finding => finding.Field == "loraRank");
        Assert.Contains(plan.Blocking, finding => finding.Field == "maxSteps");
        Assert.Contains(plan.Blocking, finding => finding.Field == "learningRate");
    }

    [Fact]
    public void A_step_count_that_cannot_see_the_data_once_is_a_warning_with_the_number_that_would()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(1_000), "sft", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 10, learningRate: 2e-4, availableGib: 24);

        // Runnable, just not useful - so it must not block.
        Assert.True(plan.CanStart);
        Assert.Contains(plan.Warnings, finding => finding.Field == "maxSteps");
    }

    [Fact]
    public void Recommendations_scale_with_the_dataset_rather_than_being_fixed_defaults()
    {
        TrainingRunPlanner.Recommendation small = TrainingRunPlanner.Recommend(ChatDataset(100), "sft");
        TrainingRunPlanner.Recommendation large = TrainingRunPlanner.Recommend(ChatDataset(1_500), "sft");

        Assert.True(large.LoraRank > small.LoraRank);
        Assert.True(large.MaxSteps > small.MaxSteps);
    }

    [Fact]
    public void Preference_tuning_is_recommended_at_a_lower_learning_rate_than_supervised()
    {
        TrainingRunPlanner.Recommendation supervised = TrainingRunPlanner.Recommend(ChatDataset(500), "sft");
        TrainingRunPlanner.Recommendation preference = TrainingRunPlanner.Recommend(PreferenceDataset(500), "dpo");

        Assert.True(preference.LearningRate < supervised.LearningRate);
    }

    [Fact]
    public void A_tiny_dataset_is_warned_about_but_not_refused()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(10), "sft", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            loraRank: 8, maxSteps: 60, learningRate: 2e-4, availableGib: 24);

        Assert.True(plan.CanStart);
        Assert.Contains(plan.Warnings, finding => finding.Message.Contains("memorise"));
    }

    [Fact]
    public void An_empty_dataset_is_blocking()
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            TrainingDatasetShape.Empty, "sft", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 60, learningRate: 2e-4, availableGib: 24);

        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blocking, finding => finding.Field == "dataset");
    }
}
