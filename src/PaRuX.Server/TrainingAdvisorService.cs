using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Recommends what to do next to make this PaRuX faster or more accurate, from what it has already
/// measured on this machine rather than from generic advice about local models.
///
/// Fine-tuning is where local-AI tools most often send people wrong. "Fine-tune it on your data" is
/// cheap to say, costs a user hours and a GPU they may not have, and usually fixes nothing: the
/// common causes of a bad answer are the prompt, the retrieved context, the quantization or the
/// routing, and every one of those is faster to change and easier to undo than a trained adapter.
/// So the default answer here is not to train, and the advisor has to be argued into it by
/// evidence PaRuX actually holds - repeated failures of the same kind, on the same task, that
/// survived a prompt change.
///
/// The rule this class is built around: every recommendation names the measurement behind it and
/// what would change the answer. A suggestion the user cannot check is a suggestion they have to
/// take on faith, and a local-first product has no business asking for that.
/// </summary>
public sealed class TrainingAdvisorService
{
    /// <summary>
    /// The evidence the advice is drawn from, passed in rather than fetched, so the reasoning is a
    /// pure function of measurements a test can state exactly and a user can check in the report.
    /// </summary>
    public sealed record Evidence(
        double? DecodeTokensPerSecond,
        double? TimeToFirstTokenMilliseconds,
        double? PromptCacheImprovementPercent,
        double? PromptCacheColdTtftMilliseconds,
        double? PromptCacheWarmTtftMilliseconds,
        bool HasDraftModel,
        int EvalPassCount,
        int EvalFailCount,
        string? DraftCandidateFileName = null);

