using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server.Orchestration;

public enum CodingCheckVerdict
{
    /// <summary>At least one check really ran and every recorded check exited 0.</summary>
    Pass,
    /// <summary>A recorded check exited non-zero.</summary>
    Fail,
    /// <summary>No executed check is recorded; nothing but a model's opinion supports the result.</summary>
    Unverified
}

/// <summary>One command the desktop agent actually executed, with its real exit code.</summary>
public sealed record ExecutedCheck(string Command, int ExitCode, string Scope);

public sealed record CodingCheckGate(CodingCheckVerdict Verdict, IReadOnlyList<ExecutedCheck> Checks, string Reason);

/// <summary>
/// The evidence gate for coding tasks. A language model reading a diff cannot establish that code
/// builds or that tests pass; only an executed command with an exit code can. VerificationStatus
/// "pass" for a coding task therefore requires at least one recorded executed check in ResultJson
/// (<c>executedChecks</c>: {command, exitCode, scope}), every recorded check exiting 0, and the worker's
/// accepted-command claim (<c>verifiedByCommand</c> true). That claim is the only receipt field bound to
/// the task checkpoint and workspace fingerprint, so exit-0 checks alone stay unverified. The model
/// verdict is kept, but only as advisory text beside the gate (<c>llmAdvisory</c>), never as proof.
/// </summary>
public static class CodingExecutedChecks
{
    public const int MaximumChecks = 32;
    public const string UnverifiedStage = "Completed (unverified: no executed checks)";
    public const string UnverifiedWithoutAcceptedCommandStage = "Completed (unverified: checks exited 0 but no accepted command)";

    public static bool AppliesTo(string? role) => string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase);

    public static CodingCheckGate Evaluate(string? resultJson)
    {
        var checks = new List<ExecutedCheck>();
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(resultJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("executedChecks", out JsonElement array)
                    && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in array.EnumerateArray())
                    {
                        if (checks.Count >= MaximumChecks) break;
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        string command = item.TryGetProperty("command", out JsonElement c) && c.ValueKind == JsonValueKind.String
                            ? (c.GetString() ?? string.Empty).Trim() : string.Empty;
                        if (command.Length == 0) continue;
                        if (!item.TryGetProperty("exitCode", out JsonElement e) || e.ValueKind != JsonValueKind.Number
                            || !e.TryGetInt32(out int exitCode)) continue;
                        string scope = item.TryGetProperty("scope", out JsonElement s) && s.ValueKind == JsonValueKind.String
                            ? (s.GetString() ?? ".") : ".";
                        checks.Add(new ExecutedCheck(command.Length <= 500 ? command : command[..500], exitCode, scope.Length == 0 ? "." : scope));
                    }
                }
            }
            catch (JsonException) { /* unparseable evidence is no evidence */ }
        }

        if (checks.Count == 0)
            return new CodingCheckGate(CodingCheckVerdict.Unverified, checks, "no executed check is recorded for this task");
        ExecutedCheck? failed = checks.FirstOrDefault(check => check.ExitCode != 0);
        if (failed != null)
            return new CodingCheckGate(CodingCheckVerdict.Fail, checks,
                "executed check \"" + failed.Command + "\" (scope " + failed.Scope + ") exited with code " + failed.ExitCode);
        return new CodingCheckGate(CodingCheckVerdict.Pass, checks, checks.Count + " executed check(s) exited 0");
    }

    /// <summary>
    /// The outcome gate the verifier applies. A pass also needs the worker's accepted-command claim
    /// (<c>verifiedByCommand</c> true). CodingResultVerifierGate has already validated that claim's
    /// checkpoint and workspace fingerprint before this runs, so exit-0 checks without it stay unverified.
    /// </summary>
    public static CodingCheckGate EvaluateOutcome(string? resultJson)
    {
        CodingCheckGate checks = Evaluate(resultJson);
        if (checks.Verdict != CodingCheckVerdict.Pass || ReportsAcceptedCommand(resultJson))
            return checks;
        return new CodingCheckGate(CodingCheckVerdict.Unverified, checks.Checks,
            checks.Checks.Count + " executed check(s) exited 0, but the worker reports no accepted command (verifiedByCommand is not true)");
    }

    private static bool ReportsAcceptedCommand(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("verifiedByCommand", out JsonElement claim)
                && claim.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Adds the model verdict to ResultJson as an advisory field, without touching the evidence.</summary>
    public static string? WithAdvisory(string? resultJson, IReadOnlyList<string> modelVerdicts, CodingCheckGate gate)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(resultJson) ? new JsonObject() : JsonNode.Parse(resultJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException) { return resultJson; }
        root["llmAdvisory"] = new JsonObject
        {
            ["note"] = "Model opinion only; it is not verification evidence.",
            ["verdicts"] = new JsonArray(modelVerdicts.Select(v => (JsonNode?)JsonValue.Create(v.Length <= 1_000 ? v : v[..1_000])).ToArray())
        };
        root["verificationBasis"] = gate.Verdict switch
        {
            CodingCheckVerdict.Pass => "executed-checks",
            CodingCheckVerdict.Fail => "executed-checks-failed",
            _ => "none"
        };
        return root.ToJsonString();
    }
}
