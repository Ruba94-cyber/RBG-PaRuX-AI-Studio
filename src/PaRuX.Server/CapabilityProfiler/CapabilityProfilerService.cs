using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.CapabilityProfiler;

/// <summary>
/// Directive Phase 7 (Empirical Model Capability Profiler): a fixed battery of probes - one per
/// CapabilityProbeCategory (json_reliability/instruction_following/tool_calling/
/// long_context_recall/math_reasoning, plus coding/debugging/multilingual/patch_generation added
/// 2026-08-27 per the directive's broader probe-coverage requirement) - run sequentially against
/// whichever model is CURRENTLY loaded and healthy in the text runtime. Reuses
/// ILocalCompletionClient (the same seam AgentWorkerService/VerifierService/EvalRunnerService
/// already depend on) rather than a second HTTP client, and RuntimeManagerService/AiProfileService
/// for real active-model identity rather than assuming one - same discipline AutoLabService
/// already established for "capture the REAL active model". Every probe here is pattern-checked,
/// never executed - this profiler never runs model-generated code/patches, a deliberate security
/// boundary (arbitrary model output is untrusted input, not something to eval() or apply()).
///
/// Unlike Eval Lab (which asks a second model call to judge a free-text response against an
/// "expected behavior" description), every probe here is scored deterministically by parsing the
/// raw output directly - no judge call, no ambiguity about what "pass" means for a fixed,
/// mechanically-checkable property like "is this valid JSON" or "does the output contain this
/// exact code".
/// </summary>
public sealed class CapabilityProfilerService
{
    private readonly CapabilityProfilerStore _store;
    private readonly ILocalCompletionClient _client;
    private readonly RuntimeManagerService _runtimeManager;
    private readonly AiProfileService _profiles;
    private readonly SmartRouterService _router;

    public CapabilityProfilerService(CapabilityProfilerStore store, ILocalCompletionClient client,
        RuntimeManagerService runtimeManager, AiProfileService profiles, SmartRouterService router)
    {
        _store = store;
        _client = client;
        _runtimeManager = runtimeManager;
        _profiles = profiles;
        _router = router;
    }

    /// <summary>Starts a profiling run. When <paramref name="requestedRole"/> is given, routes
    /// through SmartRouterService first (directive Phase 8 integration) so the caller doesn't
    /// have to manually load a role before profiling it - a real swap only happens if that role
    /// isn't already the one running. When null (the original v1 behavior), profiles whatever is
    /// already loaded right now without requesting any change. Returns immediately with status
    /// "running" (probes execute in the background, mirroring EvalRunnerService's own
    /// fire-and-forget pattern) unless no text model ends up loaded and healthy, in which case the
    /// run is recorded as "error" with an honest reason and no probes are ever attempted - never
    /// fabricates results for a model that isn't there.</summary>
    public async Task<CapabilityProfileRunDto> StartRunAsync(string? requestedRole, CancellationToken ct)
    {
        string? modelPath;
        string? role;
        if (!string.IsNullOrWhiteSpace(requestedRole))
        {
            RouteDecisionDto routed = await _router.ResolveAsync(new RouteRequest { Role = requestedRole }, ct);
            modelPath = routed.Success ? routed.ModelPath : null;
            role = routed.ResolvedRole;
        }
        else
        {
            RuntimeManagerStatus status = await _runtimeManager.GetStatusAsync(ct);
            bool textRunning = status.Backends.Any(b => b.Backend.StartsWith("text-", StringComparison.Ordinal) && b.Running && b.Healthy);
            modelPath = textRunning
                ? (status.ActiveRole != null ? LocalAiPaths.GetRoleModelPathOrNull(status.ActiveRole) : await ResolveLegacyActiveModelPathAsync(ct))
                : null;
            role = status.ActiveRole;
        }

        var run = new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelPath = modelPath ?? string.Empty,
            Role = role,
            Status = modelPath == null ? CapabilityProfileRunStatus.Error : CapabilityProfileRunStatus.Running,
            StartedUtc = DateTime.UtcNow,
            Error = modelPath == null
                ? (!string.IsNullOrWhiteSpace(requestedRole)
                    ? "Could not route to role \"" + requestedRole + "\": no assignment or hardware/runtime failure. See GET /api/v1/router/decisions for details."
                    : "No text model is currently loaded and healthy. Load one first via POST /api/v1/models/library/{fileName}/load or the Standard/Uncensored toggle.")
                : null
        };
        _store.CreateRun(run);
        if (modelPath == null) return run;