    /// <summary>
    /// A model already in the library that could act as the draft for <paramref name="mainFileName"/>.
    ///
    /// Named rather than described, because "look for a 0.5B-1B GGUF" is the kind of advice that
    /// reads well and helps nobody. The rules are deliberately strict: same family prefix, because
    /// speculative decoding needs a shared tokenizer and a draft from another family will have
    /// almost every token rejected; and no more than a quarter the size, because a draft that is
    /// not far cheaper than the model verifying it cannot pay for itself. Embedding and reranker
    /// models are excluded outright - they are in the same family and cannot generate text at all,
    /// which is exactly the trap a size-and-name heuristic would fall into on this very machine.
    /// </summary>
    public static string? FindDraftCandidate(
        string? mainFileName, long mainSizeBytes, IEnumerable<(string FileName, long SizeBytes)> library)
    {
        if (string.IsNullOrWhiteSpace(mainFileName) || mainSizeBytes <= 0) return null;
        string family = FamilyPrefix(mainFileName);
        if (family.Length < 3) return null;

        return library
            .Where(entry => !string.Equals(entry.FileName, mainFileName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.SizeBytes > 0 && entry.SizeBytes * 4 <= mainSizeBytes)
            .Where(entry => CanGenerateText(entry.FileName))
            .Where(entry => string.Equals(FamilyPrefix(entry.FileName), family, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.SizeBytes)
            .Select(entry => entry.FileName)
            .FirstOrDefault();
    }

    /// <summary>The leading family token, e.g. "qwen3" from "Qwen3.5-4B-Q4_K_M.gguf".</summary>
    internal static string FamilyPrefix(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).ToLowerInvariant();
        int cut = name.IndexOfAny(new[] { '-', '_', '.' });
        return cut > 0 ? name[..cut] : name;
    }

    /// <summary>
    /// Embedding and reranker models share a family name with the chat model and cannot generate a
    /// single token. This machine's own library holds Qwen3-Embedding-0.6B and Qwen3-Reranker-0.6B
    /// beside Qwen3.5-4B, so a heuristic that looked only at family and size would have recommended
    /// one of them and produced a runtime that refuses to start.
    /// </summary>
    internal static bool CanGenerateText(string fileName)
    {
        string name = (fileName ?? string.Empty).ToLowerInvariant();
        return !name.Contains("embedding", StringComparison.Ordinal)
            && !name.Contains("embed", StringComparison.Ordinal)
            && !name.Contains("reranker", StringComparison.Ordinal)
            && !name.Contains("rerank", StringComparison.Ordinal);
    }

    /// <summary>Below this, a draft model's own cost eats the speed-up it buys.</summary>
    internal const double SpeculativeDecodeFloorTokensPerSecond = 8.0;

    /// <summary>A run this small cannot distinguish a real weakness from noise.</summary>
    internal const int MinimumCasesForATrainingSignal = 20;

    /// <summary>Below this pass rate, with enough cases, something is wrong that prompting may not fix.</summary>
    internal const double TrainingCandidatePassRate = 0.70;

    /// <summary>Above this, the model is doing the task; training would be chasing noise.</summary>
    internal const double NoTrainingNeededPassRate = 0.90;

    public TrainingAdviceReport Build(string workload, Evidence evidence)
    {
        var advice = new List<TrainingAdvice>();
        AddPerformanceAdvice(advice, evidence);
        AddAccuracyAdvice(advice, evidence);

        return new TrainingAdviceReport
        {
            Workload = workload,
            HasMeasurement = evidence.DecodeTokensPerSecond.HasValue,
            Advice = advice.OrderBy(item => item.Priority).ToList()
        };
    }

    /// <summary>
    /// Speed first, because it is the half that can be settled by measurement alone. Nothing here
    /// asks the user to train anything: these are runtime changes, reversible in one click, and
    /// they must be exhausted before an adapter is even discussed.
    /// </summary>
    private static void AddPerformanceAdvice(List<TrainingAdvice> advice, Evidence evidence)
    {
        if (evidence.DecodeTokensPerSecond is not { } decode)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 1,
                Category = "measure",
                Title = "Run Optimize for this PC first",
                Detail = "PaRuX has no measurement for this model on this hardware yet, so any advice "
                    + "about batch size, KV cache, GPU layout or training would be guesswork. The "
                    + "optimizer tries real profiles and keeps the one that actually won.",
                Evidence = "No stored profile for this model, runtime build and GPU."
            });
            return;
        }

        // Speculative decoding: a small draft model proposes tokens the big one verifies. It is the
        // largest decode win available without changing the model that answers, and it changes
        // nothing about the output - the main model still accepts or rejects every token.
        if (!evidence.HasDraftModel && decode >= SpeculativeDecodeFloorTokensPerSecond)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 2,
                Category = "speculative-decoding",
                Title = "Try a draft model for speculative decoding",
                Detail = evidence.DraftCandidateFileName is { Length: > 0 } candidate
                    ? "A much smaller model from the same family proposes tokens that this model "
                        + "verifies, so accepted tokens cost almost nothing, and the answer is "
                        + "unchanged because the main model still decides every token. \""
                        + candidate + "\" is already in your library and is the right shape for "
                        + "this - set it as the draft model in Runtime, then re-run Optimize for "
                        + "this PC. PaRuX keeps it only if the benchmark beats this baseline."
                    : "A much smaller model from the same family proposes tokens that this model "
                        + "verifies, so accepted tokens cost almost nothing, and the answer is "
                        + "unchanged because the main model still decides every token. Nothing in "
                        + "your library fits: a draft has to be the same family as the model "
                        + "answering, so it shares a tokenizer, and no more than a quarter its size, "
                        + "so it is cheap enough to pay for itself. Embedding and reranker models do "
                        + "not count - they cannot generate text.",
                Evidence = "Measured decode " + decode.ToString("0.0")
                    + " tok/s with no draft model configured."
                    + (evidence.DraftCandidateFileName is { Length: > 0 }
                        ? " Candidate found in the library." : " No candidate in the library.")
            });
        }

        if (evidence.PromptCacheImprovementPercent is > 25)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 5,
                Category = "prompt-cache",
                Title = "Prompt cache reuse is already paying for itself",
                Detail = "Keep it on. Follow-up turns in a conversation skip re-reading the part of "
                    + "the prompt that has not changed, which is why the second question in a chat "
                    + "answers so much faster than the first.",
                Evidence = "Cold time-to-first-token "
                    + evidence.PromptCacheColdTtftMilliseconds?.ToString("0") + " ms versus warm "
                    + evidence.PromptCacheWarmTtftMilliseconds?.ToString("0") + " ms - "
                    + evidence.PromptCacheImprovementPercent?.ToString("0") + "% better."
            });
        }

        if (evidence.TimeToFirstTokenMilliseconds is > 2500)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 3,
                Category = "latency",
                Title = "First token is slow enough to feel",
                Detail = "Time-to-first-token is what a person waiting on a chat actually notices. "
                    + "A smaller quantization, fewer parallel slots, or a shorter system prompt each "
                    + "move it more than training would.",
                Evidence = "Measured " + evidence.TimeToFirstTokenMilliseconds?.ToString("0")
                    + " ms to first token."
            });
        }
    }

    /// <summary>
    /// Accuracy, where the temptation to train lives. The order is deliberate: say what the evidence
    /// supports, and when it supports nothing, say what to collect rather than inventing a task.
    /// </summary>
    private static void AddAccuracyAdvice(List<TrainingAdvice> advice, Evidence evidence)
    {
        int judged = evidence.EvalPassCount + evidence.EvalFailCount;
        if (judged < MinimumCasesForATrainingSignal)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 4,
                Category = "evidence",
                Title = "Do not fine-tune yet - there is nothing to fine-tune against",
                Detail = "Build an Eval dataset in Lifecycle Lab from the answers this model actually "
                    + "got wrong for you, at least " + MinimumCasesForATrainingSignal + " cases of "
                    + "the same kind of task. Without it there is no way to tell whether training "
                    + "helped, and a fine-tune you cannot measure is one you cannot keep or undo "
                    + "with any confidence. Most gaps close with a better prompt or better retrieved "
                    + "context, both of which this dataset will also tell you.",
                Evidence = "No completed Eval run with at least "
                    + MinimumCasesForATrainingSignal + " judged cases."
            });
            return;
        }

        double passRate = (double)evidence.EvalPassCount / judged;

        if (passRate >= NoTrainingNeededPassRate)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 4,
                Category = "no-training",
                Title = "This model is already doing the task - do not train it",
                Detail = "At this pass rate a fine-tune would be chasing noise, and it would cost you "
                    + "the ability to swap models freely. Spend the effort on the cases that still "
                    + "fail: read them one at a time and check whether the prompt or the retrieved "
                    + "context is what is missing.",
                Evidence = evidence.EvalPassCount + " of " + judged + " cases passed ("
                    + (passRate * 100).ToString("0") + "%)."
            });
            return;
        }

        if (passRate < TrainingCandidatePassRate)
        {
            advice.Add(new TrainingAdvice
            {
                Priority = 4,
                Category = "adapter-candidate",
                Title = "A LoRA adapter is worth considering for this task",
                Detail = "The same dataset fails often enough that this may be a real gap rather than "
                    + "noise. Before training: re-run the dataset with a sharper prompt and with "
                    + "retrieval enabled, because either can close a gap this size and both are "
                    + "reversible. If the same cases still fail, train a LoRA adapter on those cases "
                    + "in Lifecycle Lab and re-run the identical dataset - keep the adapter only if "
                    + "the pass rate moves on the cases that were failing, not just overall.",
                Evidence = evidence.EvalFailCount + " of " + judged + " cases failed ("
                    + (passRate * 100).ToString("0") + "% pass)."
            });
            return;
        }

        advice.Add(new TrainingAdvice
        {
            Priority = 4,
            Category = "prompt-first",
            Title = "Close this gap with prompting or retrieval before training",
            Detail = "The pass rate is short of good but not low enough for training to be the "
                + "obvious answer. A gap this size usually comes from an instruction the model is "
                + "not being given, or a document it is not being shown.",
            Evidence = evidence.EvalPassCount + " of " + judged + " cases passed ("
                + (passRate * 100).ToString("0") + "%)."
        });
    }
}
