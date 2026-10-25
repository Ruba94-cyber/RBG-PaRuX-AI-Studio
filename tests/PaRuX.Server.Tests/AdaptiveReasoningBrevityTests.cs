using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// An explicit request for brevity should not cost the user several hundred hidden reasoning
/// tokens before a one-line answer. Measured live on this machine, the same trivial prompt takes
/// ~35.7 s at standard depth and ~2.8 s with reasoning off, so this classification is the
/// difference between an answer that feels instant and one that does not.
///
/// The second half of this file is the more important half: brevity must never *cost* depth on a
/// genuinely hard task that merely asks for a short write-up.
/// </summary>
public sealed class AdaptiveReasoningBrevityTests
{
    [Theory]
    [InlineData("Reply with exactly one sentence about local inference.")]
    [InlineData("Give me a single sentence summary.")]
    [InlineData("Answer in one line.")]
    [InlineData("Answer in one word.")]
    [InlineData("Just tell me briefly what this does.")]
    [InlineData("In short, what is a GGUF file?")]
    [InlineData("tl;dr of this release?")]
    [InlineData("Is this supported, yes or no?")]
    [InlineData("Rispondi con una frase.")]
    [InlineData("Spiegamelo in breve.")]
    [InlineData("Dimmelo brevemente.")]
    [InlineData("Scrivilo in una riga.")]
    [InlineData("Rispondi con una parola.")]
    public void An_explicit_request_for_brevity_selects_the_fast_route(string prompt)
    {
        Assert.Equal("fast", AdaptiveReasoningPolicy.InferRole(prompt));

        AdaptiveReasoningPolicy.Decision decision =
            AdaptiveReasoningPolicy.Decide(demand: 0.5, role: AdaptiveReasoningPolicy.InferRole(prompt));
        Assert.Equal("minimal", decision.Effort);
        // 0, not a small non-zero budget: measured live, a small budget (256) truncates the model
        // mid-thought and the broken reasoning spills into the visible answer instead of a clean
        // response - it was both barely faster than full depth AND far less likely to actually
        // honor the brevity request (3/8 vs 7/8 compliant, paired samples). Thinking fully off was
        // both 13x faster and fully compliant (8/8).
        Assert.Equal(0, decision.Budget);
    }

    /// <summary>
    /// The ordering guarantee. A hard task that asks for a short answer is still a hard task: the
    /// word "briefly" must not strip its reasoning depth. Coding and reasoning signals are checked
    /// before brevity precisely so these keep their route.
    /// </summary>
    [Theory]
    [InlineData("Analyse this architecture and briefly summarise the tradeoffs.", "reasoning")]
    [InlineData("Research the options and give me a one line recommendation.", "reasoning")]
    [InlineData("Refactor this code, then explain in short what changed.", "coding")]
    [InlineData("Debug the failing test and answer in one sentence why it broke.", "coding")]
    [InlineData("Analizza l'architettura e rispondi in breve.", "reasoning")]
    public void Brevity_never_downgrades_a_genuinely_hard_task(string prompt, string expectedRole)
    {
        Assert.Equal(expectedRole, AdaptiveReasoningPolicy.InferRole(prompt));
    }

    /// <summary>Prompts with no brevity signal must not be classified as "fast" by InferRole -
    /// this change is only allowed to widen what counts as an explicit brevity request, not to
    /// reclassify ordinary prompts as brevity requests. (Their actual reasoning depth is a separate
    /// question, decided by Decide's demand tiers - see AdaptiveReasoningLowTierTests.)</summary>
    [Theory]
    [InlineData("What are the tradeoffs between Vulkan and CUDA for local inference?")]
    [InlineData("Write a haiku about GPUs.")]
    [InlineData("Summarise this document.")]
    public void Prompts_without_a_brevity_signal_are_unaffected(string prompt)
    {
        Assert.NotEqual("fast", AdaptiveReasoningPolicy.InferRole(prompt));
    }
}
