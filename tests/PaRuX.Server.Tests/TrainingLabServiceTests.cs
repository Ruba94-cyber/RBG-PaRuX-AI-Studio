using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// 2026-08-27, DPO training: TrainingLabService.CreateJob's full path always reaches a real,
/// fixed, machine-wide VenvPython check (%LOCALAPPDATA%\PaRuX\ai-runtimes\training-lab\...) that
/// has no test-injectable override - exactly the class of hazard this codebase's own convention
/// (see ModelRolesTests/RuntimeManagerTests) already documents for real shared machine state, and
/// on a bare CI/dev-container machine (no Training Lab engine installed) it fails unconditionally
/// before ever reaching the new method-validation logic. NormalizeMethod and ValidateDpoJsonlShape
/// are therefore extracted as internal static pure/file-only functions specifically so this real,
/// valuable validation logic has genuine hermetic coverage.
/// </summary>
public sealed class TrainingLabServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-training-lab-tests-" + Guid.NewGuid().ToString("N"));

    public TrainingLabServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string content)
    {
        string path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData(null, "sft")]
    [InlineData("", "sft")]
    [InlineData("  ", "sft")]
    [InlineData("sft", "sft")]
    [InlineData("SFT", "sft")]
    [InlineData(" Sft ", "sft")]
    [InlineData("dpo", "dpo")]
    [InlineData("DPO", "dpo")]
    public void NormalizeMethod_defaults_to_sft_and_is_case_and_whitespace_tolerant(string? raw, string expected)
    {
        Assert.Equal(expected, TrainingLabService.NormalizeMethod(raw));
    }

    [Theory]
    [InlineData("ppo")]
    [InlineData("orpo")]
    [InlineData("gibberish")]
    public void NormalizeMethod_rejects_an_unknown_method(string raw)
    {
        Assert.Throws<InvalidDataException>(() => TrainingLabService.NormalizeMethod(raw));
    }

    [Fact]
    public void ValidateDpoJsonlShape_accepts_a_real_prompt_chosen_rejected_row()
    {
        string path = WriteFile("{\"prompt\":\"What is 2+2?\",\"chosen\":\"4\",\"rejected\":\"5\"}\n");
        TrainingLabService.ValidateDpoJsonlShape(path); // must not throw
    }

    [Fact]
    public void ValidateDpoJsonlShape_skips_leading_blank_lines()
    {
        string path = WriteFile("\n   \n{\"prompt\":\"p\",\"chosen\":\"c\",\"rejected\":\"r\"}\n");
        TrainingLabService.ValidateDpoJsonlShape(path); // must not throw
    }

    [Fact]
    public void ValidateDpoJsonlShape_rejects_chat_format_messages_meant_for_sft()
    {
        string path = WriteFile("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}\n");
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDpoJsonlShape(path));
    }

    [Theory]
    [InlineData("{\"chosen\":\"c\",\"rejected\":\"r\"}")] // missing prompt
    [InlineData("{\"prompt\":\"p\",\"rejected\":\"r\"}")] // missing chosen
    [InlineData("{\"prompt\":\"p\",\"chosen\":\"c\"}")] // missing rejected
    [InlineData("{\"prompt\":\"\",\"chosen\":\"c\",\"rejected\":\"r\"}")] // empty prompt
    [InlineData("{\"prompt\":\"p\",\"chosen\":123,\"rejected\":\"r\"}")] // chosen is not a string
    public void ValidateDpoJsonlShape_rejects_a_row_missing_any_required_field(string line)
    {
        string path = WriteFile(line + "\n");
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDpoJsonlShape(path));
    }

    [Fact]
    public void ValidateDpoJsonlShape_rejects_invalid_json()
    {
        string path = WriteFile("not json at all\n");
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDpoJsonlShape(path));
    }

    [Fact]
    public void ValidateDpoJsonlShape_rejects_a_file_with_no_rows()
    {
        string path = WriteFile("\n\n   \n");
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDpoJsonlShape(path));
    }

    // Physical-PC regression: a failed training job's PumpAsync used to read every stdout/stderr
    // line, discard whichever ones did not parse as a metrics/step sample, and leave the caller
    // with nothing but "Training process exited with code 1" - confirmed live on an iGPU-only
    // machine where Unsloth genuinely requires an accelerator torch cannot find, with the real
    // NotImplementedError traceback nowhere in the API response or on disk. RecentOutputBuffer is
    // the bounded tail that now backs the surfaced error; these tests cover its own ring-buffer
    // and formatting behavior without needing a real subprocess.
    [Fact]
    public void RecentOutputBuffer_returns_null_tail_when_nothing_was_recorded()
    {
        var buffer = new TrainingLabService.RecentOutputBuffer();
        Assert.Null(buffer.Tail());
    }

    [Fact]
    public void RecentOutputBuffer_joins_recorded_lines_in_order()
    {
        var buffer = new TrainingLabService.RecentOutputBuffer();
        buffer.Record("Traceback (most recent call last):");
        buffer.Record("  File \"parux_train.py\", line 114, in <module>");
        buffer.Record("NotImplementedError: Unsloth cannot find any torch accelerator? You need a GPU.");

        Assert.Equal(
            "Traceback (most recent call last):\n  File \"parux_train.py\", line 114, in <module>\nNotImplementedError: Unsloth cannot find any torch accelerator? You need a GPU.",
            buffer.Tail());
    }

    [Fact]
    public void RecentOutputBuffer_keeps_only_the_most_recent_lines_when_it_overflows()
    {
        var buffer = new TrainingLabService.RecentOutputBuffer();
        int total = TrainingLabService.RecentOutputBuffer.MaxLines + 10;
        for (int i = 0; i < total; i++) buffer.Record("line " + i);

        string[] tailLines = buffer.Tail()!.Split('\n');
        Assert.Equal(TrainingLabService.RecentOutputBuffer.MaxLines, tailLines.Length);
        Assert.Equal("line 10", tailLines[0]);
        Assert.Equal("line " + (total - 1), tailLines[^1]);
    }
}
