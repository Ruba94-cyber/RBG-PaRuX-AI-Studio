using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Turns a stored context choice into the launch it implies, in one place.
///
/// A preference that does not reach the runtime is not a preference, and the failure mode of
/// spreading this across the call sites is specific: the fit estimate would keep reserving for one
/// context while llama-server was started with another, so the gate that exists to prevent
/// out-of-memory would be checking the wrong number.
///
/// An explicit per-model ContextPreferenceStore choice remains authoritative. When the user has not
/// made such a choice, the active Execution Profile is allowed to CAP the tuned runtime context (for
/// example Fast -> 16k) but never silently expand it beyond the profile that was benchmarked for the
/// machine. This keeps explicit user choices stable and makes PreferredContextTokens operational
/// without turning a quality preference into an unsafe long-context request.
/// </summary>
public static class ContextPreferenceLaunch
{
    /// <summary>
    /// Applies the user's choice to the tuned performance profile. The profile already owns
    /// <c>--ctx-size</c> and <c>--cache-type-k/v</c>, so overriding it here keeps a single writer for
    /// those arguments rather than appending a second, conflicting pair. Launch-only controls such
    /// as RAM-backed KV cache and RoPE scaling are carried on the same concrete profile so every
    /// loader can build an identical command line.
    /// </summary>
    /// <returns>The explicit preference when this call applied it for the first time, otherwise
    /// null. Returning null on a second pass keeps the legacy StartText path from appending the
    /// same advanced flags after the common runtime builder has already emitted them.</returns>
    public static ContextPreferenceStore.ContextPreference? ApplyTo(
        LlamaPerformanceProfileDto profile, string modelPath)
    {
        if (profile == null) return null;
        if (profile.ContextPolicyApplied) return null;

        // A concrete launch profile is finalized exactly once. Tuning/profile-copy code creates a
        // fresh instance for a new launch, so this marker never leaks into persisted benchmark
        // evidence (and the DTO marks it JsonIgnore as an additional guard).
        profile.ContextPolicyApplied = true;
        profile.DisableKvOffload = false;
        profile.RopeScale = 1.0;
        profile.ContextIsPerRequest = false;

        ContextPreferenceStore.ContextPreference? preference = ContextPreferenceStore.Get(modelPath);
        if (preference != null)
        {
            profile.ContextSize = (int)Math.Clamp(
                preference.ContextLength, ContextCapacityPlanner.MinimumUsableContext, int.MaxValue);
            // The user picked a window for one inference, not a pool to divide across slots.
            profile.ContextIsPerRequest = true;
            profile.CacheTypeK = preference.CacheType;
            profile.CacheTypeV = preference.CacheType;
            profile.DisableKvOffload = preference.UsesSystemRam;
            profile.RopeScale = double.IsFinite(preference.RopeScale) && preference.RopeScale > 1.0
                ? preference.RopeScale : 1.0;
            return preference;
        }

        // No explicit context choice: profile context is a resource envelope, not a second user
        // setting. It may reduce a tuned launch but may not expand one that benchmark evidence chose.
        ApplyExecutionProfileCap(profile, ExecutionProfileStore.Current.Get(modelPath).Effective);
        return null;
    }

    internal static void ApplyExecutionProfileCap(
        LlamaPerformanceProfileDto profile,
        ModelExecutionProfile? executionProfile)
    {
        if (profile == null || executionProfile == null || profile.ContextSize <= 0) return;
        int preferred = Math.Clamp(
            executionProfile.PreferredContextTokens,
            (int)ContextCapacityPlanner.MinimumUsableContext,
            int.MaxValue);
        profile.ContextSize = Math.Max(
            (int)ContextCapacityPlanner.MinimumUsableContext,
            Math.Min(profile.ContextSize, preferred));
    }

    /// <summary>
    /// Legacy argument projection retained for callers/tests that need to inspect a preference
    /// directly. RuntimeLaunchOptimizer also projects the same values from the concrete launch
    /// profile, which is what closes parity for arbitrary/routed/hot-pool loads.
    /// </summary>
    public static IReadOnlyList<string> ExtraArguments(ContextPreferenceStore.ContextPreference? preference)
    {
        if (preference == null) return Array.Empty<string>();
        var args = new List<string>();

        if (preference.UsesSystemRam) args.Add("--no-kv-offload");

        if (preference.RopeScale > 1.0)
        {
            args.Add("--rope-scaling");
            args.Add("yarn");
            args.Add("--rope-scale");
            args.Add(preference.RopeScale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }

        return args;
    }
}
