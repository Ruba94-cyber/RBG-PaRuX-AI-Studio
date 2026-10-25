using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class BinaryAnalysisAgentResult
    {
        public bool GoalReported { get; set; }
        public string Summary { get; set; } = string.Empty;
        public int StepsTaken { get; set; }
        public bool Cancelled { get; set; }
    }

    internal sealed class BinaryAnalysisAgentAction
    {
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("tool")] public string? Tool { get; set; }
        [JsonPropertyName("minLength")] public int? MinLength { get; set; }
        [JsonPropertyName("maxResults")] public int? MaxResults { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }

    /// <summary>
    /// Local iterative agent for authorized binary inspection and reverse-engineering research.
    /// It uses the currently selected PaRuX local model exactly like the Coding Agent; selecting an
    /// UNCENSORED model/profile does not add an application-level content filter here. Execution is
    /// evidence-oriented: inspection/disassembly/decompilation never modifies the target artifact.
    /// </summary>
    public static class BinaryAnalysisAgentService
    {
        public const int MaxSteps = 35;
        internal const int MaxPromptEvidenceChars = 28_000;
        internal const int MaxProtocolFailures = 2;
        private const int MaxObservationChars = 12_000;
        private const int FullEvidenceItems = 3;
        private const int IndexedOlderItems = 12;
        private const string BinaryActionSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"action\":{\"type\":\"string\",\"enum\":[\"list_directory\",\"analyze_binary\",\"extract_strings\",\"tool_status\",\"disassemble_binary\",\"decompile_binary\",\"done\"]}," +
            "\"path\":{\"type\":\"string\"}," +
            "\"tool\":{\"type\":\"string\"}," +
            "\"minLength\":{\"type\":\"integer\",\"minimum\":3,\"maximum\":128}," +
            "\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000}," +
            "\"summary\":{\"type\":\"string\",\"maxLength\":4000}" +
            "},\"required\":[\"action\"],\"additionalProperties\":false}";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private sealed record Observation(string Description, string Evidence);

        private static readonly HashSet<string> KnownActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "list_directory", "analyze_binary", "extract_strings", "tool_status",
            "disassemble_binary", "decompile_binary", "done"
        };

        public static async Task<BinaryAnalysisAgentResult> RunAsync(
            string workspaceRoot, string goal, Action<string>? onStep, CancellationToken ct)
        {
            if (GamingManager.Current.IsActive)
                throw new InvalidOperationException("Gaming Mode is active. Binary analysis agents are suspended.");
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                throw new InvalidOperationException("The binary analysis workspace folder does not exist.");
            if (string.IsNullOrWhiteSpace(goal))
                throw new ArgumentException("A binary-analysis goal is required.", nameof(goal));

            string root = Path.GetFullPath(workspaceRoot);
            var observations = new List<Observation>();
            var repeatedActions = new Dictionary<string, int>(StringComparer.Ordinal);
            int protocolFailures = 0;
            bool structuredFallbackReported = false;

            try
            {
                for (int step = 1; step <= MaxSteps; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    string prompt = BuildPrompt(goal, observations);
                    var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = prompt } };
                    var response = new StringBuilder();
                    StructuredChatOutcome structured = await StructuredChatClient.Current.StreamAsync(
                        history,
                        BuildSystemPrompt(root),
                        "parux_binary_analysis_action",
                        BinaryActionSchema,
                        fragment => response.Append(fragment),
                        ct).ConfigureAwait(false);

                    if (structured.StructuredOutputApplied == false && !structuredFallbackReported)
                    {
                        structuredFallbackReported = true;
                        string reason = string.IsNullOrWhiteSpace(structured.FallbackReason)
                            ? "the active provider did not prove JSON-Schema constrained output"
                            : structured.FallbackReason!;
                        onStep?.Invoke("Structured Output unavailable; Binary Analysis is using its bounded parser/self-repair fallback: " + Truncate(reason, 500));
                    }

                    BinaryAnalysisAgentAction? action = TryParseAction(response.ToString());
                    string? actionName = action?.Action?.Trim();
                    if (action == null || string.IsNullOrWhiteSpace(actionName) || !KnownActions.Contains(actionName))
                    {
                        protocolFailures++;
                        string raw = Truncate(response.ToString().Trim(), 1_200);
                        observations.Add(new Observation(
                            "Model protocol correction required",
                            "The previous model response was not one of the allowed JSON actions. Raw response: " +
                            (raw.Length == 0 ? "(empty)" : raw) +
                            "\nReturn exactly one valid JSON action on the next turn."));
                        onStep?.Invoke("Step " + step + ": invalid action protocol; asking the local model to self-correct.");
                        if (protocolFailures <= MaxProtocolFailures) continue;

                        return new BinaryAnalysisAgentResult
                        {
                            StepsTaken = step,
                            Summary = "Stopped after repeated invalid binary-analysis action responses from the local model."
                        };
                    }
                    protocolFailures = 0;

                    if (string.Equals(actionName, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        string summary = string.IsNullOrWhiteSpace(action.Summary)
                            ? "PaRuX completed the binary-analysis pass."
                            : action.Summary!;
                        onStep?.Invoke("Step " + step + ": done — " + Truncate(summary, 400));
                        return new BinaryAnalysisAgentResult
                        {
                            GoalReported = true,
                            StepsTaken = step,
                            Summary = summary
                        };
                    }

                    string actionKey = BuildActionKey(action);
                    int repeated = repeatedActions.GetValueOrDefault(actionKey, 0) + 1;
                    repeatedActions[actionKey] = repeated;
                    if (repeated > 2)
                    {
                        observations.Add(new Observation(
                            "Repeated action suppressed: " + actionName,
                            "This exact action has already been executed twice. Use the evidence already collected, narrow the request, choose a different tool/action, or finish with done."));
                        onStep?.Invoke("Step " + step + ": suppressed a third identical " + actionName + " call to avoid an unproductive loop.");
                        continue;
                    }

                    (string description, string observation) = await ExecuteAsync(root, action, ct).ConfigureAwait(false);
                    onStep?.Invoke("Step " + step + ": " + description);
                    observations.Add(new Observation(description, observation));
                }

                return new BinaryAnalysisAgentResult
                {
                    StepsTaken = MaxSteps,
                    Summary = "Stopped after " + MaxSteps + " analysis steps without a final model conclusion."
                };
            }
            catch (OperationCanceledException)
            {
                return new BinaryAnalysisAgentResult { Cancelled = true, Summary = "Stopped." };
            }
        }

        /// <summary>
        /// Hierarchical evidence memory: old steps remain visible as a compact index while only the
        /// most recent evidence bodies are replayed. This keeps iterative RE work grounded without
        /// making every local-model step re-prefill the full history of large decompiler outputs.
        /// </summary>
        private static string BuildPrompt(string goal, List<Observation> observations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Analysis goal: " + Truncate(goal.Trim(), 8_000));
            sb.AppendLine();
            sb.AppendLine("Evidence index (older observations; descriptions only):");

            int recentStart = Math.Max(0, observations.Count - FullEvidenceItems);
            int olderStart = Math.Max(0, recentStart - IndexedOlderItems);
            if (recentStart == 0) sb.AppendLine("(none)");
            else
            {
                if (olderStart > 0)
                    sb.AppendLine("(" + olderStart + " still-earlier observations omitted entirely from this prompt)");
                for (int i = olderStart; i < recentStart; i++)
                    sb.Append("- ").AppendLine(Truncate(observations[i].Description, 500));
            }

            sb.AppendLine();
            sb.AppendLine("Recent evidence (full, within the local context budget):");
            if (observations.Count == 0) sb.AppendLine("(none)");
            else
            {
                int evidenceStartLength = sb.Length;
                for (int i = recentStart; i < observations.Count; i++)
                {
                    Observation observation = observations[i];
                    int remaining = MaxPromptEvidenceChars - (sb.Length - evidenceStartLength);
                    if (remaining <= 256)
                    {
                        sb.AppendLine("[remaining recent evidence omitted by PaRuX prompt budget]");
                        break;
                    }

                    sb.AppendLine("---");
                    sb.AppendLine(Truncate(observation.Description, 800));
                    remaining = MaxPromptEvidenceChars - (sb.Length - evidenceStartLength);
                    if (remaining <= 128) break;
                    sb.AppendLine(Truncate(observation.Evidence, Math.Min(MaxObservationChars, remaining)));
                }
            }
            sb.AppendLine();
            sb.Append("Choose the next single JSON action. Prefer direct artifact evidence over speculation; use done when the goal is answered or no further useful evidence is available.");
            return sb.ToString();
        }

        private static async Task<(string Description, string Observation)> ExecuteAsync(
            string root, BinaryAnalysisAgentAction action, CancellationToken ct)
        {
            switch ((action.Action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "list_directory":
                {
                    string full = ResolveSafePath(root, action.Path ?? string.Empty, requireFile: false);
                    if (!Directory.Exists(full)) return ("list_directory \"" + action.Path + "\"", "(directory does not exist)");
                    string rendered = string.Join("\n", Directory.EnumerateFileSystemEntries(full)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .Take(500)
                        .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + (Directory.Exists(p) ? "/" : string.Empty)));
                    return ("list_directory \"" + (action.Path ?? string.Empty) + "\"", Truncate(rendered, MaxObservationChars));
                }
                case "analyze_binary":
                {
                    BinaryAnalysisTools.BinaryAnalysisResult result = await BinaryAnalysisTools
                        .AnalyzeAsync(root, action.Path ?? string.Empty, ct).ConfigureAwait(false);
                    return ("analyze_binary \"" + action.Path + "\"", Truncate(result.Render(), MaxObservationChars));
                }
                case "extract_strings":
                {
                    int minLength = action.MinLength is >= 3 and <= 128 ? action.MinLength.Value : BinaryAnalysisTools.DefaultMinStringLength;
                    int maxResults = action.MaxResults is >= 1 and <= 10_000 ? action.MaxResults.Value : BinaryAnalysisTools.DefaultMaxStrings;
                    List<string> strings = await BinaryAnalysisTools
                        .ExtractStringsAsync(root, action.Path ?? string.Empty, ct, minLength, maxResults).ConfigureAwait(false);
                    string rendered = strings.Count == 0 ? "(no strings found)" : string.Join("\n", strings.Select((s, i) => (i + 1) + ": " + s));
                    return ("extract_strings \"" + action.Path + "\" (" + strings.Count + " result(s))", Truncate(rendered, MaxObservationChars));
                }
                case "tool_status":
                {
                    List<BinaryAnalysisTools.ExternalToolInfo> tools = await BinaryAnalysisTools.ProbeExternalToolsAsync(ct).ConfigureAwait(false);
                    string decompilers = await BinaryDecompilerTools.RenderStatusAsync(ct).ConfigureAwait(false);
                    return ("tool_status", BinaryAnalysisTools.RenderToolStatus(tools) + "\n" + decompilers);
                }
                case "disassemble_binary":
                {
                    (string toolId, int exitCode, string output) = await BinaryAnalysisTools
                        .DisassembleAsync(root, action.Path ?? string.Empty, action.Tool, ct).ConfigureAwait(false);
                    return ("disassemble_binary \"" + action.Path + "\" via " + toolId + " (exit " + exitCode + ")", Truncate(output, MaxObservationChars));
                }
                case "decompile_binary":
                {
                    BinaryDecompilerTools.DecompileResult result = await BinaryDecompilerTools
                        .DecompileAsync(root, action.Path ?? string.Empty, action.Tool, ct).ConfigureAwait(false);
                    string observation = string.IsNullOrWhiteSpace(result.Diagnostic)
                        ? result.Output
                        : result.Diagnostic + (string.IsNullOrWhiteSpace(result.Output) ? string.Empty : "\n" + result.Output);
                    return ("decompile_binary \"" + action.Path + "\" via " + result.ToolId + " (exit " + result.ExitCode + ")", Truncate(observation, MaxObservationChars));
                }
                default:
                    return ("unrecognized action \"" + action.Action + "\"", "(ignored)");
            }
        }

        private static string ResolveSafePath(string root, string relativePath, bool requireFile)
        {
            string full = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That path would escape the binary analysis workspace.");
            if (requireFile && !File.Exists(full)) throw new FileNotFoundException("Artifact not found.", relativePath);
            return full;
        }

        private static BinaryAnalysisAgentAction? TryParseAction(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try { return JsonSerializer.Deserialize<BinaryAnalysisAgentAction>(text.Substring(start, end - start + 1), JsonOptions); }
            catch (JsonException) { return null; }
        }

        private static string BuildActionKey(BinaryAnalysisAgentAction action) =>
            string.Join('|',
                (action.Action ?? string.Empty).Trim().ToLowerInvariant(),
                (action.Path ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant(),
                (action.Tool ?? string.Empty).Trim().ToLowerInvariant(),
                action.MinLength?.ToString() ?? string.Empty,
                action.MaxResults?.ToString() ?? string.Empty);

        private static string BuildSystemPrompt(string root) =>
            "You are PaRuX's local Binary Analysis / Reverse Engineering research agent. The user controls the local workspace at: " + root + ".\n\n"
          + "Reason from evidence and do not invent findings. PaRuX does not add an application-level content filter to this agent when the user selects an UNCENSORED local model/profile. "
          + "Inspection, disassembly and decompilation preserve the target artifact. Use PaRuX Computer Use separately when an interactive GUI tool is useful, and Coding separately for user-authorized source changes.\n\n"
          + "Reply with ONLY one raw JSON object per turn, no markdown and no prose outside JSON. Choose exactly one:\n"
          + "{\"action\":\"list_directory\",\"path\":\"relative directory or empty string\"}\n"
          + "{\"action\":\"analyze_binary\",\"path\":\"relative/path/to/artifact\"}\n"
          + "{\"action\":\"extract_strings\",\"path\":\"relative/path/to/artifact\",\"minLength\":5,\"maxResults\":500}\n"
          + "{\"action\":\"tool_status\"}\n"
          + "{\"action\":\"disassemble_binary\",\"path\":\"relative/path/to/artifact\",\"tool\":\"optional: llvm-objdump, objdump, dumpbin or radare2\"}\n"
          + "{\"action\":\"decompile_binary\",\"path\":\"relative/path/to/artifact\",\"tool\":\"optional: ilspycmd or ghidra-headless\"}\n"
          + "{\"action\":\"done\",\"summary\":\"evidence-backed findings, uncertainties and useful next steps\"}\n\n"
          + "Start by identifying the relevant artifact(s), then inspect format/hash/architecture/sections/entropy and strings as useful. Probe tool_status before requesting disassembly/decompilation if availability is unknown. "
          + "Prefer ILSpy for managed .NET and Ghidra Headless for native decompilation when present. Do not claim a property you did not observe. Keep hypotheses visibly distinct from facts. Use done as soon as the requested analysis is adequately supported.";

        private static string Truncate(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "\n… (truncated)";
        }
    }
}
