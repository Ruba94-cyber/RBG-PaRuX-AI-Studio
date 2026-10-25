using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// "Fine-tune it on your data" is the advice local-AI tools most often give and most often should
/// not. It costs a user hours and a GPU they may not have, and the usual causes of a bad answer -
/// the prompt, the retrieved context, the quantization, the routing - are all faster to change and
/// easier to undo. So the advisor's default answer is no, and it has to be argued into yes by
/// evidence PaRuX actually measured. These tests pin that discipline, because it is exactly the
/// kind of restraint that erodes the first time somebody wants the feature to look impressive.
/// </summary>
public sealed class TrainingAdvisorTests
{
    private static readonly TrainingAdvisorService Advisor = new();

    private static TrainingAdvisorService.Evidence Measured(
        double decodeTps = 24,
        double ttft = 900,
        bool hasDraft = false,
        int passed = 0,
        int failed = 0) =>
        new(decodeTps, ttft, 88, 4800, 550, hasDraft, passed, failed);

    private static bool Has(TrainingAdviceReport report, string category) =>
        report.Advice.Any(item => item.Category == category);

    [Fact]
    public void With_no_measurement_at_all_the_only_advice_is_to_measure()
    {
        TrainingAdviceReport report = Advisor.Build("chat",
            new TrainingAdvisorService.Evidence(null, null, null, null, null, false, 0, 0));

        Assert.False(report.HasMeasurement);
        Assert.True(Has(report, "measure"));
        Assert.False(Has(report, "adapter-candidate"));
        Assert.False(Has(report, "speculative-decoding"));
    }

    [Fact]
    public void With_no_eval_history_it_refuses_to_recommend_fine_tuning_and_says_what_to_collect()
    {
        TrainingAdviceReport report = Advisor.Build("chat", Measured(passed: 0, failed: 0));

        Assert.True(Has(report, "evidence"));
        Assert.False(Has(report, "adapter-candidate"));
        TrainingAdvice advice = report.Advice.Single(item => item.Category == "evidence");
        Assert.Contains("Eval dataset", advice.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_handful_of_cases_is_not_enough_to_justify_training()
    {
        // Nine failures out of ten looks damning and means nothing at this size.
        TrainingAdviceReport report = Advisor.Build("chat", Measured(passed: 1, failed: 9));

        Assert.True(Has(report, "evidence"));
        Assert.False(Has(report, "adapter-candidate"));
    }

    [Fact]
    public void A_model_that_is_already_doing_the_task_is_explicitly_told_not_to_train()
    {
        TrainingAdviceReport report = Advisor.Build("chat", Measured(passed: 47, failed: 3));

        Assert.True(Has(report, "no-training"));
        Assert.False(Has(report, "adapter-candidate"));
    }

    [Fact]
    public void A_middling_pass_rate_is_sent_to_prompting_and_retrieval_first()
    {
        TrainingAdviceReport report = Advisor.Build("chat", Measured(passed: 40, failed: 10));

        Assert.True(Has(report, "prompt-first"));
        Assert.False(Has(report, "adapter-candidate"));
    }

    [Fact]
    public void Only_a_repeated_measured_weakness_gets_an_adapter_recommendation()
    {
        TrainingAdviceReport report = Advisor.Build("chat", Measured(passed: 20, failed: 30));

        Assert.True(Has(report, "adapter-candidate"));
        TrainingAdvice advice = report.Advice.Single(item => item.Category == "adapter-candidate");

        // Even here it insists on the cheaper fixes first, and on re-running the identical dataset.
        Assert.Contains("prompt", advice.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-run", advice.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30 of 50", advice.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Speculative_decoding_is_offered_when_there_is_no_draft_model_and_withdrawn_when_there_is()
    {
        Assert.True(Has(Advisor.Build("chat", Measured(hasDraft: false)), "speculative-decoding"));
        Assert.False(Has(Advisor.Build("chat", Measured(hasDraft: true)), "speculative-decoding"));
    }

    [Fact]
    public void A_machine_too_slow_to_benefit_is_not_sent_after_a_draft_model()
    {
        // Below the floor the draft's own decode cost eats the speed-up it buys.
        TrainingAdviceReport report = Advisor.Build("chat", Measured(decodeTps: 3));

        Assert.False(Has(report, "speculative-decoding"));
    }

    [Fact]
    public void Slow_first_token_is_reported_and_a_fast_one_is_not()
    {
        Assert.True(Has(Advisor.Build("chat", Measured(ttft: 4200)), "latency"));
        Assert.False(Has(Advisor.Build("chat", Measured(ttft: 900)), "latency"));
    }

    /// <summary>
    /// The trap this machine walks straight into: its library holds Qwen3-Embedding-0.6B and
    /// Qwen3-Reranker-0.6B beside Qwen3.5-4B. Same family, a fifth of the size, and neither can
    /// generate a single token - a heuristic that looked only at family and size would recommend
    /// one and produce a runtime that refuses to start.
    /// </summary>
    [Fact]
    public void An_embedding_or_reranker_model_is_never_offered_as_a_draft()
    {
        var library = new[]
        {
            ("Qwen3-Embedding-0.6B-Q8_0.gguf", 600_000_000L),
            ("qwen3-reranker-0.6b-q8_0.gguf", 600_000_000L)
        };

        Assert.Null(TrainingAdvisorService.FindDraftCandidate(
            "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888L, library));
    }

    [Fact]
    public void A_genuine_small_sibling_is_named_so_the_advice_can_be_acted_on()
    {
        var library = new[]
        {
            ("Qwen3.5-0.5B-Q4_K_M.gguf", 400_000_000L),
            ("Qwen3-Embedding-0.6B-Q8_0.gguf", 600_000_000L)
        };

        Assert.Equal("Qwen3.5-0.5B-Q4_K_M.gguf", TrainingAdvisorService.FindDraftCandidate(
            "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888L, library));
    }

    [Fact]
    public void A_draft_from_another_family_is_refused_because_the_tokenizer_would_not_match()
    {
        var library = new[] { ("Llama-3.2-1B-Q4_K_M.gguf", 700_000_000L) };

        Assert.Null(TrainingAdvisorService.FindDraftCandidate(
            "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888L, library));
    }

    [Fact]
    public void A_draft_that_is_not_much_cheaper_cannot_pay_for_itself_and_is_refused()
    {
        var library = new[] { ("Qwen3.5-3B-Q4_K_M.gguf", 2_000_000_000L) };

        Assert.Null(TrainingAdvisorService.FindDraftCandidate(
            "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888L, library));
    }

    [Fact]
    public void The_advice_says_plainly_when_the_library_has_nothing_that_fits()
    {
        TrainingAdviceReport report = Advisor.Build("chat", Measured());
        TrainingAdvice advice = report.Advice.Single(item => item.Category == "speculative-decoding");

        Assert.Contains("Nothing in your library fits", advice.Detail, StringComparison.Ordinal);
        Assert.Contains("No candidate in the library", advice.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_recommendation_carries_the_measurement_it_rests_on()
    {
        TrainingAdviceReport report = Advisor.Build("chat", Measured(passed: 20, failed: 30));

        Assert.NotEmpty(report.Advice);
        foreach (TrainingAdvice advice in report.Advice)
        {
            Assert.False(string.IsNullOrWhiteSpace(advice.Evidence),
                $"\"{advice.Title}\" asks the user to take it on faith.");
            Assert.False(string.IsNullOrWhiteSpace(advice.Title));
            Assert.False(string.IsNullOrWhiteSpace(advice.Detail));
        }
    }
}
