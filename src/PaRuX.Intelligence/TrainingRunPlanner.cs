namespace PaRuX.Intelligence;

/// <summary>
/// Preflight planner for every PaRuX-managed Training Lab method. It is deliberately conservative:
/// when model/dataset compatibility cannot be established, the wizard says so before allocating
/// VRAM instead of converting a Python traceback into the product's normal validation UX.
/// </summary>
public static class TrainingRunPlanner
{
    private const double GibPerBillionParameters4Bit = 1.1;
    private const double MinimumRunGib = 3.0;

    public enum Severity { Blocking, Warning }
    public sealed record Finding(Severity Severity, string Field, string Message);
    public sealed record Recommendation(int LoraRank, int MaxSteps, double LearningRate, string Rationale);
    public sealed record RunPlan(
        bool CanStart,
        string DetectedMethod,
        TrainingDatasetShape.Shape Dataset,
        Recommendation Recommended,
        IReadOnlyList<Finding> Findings,
        double EstimatedGib,
        double AvailableGib)
    {
        public IEnumerable<Finding> Blocking => Findings.Where(finding => finding.Severity == Severity.Blocking);
        public IEnumerable<Finding> Warnings => Findings.Where(finding => finding.Severity == Severity.Warning);
    }

    public static RunPlan Plan(
        TrainingDatasetShape.Shape dataset,
        string method,
        string baseModelId,
        int loraRank,
        int maxSteps,
        double learningRate,
        double availableGib)
    {
        var findings = new List<Finding>();
        string normalizedMethod = NormalizeMethod(method);
        dataset ??= TrainingDatasetShape.Empty;

        if (dataset.LinesRead == 0)
        {
            findings.Add(new Finding(Severity.Blocking, "dataset", "No readable training records were found in this file."));
        }
        else
        {
            bool supported = Supports(dataset, normalizedMethod);
            if (!supported)
            {
                string alternative = normalizedMethod == "dpo" && dataset.SupportsSupervisedFineTuning
                    ? " This file is chat-shaped, so supervised fine-tuning would accept it."
                    : normalizedMethod != "dpo" && dataset.SupportsPreferenceTuning
                        ? " This file is preference-shaped, so DPO preference tuning would accept it."
                        : string.Empty;
                findings.Add(new Finding(Severity.Blocking, "dataset",
                    dataset.Summary + alternative + " Required shape for " + FriendlyMethod(normalizedMethod) + ": " + RequiredShape(normalizedMethod)));
            }
            if (dataset.MissingMediaFiles > 0 && normalizedMethod is "vision-sft" or "tts-sft")
            {
                findings.Add(new Finding(Severity.Blocking, "dataset",
                    $"{dataset.MissingMediaFiles:N0} referenced media file(s) do not exist. Resolve media paths before training."));
            }
            if (dataset.UnparseableLines > 0)
            {
                findings.Add(new Finding(Severity.Warning, "dataset",
                    $"{dataset.UnparseableLines:N0} line(s) are not valid JSON and will be skipped"
                    + (dataset.FirstProblemLines.Count > 0 ? " - first at line " + dataset.FirstProblemLines[0].ToString("N0") + "." : ".")));
            }
            if (dataset.LinesRead < 50)
            {
                findings.Add(new Finding(Severity.Warning, "dataset",
                    $"Only {dataset.LinesRead:N0} examples were observed. The run can fit them, but it may memorise them rather than generalise; treat quality evidence as weak."));
            }
        }

        ValidateModelFamily(baseModelId, normalizedMethod, findings);

        double? parameters = ParameterBillionsFrom(baseModelId);
        double methodMultiplier = normalizedMethod switch
        {
            "continued-pretraining" => 1.25,
            "grpo" => 1.45,
            "vision-sft" => 1.55,
            "tts-sft" => 1.70,
            "dpo" => 1.15,
            _ => 1.0
        };
        double estimatedGib = parameters.HasValue
            ? Math.Max(MinimumRunGib, parameters.Value * GibPerBillionParameters4Bit * methodMultiplier)
            : 0;

        if (parameters.HasValue && availableGib > 0 && estimatedGib > availableGib)
        {
            findings.Add(new Finding(Severity.Blocking, "baseModel",
                $"{FriendlyMethod(normalizedMethod)} with a {parameters.Value:0.#}B base needs roughly {estimatedGib:0.#} GB, "
                + $"but PaRuX observed only {availableGib:0.#} GB available."));
        }
        else if (!parameters.HasValue)
        {
            findings.Add(new Finding(Severity.Warning, "baseModel",
                "PaRuX could not establish parameter count from the model id, so it has not checked whether this model fits; memory fit is unproven rather than assumed."));
        }

        if (normalizedMethod is not "tts-sft" && !string.IsNullOrWhiteSpace(baseModelId)
            && !baseModelId.Contains("bnb-4bit", StringComparison.OrdinalIgnoreCase)
            && !baseModelId.Contains("4bit", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding(Severity.Warning, "baseModel",
                "This model id does not look pre-quantized 4-bit. Actual training memory may be substantially higher."));
        }

        Recommendation recommended = Recommend(dataset, normalizedMethod);
        if (loraRank <= 0)
            findings.Add(new Finding(Severity.Blocking, "loraRank", "LoRA rank must be at least 1."));
        else if (loraRank > 128)
            findings.Add(new Finding(Severity.Warning, "loraRank", "Rank above 128 is unusually expensive and rarely justified by local fine-tuning evidence."));

        if (maxSteps <= 0)
            findings.Add(new Finding(Severity.Blocking, "maxSteps", "The number of steps must be at least 1."));
        else if (dataset.LinesRead > 0 && maxSteps < Math.Max(1, dataset.LinesRead / 8))
            findings.Add(new Finding(Severity.Warning, "maxSteps",
                $"{maxSteps:N0} steps will not cover most of {dataset.LinesRead:N0} examples at the default effective batch; about {recommended.MaxSteps:N0} is a better starting point."));

        if (learningRate <= 0)
            findings.Add(new Finding(Severity.Blocking, "learningRate", "The learning rate must be greater than zero."));
        else
        {
            double warningThreshold = normalizedMethod switch
            {
                "grpo" or "dpo" => 1e-4,
                "tts-sft" => 2e-4,
                _ => 1e-3
            };
            if (learningRate > warningThreshold)
                findings.Add(new Finding(Severity.Warning, "learningRate",
                    $"{learningRate:G} is aggressive for {FriendlyMethod(normalizedMethod)}; {recommended.LearningRate:G} is the safer starting range."));
        }

        return new RunPlan(
            findings.All(finding => finding.Severity != Severity.Blocking), normalizedMethod, dataset,
            recommended, findings, estimatedGib, availableGib);
    }

