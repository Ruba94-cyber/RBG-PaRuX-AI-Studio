using System.Reflection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX reported its own prompt throughput 6.6x lower than the machine could actually do.
///
/// llama.cpp's prompt_per_second divides prompt tokens by the whole prefill, fixed start-up cost
/// included, so on a short prompt it reports latency wearing throughput's units. The chat
/// benchmark's prompt is 32 tokens. Measured on the physical PC against Qwen3.5-4B-Q4_K_M on the
/// Radeon 780M: 32 tokens reported 42.7 tok/s, 610 tokens 202.3, 2,410 tokens 280.8 and 7,210
/// tokens 283.7. The Runtime Manager printed "43 prompt tok/s" for a machine doing 284, and any
/// comparison drawn from that number was wrong by the same factor.
///
/// These tests pin the property rather than the figure: throughput is measured on a prompt long
/// enough that prefill dominates start-up. Hard-coding 284 would only encode this laptop.
/// </summary>
public sealed class PromptThroughputMeasurementTests
{
    private static (string System, string User) ThroughputPrompt()
    {
        MethodInfo build = typeof(LlamaServerPerformanceProbe)
            .GetMethod("BuildThroughputPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;
        object prompt = build.Invoke(null, null)!;
        return ((string)prompt.GetType().GetProperty("System")!.GetValue(prompt)!,
                (string)prompt.GetType().GetProperty("User")!.GetValue(prompt)!);
    }

    private static (string System, string User) WorkloadPrompt(string workload)
    {
        MethodInfo build = typeof(LlamaServerPerformanceProbe)
            .GetMethod("BuildPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;
        object prompt = build.Invoke(null, new object[] { workload, 0 })!;
        return ((string)prompt.GetType().GetProperty("System")!.GetValue(prompt)!,
                (string)prompt.GetType().GetProperty("User")!.GetValue(prompt)!);
    }

    [Fact]
    public void The_throughput_prompt_is_long_enough_for_prefill_to_dominate_start_up()
    {
        (string _, string user) = ThroughputPrompt();

        // The measured curve flattened between 2,410 and 7,210 tokens, so the probe is sized at
        // roughly 2,000. Counted in characters because the tokenizer is not available here; at the
        // measured ratio for this prose, 8,000 characters is comfortably past a thousand tokens.
        Assert.True(
            user.Length > 8_000,
            $"The throughput prompt is only {user.Length:N0} characters, short enough for start-up "
            + "cost to be measured as throughput again.");
    }

    [Fact]
    public void The_chat_workload_prompt_is_far_too_short_to_measure_throughput_with()
    {
        (string _, string chat) = WorkloadPrompt("chat");
        (string _, string throughput) = ThroughputPrompt();

        // This is the whole reason the two are separate. The workload prompt stays short because
        // time-to-first-token is what a person waiting on a chat actually feels.
        Assert.True(chat.Length < 200);
        Assert.True(throughput.Length > chat.Length * 40);
    }

    [Fact]
    public void The_throughput_probe_barely_decodes_because_only_the_prefill_is_being_measured()
    {
        MethodInfo run = typeof(LlamaServerPerformanceProbe)
            .GetMethod("RunCompletionAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        ParameterInfo maxTokens = run.GetParameters().Single(p => p.Name == "maxTokens");

        Assert.True(maxTokens.HasDefaultValue);
        Assert.Equal(192, maxTokens.DefaultValue);
    }
}
