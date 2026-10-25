using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>A model as the capability resolver sees it; facts only, no I/O.</summary>
public sealed record CapabilityRouteCandidate(
    string ModelId,
    IReadOnlyCollection<string> Capabilities,
    string ContentProfile,
    bool HardwareFit,
    double Score,
    bool IsCloud = false,
    string? RepoId = null,
    string? Architecture = null);

public sealed record CapabilityRouteContext(
    string Capability,
    string ContentProfile,
    bool GamingActive,
    bool LocalOnly,
    bool CloudConsent,
    InstalledRuntime Runtime);

public sealed record CapabilityRouteOutcome(
    bool Success,
    string Mode,
    string? ChosenModelId,
    string? RefusalCode,
    IReadOnlyList<string> FallbackTrace,
    string Explanation);

/// <summary>
/// Pure per-capability routing: Automatic ranks eligible candidates, Prefer tries the pinned model first and falls back
/// with a visible trace, Force never substitutes and returns a typed refusal instead.
/// </summary>
public static class CapabilityRouteResolver
{
    public static CapabilityRouteOutcome Resolve(CapabilityRouteContext ctx,
        CapabilityModeOverride? over, IReadOnlyList<CapabilityRouteCandidate> installed)
    {
        string mode = over?.Mode ?? ScopedRouteMode.Automatic;
        var trace = new List<string>();
        if (ctx.GamingActive)
            return Refuse(mode, "gaming_hard_off", trace, "Gaming mode is active, so no model was routed or loaded.");

        if (mode == ScopedRouteMode.Force)
        {
            CapabilityRouteCandidate? model = installed.FirstOrDefault(c => c.ModelId == over!.ModelId);
            string? why = model is null ? "missing" : Ineligibility(ctx, model);
            if (why is not null)
            {
                trace.Add("force_refused:" + why + ":no_substitution");
                return Refuse(mode, why, trace, $"Forced model \"{over!.ModelId}\" cannot be used for {ctx.Capability}: "
                    + Describe(why) + " No other model was substituted.");
            }
            return Ok(mode, model!, trace, $"Forced model \"{model!.ModelId}\" for {ctx.Capability}.");
        }

        if (mode == ScopedRouteMode.Prefer)
        {
            CapabilityRouteCandidate? model = installed.FirstOrDefault(c => c.ModelId == over!.ModelId);
            string? why = model is null ? "missing" : Ineligibility(ctx, model);
            if (why is null) return Ok(mode, model!, trace, $"Preferred model \"{model!.ModelId}\" for {ctx.Capability}.");
            trace.Add("prefer_unavailable:" + why);
            CapabilityRouteCandidate? auto = Best(ctx, installed);
            if (auto is null)
            {
                trace.Add("fallback_failed:no_eligible_model");
                return Refuse(mode, "no_eligible_model", trace, $"Preferred model \"{over!.ModelId}\" was unavailable ("
                    + Describe(why) + ") and no other eligible model exists for " + ctx.Capability + ".");
            }
            trace.Add("fallback:" + auto.ModelId);
            return Ok(mode, auto, trace, $"Preferred model \"{over!.ModelId}\" was unavailable (" + Describe(why)
                + $"); automatic routing selected \"{auto.ModelId}\".");
        }

        CapabilityRouteCandidate? pick = Best(ctx, installed);
        return pick is null
            ? Refuse(ScopedRouteMode.Automatic, "no_eligible_model", trace, $"No eligible model for {ctx.Capability}.")
            : Ok(ScopedRouteMode.Automatic, pick, trace, $"Automatic routing selected \"{pick.ModelId}\" for {ctx.Capability}.");
    }

    internal static string? Ineligibility(CapabilityRouteContext ctx, CapabilityRouteCandidate c)
    {
        if (!c.Capabilities.Contains(ctx.Capability, StringComparer.Ordinal)) return "wrong_capability";
        if (c.IsCloud && (ctx.LocalOnly || !ctx.CloudConsent)) return "cloud_not_permitted";
        if (!string.Equals(c.ContentProfile, ctx.ContentProfile, StringComparison.Ordinal)) return "content_profile_mismatch";
        if (!c.HardwareFit) return "hardware_unfit";
        if (!c.IsCloud && ModelRuntimeRequirements.Evaluate(c.RepoId, c.Architecture, ctx.Runtime.BuildTag,
                ctx.Runtime.Ownership).Verdict is RuntimeRequirementVerdict.NeedsRuntimeUpdate
                or RuntimeRequirementVerdict.NeedsUserAction) return "runtime_too_old";
        return null;
    }

    private static CapabilityRouteCandidate? Best(CapabilityRouteContext ctx, IReadOnlyList<CapabilityRouteCandidate> installed) =>
        installed.Where(c => Ineligibility(ctx, c) is null).OrderByDescending(c => c.Score)
            .ThenBy(c => c.ModelId, StringComparer.Ordinal).FirstOrDefault();

    private static string Describe(string code) => code switch
    {
        "missing" => "the model is not installed.",
        "wrong_capability" => "it does not provide this capability.",
        "hardware_unfit" => "it does not fit this hardware.",
        "runtime_too_old" => "the installed runtime is too old for it.",
        "content_profile_mismatch" => "its content profile does not match the request.",
        "cloud_not_permitted" => "cloud models are not permitted (local-only or no consent).",
        _ => code
    };

    private static CapabilityRouteOutcome Ok(string mode, CapabilityRouteCandidate c, List<string> trace, string text) =>
        new(true, mode, c.ModelId, null, trace, text);

    private static CapabilityRouteOutcome Refuse(string mode, string code, List<string> trace, string text) =>
        new(false, mode, null, code, trace, text);
}