    public static Recommendation Recommend(TrainingDatasetShape.Shape dataset, string method)
    {
        int examples = dataset?.LinesRead ?? 0;
        string normalized = NormalizeMethod(method);
        int rank = examples switch { < 200 => 8, < 2_000 => 16, < 20_000 => 32, _ => 64 };
        int steps = Math.Clamp(examples * (normalized == "continued-pretraining" ? 2 : 3) / 8, 60, 5_000);
        double learningRate = normalized switch
        {
            "dpo" => examples < 2_000 ? 5e-6 : 2e-6,
            "grpo" => examples < 2_000 ? 5e-6 : 2e-6,
            "continued-pretraining" => examples < 2_000 ? 5e-5 : 2e-5,
            "vision-sft" => examples < 2_000 ? 1e-4 : 5e-5,
            "tts-sft" => examples < 2_000 ? 2e-5 : 1e-5,
            _ => examples < 2_000 ? 2e-4 : 1e-4
        };
        string rationale = examples == 0
            ? "No dataset has been inspected yet; these are conservative defaults."
            : $"Scaled to {examples:N0} example(s) for {FriendlyMethod(normalized)}: rank {rank}, bounded multi-pass coverage and a method-specific learning rate.";
        return new Recommendation(rank, steps, learningRate, rationale);
    }

    public static bool Supports(TrainingDatasetShape.Shape dataset, string method) => NormalizeMethod(method) switch
    {
        "dpo" => dataset.SupportsPreferenceTuning,
        "continued-pretraining" => dataset.SupportsContinuedPretraining,
        "grpo" => dataset.SupportsGrpo,
        "vision-sft" => dataset.SupportsVisionFineTuning,
        "tts-sft" => dataset.SupportsTtsFineTuning,
        _ => dataset.SupportsSupervisedFineTuning
    };

    private static void ValidateModelFamily(string modelId, string method, List<Finding> findings)
    {
        string id = modelId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            findings.Add(new Finding(Severity.Blocking, "baseModel", "A base model is required."));
            return;
        }
        if (method == "vision-sft")
        {
            bool looksVision = ContainsAny(id, "vision", "-vl", "vl-", "llava", "pixtral", "qwen2.5-vl", "qwen2-vl", "idefics");
            if (!looksVision)
                findings.Add(new Finding(Severity.Blocking, "baseModel",
                    "Vision SFT requires a vision-language base model. Choose a VL/Vision/LLaVA/Pixtral-compatible model instead of a text-only model."));
        }
        if (method == "tts-sft")
        {
            // Keep planner and runner honest: the current executable adapter is SpeechT5. Other TTS
            // model families are not advertised until PaRuX owns a real training adapter for them.
            if (!id.Contains("speecht5", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding(Severity.Blocking, "baseModel",
                    "The current PaRuX TTS training adapter supports SpeechT5-family models. Other TTS families are not advertised until they have a real PaRuX-managed trainer."));
        }
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string RequiredShape(string method) => method switch
    {
        "dpo" => "JSONL objects with prompt, chosen and rejected strings.",
        "continued-pretraining" => "JSONL objects with a non-empty text string.",
        "grpo" => "JSONL objects with prompt and, for reference rewards, reference strings.",
        "vision-sft" => "JSONL objects with image plus messages, or image + prompt + response.",
        "tts-sft" => "JSONL objects with audio and text; referenced audio files must exist.",
        _ => "JSONL objects with a messages array of role/content pairs."
    };

    private static string FriendlyMethod(string method) => method switch
    {
        "dpo" => "DPO",
        "continued-pretraining" => "continued pretraining",
        "grpo" => "GRPO",
        "vision-sft" => "Vision SFT",
        "tts-sft" => "TTS SFT",
        _ => "supervised fine-tuning"
    };

    private static string NormalizeMethod(string? method)
    {
        string value = string.IsNullOrWhiteSpace(method) ? "sft" : method.Trim().ToLowerInvariant();
        return value is "sft" or "dpo" or "continued-pretraining" or "grpo" or "vision-sft" or "tts-sft" ? value : "sft";
    }

    public static double? ParameterBillionsFrom(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            modelId, @"(?<![A-Za-z0-9.])(?<value>\d+(?:\.\d+)?)\s*(?<unit>[bBmM])(?![A-Za-z0-9])");
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["value"].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)) return null;
        double billions = match.Groups["unit"].Value is "m" or "M" ? value / 1000.0 : value;
        return billions is > 0 and < 2_000 ? billions : null;
    }
}