        // Real bug caught by live testing: passing the endpoint's own request-scoped `ct` here
        // stalls the run after exactly one probe with no error recorded - ASP.NET Core cancels
        // HttpContext.RequestAborted shortly after the 202 response is sent, and that same token
        // then cancels the second probe's HTTP call mid-flight; the resulting
        // OperationCanceledException is deliberately excluded from RunProbesAsync's own catch
        // (matching AgentWorkerService/VerifierService's convention of never swallowing
        // cancellation) so it silently escapes the fire-and-forget task, leaving the run stuck at
        // "running" forever. Fixed by using CancellationToken.None for the background execution,
        // the exact same choice AutoLabService.CreateExperimentAsync/EvalRunnerService already
        // make for their own fire-and-forget runs.
        _ = RunProbesAsync(run.Id, CancellationToken.None);
        return run;
    }

    /// <summary>Falls back to the pre-existing Standard/Uncensored toggle's own identity
    /// resolution (AiProfileService.GetStatusAsync + LocalAiPaths.StandardModel/UncensoredModel -
    /// the exact same pattern AutoLabService.CreateExperimentAsync already uses) for a text
    /// runtime that was started outside the new generalized-role tracking, e.g. via the legacy
    /// Standard/Uncensored chat-header toggle rather than the Managed Model Library's Load action.</summary>
    private async Task<string> ResolveLegacyActiveModelPathAsync(CancellationToken ct)
    {
        AiProfileStatus profile = await _profiles.GetStatusAsync(ct);
        bool uncensored = string.Equals(profile.TextProfile, "UNCENSORED", StringComparison.OrdinalIgnoreCase);
        return uncensored ? LocalAiPaths.UncensoredModel : LocalAiPaths.StandardModel;
    }

    private async Task RunProbesAsync(string runId, CancellationToken ct)
    {
        try
        {
            foreach (Probe probe in BuildProbes())
            {
                CompletionResult completion = await _client.CompleteAsync(probe.SystemPrompt, probe.UserPrompt, ct);
                bool passed;
                string detail;
                string? rawOutput;
                if (!completion.Success)
                {
                    passed = false;
                    detail = "The model call itself failed: " + (completion.Error ?? "unknown error");
                    rawOutput = null;
                }
                else
                {
                    (passed, detail) = probe.Score(completion.Content ?? string.Empty);
                    rawOutput = completion.Content;
                }

                _store.AddResult(new CapabilityProbeResultDto
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RunId = runId,
                    Category = probe.Category,
                    Passed = passed,
                    Detail = detail,
                    RawOutput = rawOutput,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            _store.UpdateRunStatus(runId, CapabilityProfileRunStatus.Completed, DateTime.UtcNow, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.UpdateRunStatus(runId, CapabilityProfileRunStatus.Error, DateTime.UtcNow, "Profiling run failed: " + ex.Message);
        }
    }

    public List<CapabilityProfileRunDto> ListRuns() => _store.ListRuns();
    public CapabilityProfileRunDto? GetRun(string id) => _store.GetRun(id);
    public List<CapabilityProbeResultDto> GetResults(string runId) => _store.GetResults(runId);

    /// <summary>Per-model rollup across every completed run for that exact ModelPath - null when
    /// no completed run exists for it yet, never a fabricated zero. Delegates to the store (see
    /// CapabilityProfilerStore.GetModelSummary's own doc comment for why the aggregation itself
    /// lives there now, not here).</summary>
    public CapabilityModelSummaryDto? GetModelSummary(string modelPath) => _store.GetModelSummary(modelPath);

    // ---- The probe battery ----

    private sealed record Probe(string Category, string SystemPrompt, string UserPrompt, Func<string, (bool Passed, string Detail)> Score);

    /// <summary>Deliberately not a random-context test: the needle position/length are fixed and
    /// the filler is deterministically generated (no external text, no copyright surface), sized
    /// to a few thousand tokens - a real but bounded "does the model actually read past the
    /// opening before answering" probe, not an exhaustive 128k-context benchmark. Disclosed as
    /// such in CapabilityProbeCategory.LongContextRecall's own doc comment.</summary>
    public const string ContextNeedle = "ZX7Q9K2";

    private static List<Probe> BuildProbes() => new()
    {
        new Probe(
            CapabilityProbeCategory.JsonReliability,
            "Respond with ONLY a single valid JSON object matching exactly this shape: "
                + "{\"name\": string, \"age\": number, \"active\": boolean}. No prose, no markdown code fences, no explanation.",
            "Create a JSON object describing a fictional person named Alex who is 30 years old and currently active.",
            ScoreJson),
        new Probe(
            CapabilityProbeCategory.InstructionFollowing,
            "You must follow output-format instructions exactly, with no extra words, punctuation, or explanation.",
            "Reply with exactly the single word PONG in uppercase, and nothing else - no punctuation, no explanation.",
            ScoreInstructionFollowing),
        new Probe(
            CapabilityProbeCategory.MathReasoning,
            "Solve the problem. Reply with ONLY the final numeric answer, no units, no explanation.",
            "A store had 120 apples. It sold 35% of them on Monday, then sold 40 more on Tuesday. How many apples are left?",
            raw => ScoreMath(raw, expected: 38)),
        new Probe(
            CapabilityProbeCategory.LongContextRecall,
            "Read the notes carefully. Answer only the final question, with ONLY the requested value and nothing else.",
            BuildLongContextPrompt(),
            ScoreLongContextRecall),
        new Probe(
            CapabilityProbeCategory.ToolCalling,
            "You have access to one tool: get_weather(city: string) - returns current weather for a city. "
                + "When you need it, respond with ONLY a block in exactly this format and nothing else: "
                + "<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"<city name>\"}}</tool_call>",
            "What is the weather like in Paris right now? Use the tool to find out.",
            ScoreToolCall),
        new Probe(
            CapabilityProbeCategory.Coding,
            "Reply with ONLY the function code, no explanation, no markdown code fences.",
            "Write a Python function named is_even that takes one integer argument n and returns "
                + "True if n is even, False otherwise.",
            ScoreCoding),
        new Probe(
            CapabilityProbeCategory.Debugging,
            "You are given a short code snippet with exactly one bug: a misspelled variable name. "
                + "Reply with ONLY the exact misspelled variable name as it appears in the code, nothing else.",
            "def find_max(items):\n    highest = items[0]\n    for i in range(1, len(items)):\n"
                + "        if items[i] > highest:\n            highest = items[i]\n    return highst",
            raw => ScoreDebugging(raw, expected: "highst")),
        new Probe(
            CapabilityProbeCategory.Multilingual,
            "Reply with ONLY one word, in Italian - no English, no punctuation, no explanation.",
            "What is the capital of France? Reply with the Italian name of the city.",
            raw => ScoreMultilingual(raw, expectedItalian: "parigi", commonEnglishAnswer: "paris")),
        new Probe(
            CapabilityProbeCategory.PatchGeneration,
            "Reply with ONLY a unified diff patch, no explanation, no markdown code fences.",
            "Given this file content:\nline1\nline2\nline3\n\n"
                + "Produce a unified diff that changes the line \"line2\" to \"line2 modified\".",
            ScorePatch)
    };

    public static (bool, string) ScoreJson(string raw)
    {
        string candidate = StripCodeFences(raw).Trim();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(candidate);
            JsonElement root = doc.RootElement;
            bool hasName = root.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String;
            bool hasAge = root.TryGetProperty("age", out JsonElement age) && age.ValueKind == JsonValueKind.Number;
            bool hasActive = root.TryGetProperty("active", out JsonElement active)
                && active.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (hasName && hasAge && hasActive)
                return (true, "Valid JSON with the expected \"name\" (string)/\"age\" (number)/\"active\" (boolean) fields.");
            return (false, "Parsed as JSON but was missing or mistyped one of \"name\"/\"age\"/\"active\".");
        }
        catch (JsonException ex) { return (false, "Output did not parse as JSON: " + ex.Message); }
    }

    private static string StripCodeFences(string raw)
    {
        string trimmed = raw.Trim();
        Match match = Regex.Match(trimmed, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : trimmed;
    }

    public static (bool, string) ScoreInstructionFollowing(string raw)
    {
        string trimmed = raw.Trim().Trim('.', '!', '"', '\'');
        bool passed = string.Equals(trimmed, "PONG", StringComparison.OrdinalIgnoreCase);
        return (passed, passed ? "Replied with exactly \"PONG\" as instructed." : "Expected exactly \"PONG\"; got: " + Truncate(raw, 120));
    }

    public static (bool, string) ScoreMath(string raw, int expected)
    {
        // Last number in the output, not the first: a reasoning-enabled model's chain of thought
        // typically restates the problem's own numbers (e.g. "120") before its final answer.
        MatchCollection matches = Regex.Matches(raw, @"-?\d+(?:\.\d+)?");
        if (matches.Count == 0) return (false, "No number found in the output.");
        if (!double.TryParse(matches[^1].Value, out double value)) return (false, "Could not parse the number in the output.");
        bool passed = Math.Abs(value - expected) < 0.5;
        return (passed, passed ? $"Correctly answered {expected}." : $"Expected {expected}, model answered {value}.");
    }

    private static string BuildLongContextPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"IMPORTANT CODE TO REMEMBER: {ContextNeedle}");
        sb.AppendLine();
        // Deterministic, self-contained filler (no external/copyrighted text) - about 1,000-1,500
        // tokens of genuinely-distinct sentences the model must actually skip past.
        for (int i = 0; i < 70; i++)
        {
            char condition = (char)('A' + i % 5);
            sb.AppendLine($"Observation log entry {i}: in measurement set {i}, the recorded value was "
                + $"{(i * 37) % 97} units under test condition {condition}, repeated {1 + i % 4} times with no anomalies noted.");
        }
        sb.AppendLine();
        sb.AppendLine("Question: what is the exact code given near the start of these notes? Reply with only the code.");
        return sb.ToString();
    }

    public static (bool, string) ScoreLongContextRecall(string raw)
    {
        bool passed = raw.Contains(ContextNeedle, StringComparison.OrdinalIgnoreCase);
        return (passed, passed ? "Correctly recalled the planted code from early in the context."
            : "Did not recall the planted code; got: " + Truncate(raw, 120));
    }

    public static (bool, string) ScoreToolCall(string raw)
    {
        Match match = Regex.Match(raw, @"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline);
        if (!match.Success) return (false, "No <tool_call>...</tool_call> block found in the output.");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(match.Groups[1].Value);
            JsonElement root = doc.RootElement;
            bool nameOk = root.TryGetProperty("name", out JsonElement name) && name.GetString() == "get_weather";
            bool hasCity = root.TryGetProperty("arguments", out JsonElement args)
                && args.TryGetProperty("city", out JsonElement city)
                && city.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(city.GetString());
            if (nameOk && hasCity)
                return (true, "Emitted a correctly-shaped tool_call block for get_weather with a city argument.");
            return (false, "Found a tool_call block, but \"name\"/\"arguments.city\" was missing or wrong.");
        }
        catch (JsonException ex) { return (false, "Found a tool_call block but its JSON was invalid: " + ex.Message); }
    }

    /// <summary>Pattern-checked, never executed - see CapabilityProbeCategory.Coding's own doc
    /// comment for why this profiler never runs model-generated code. Requires the exact
    /// function name/signature and a real modulo check, not just any code that happens to mention
    /// "is_even" (e.g. a comment or docstring alone would not pass).</summary>
    public static (bool, string) ScoreCoding(string raw)
    {
        string code = StripCodeFences(raw);
        bool hasSignature = Regex.IsMatch(code, @"def\s+is_even\s*\(\s*n\s*\)\s*:");
        bool hasModuloCheck = Regex.IsMatch(code, @"n\s*%\s*2");
        bool hasReturn = code.Contains("return", StringComparison.Ordinal);
        if (hasSignature && hasModuloCheck && hasReturn)
            return (true, "Defined is_even(n) with a real modulo check and a return statement.");
        return (false, "Missing the expected \"def is_even(n):\" signature, an \"n % 2\" check, or a return statement. Got: " + Truncate(code, 160));
    }

    /// <summary>Exact match (trimmed of surrounding punctuation/quotes) against the one
    /// deliberately-misspelled variable name in the planted snippet - "highest" (the correctly
    /// spelled name used everywhere else in the function) is a wrong answer, not a near-miss,
    /// since naming the WRONG variable would send a real user looking in the wrong place.</summary>
    public static (bool, string) ScoreDebugging(string raw, string expected)
    {
        string trimmed = raw.Trim().Trim('.', '!', '"', '\'', '`');
        bool passed = string.Equals(trimmed, expected, StringComparison.OrdinalIgnoreCase);
        return (passed, passed ? $"Correctly identified the misspelled variable \"{expected}\"."
            : $"Expected exactly \"{expected}\"; got: " + Truncate(raw, 120));
    }

    /// <summary>Explicitly distinguishes "answered in the requested language" from "answered
    /// correctly but in English" - the latter is a real, common failure mode (the instruction to
    /// answer in Italian being silently ignored) and gets its own honest failure message rather
    /// than being lumped in with "wrong answer entirely".</summary>
    public static (bool, string) ScoreMultilingual(string raw, string expectedItalian, string commonEnglishAnswer)
    {
        string trimmed = raw.Trim().Trim('.', '!', '"', '\'').ToLowerInvariant();
        if (string.Equals(trimmed, expectedItalian, StringComparison.Ordinal))
            return (true, $"Correctly answered \"{expectedItalian}\" in the requested language.");
        if (string.Equals(trimmed, commonEnglishAnswer, StringComparison.Ordinal))
            return (false, $"Answered in English (\"{commonEnglishAnswer}\") instead of the requested Italian (\"{expectedItalian}\").");
        return (false, $"Expected \"{expectedItalian}\"; got: " + Truncate(raw, 120));
    }

    /// <summary>Checked for the real structural markers of a unified diff (a hunk header and both
    /// a removed and an added line for the changed content) - never executed or applied, matching
    /// this profiler's own "pattern-check, don't run model output" discipline.</summary>
    public static (bool, string) ScorePatch(string raw)
    {
        string patch = StripCodeFences(raw);
        bool hasHunkHeader = patch.Contains("@@", StringComparison.Ordinal);
        bool hasRemovedLine = Regex.IsMatch(patch, @"^-\s*line2\s*$", RegexOptions.Multiline);
        bool hasAddedLine = Regex.IsMatch(patch, @"^\+\s*line2 modified\s*$", RegexOptions.Multiline);
        if (hasHunkHeader && hasRemovedLine && hasAddedLine)
            return (true, "Produced a unified diff with a hunk header and the correct removed/added lines.");
        return (false, "Missing a \"@@\" hunk header or the expected \"-line2\"/\"+line2 modified\" changed lines. Got: " + Truncate(patch, 160));
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}
