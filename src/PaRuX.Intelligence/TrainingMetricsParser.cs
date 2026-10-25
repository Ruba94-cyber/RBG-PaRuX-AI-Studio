using System.Globalization;
using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

/// <summary>
/// Reads a training progress line into the numbers a user watches while a fine-tune runs.
///
/// Hugging Face's Trainer logs a Python dict per logging step -
/// <c>{'loss': 1.234, 'grad_norm': 0.51, 'learning_rate': 0.0002, 'epoch': 0.5}</c> - and that line
/// is the only progress signal a subprocess run gives. PaRuX read <c>loss</c> out of it and nothing
/// else, so the two numbers that tell you whether a run is actually working were being thrown away:
/// grad_norm spiking is divergence, and learning_rate reaching zero is the schedule finishing.
///
/// Values are matched with or without surrounding quotes. Which of the two appears depends on the
/// TRL and transformers versions in the managed venv, and a parser that only handles one of them
/// silently reports no progress at all on the other - the failure looks like a hung job rather than
/// a parsing problem.
///
/// Every parse is invariant-culture. That is a real bug this codebase already hit: under an Italian
/// culture "." is a thousands separator, so a culture-sensitive parse read a loss of 10.98 as 1098.
/// </summary>
public static class TrainingMetricsParser
{
    private static Regex FieldPattern(string name) => new(
        "'" + name + @"':\s*'?(?<value>-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)'?",
        RegexOptions.Compiled);

    private static readonly Regex Loss = FieldPattern("loss");
    private static readonly Regex GradientNorm = FieldPattern("grad_norm");
    private static readonly Regex LearningRate = FieldPattern("learning_rate");
    private static readonly Regex Epoch = FieldPattern("epoch");

    /// <summary>One logging step. Any field the line did not carry stays null rather than becoming
    /// zero - a grad_norm of 0 and an absent grad_norm mean very different things on a chart.</summary>
    public sealed record Sample(double? Loss, double? GradientNorm, double? LearningRate, double? Epoch)
    {
        public bool HasAnything => Loss.HasValue || GradientNorm.HasValue || LearningRate.HasValue || Epoch.HasValue;
    }

    public static readonly Sample None = new(null, null, null, null);

    public static Sample Parse(string? logLine)
    {
        if (string.IsNullOrEmpty(logLine)) return None;
        return new Sample(
            Read(Loss, logLine),
            Read(GradientNorm, logLine),
            Read(LearningRate, logLine),
            Read(Epoch, logLine));
    }

    private static double? Read(Regex pattern, string line)
    {
        Match match = pattern.Match(line);
        return match.Success
            && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }
}
