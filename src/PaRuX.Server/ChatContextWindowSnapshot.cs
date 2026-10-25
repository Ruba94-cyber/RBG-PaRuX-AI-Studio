using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Captures the serving context before a direct-engine transition can stop llama.cpp. The preferred
/// source is the live provider /props value because it reflects the actual launch. If that endpoint
/// is unavailable, the selected GGUF's explicit context preference or the same conservative 32k
/// default used by RuntimeManagerService is used. This snapshot is a safety ceiling for prompt
/// fitting, not evidence that a different direct-engine implementation has a larger window.
/// </summary>
internal static class ChatContextWindowSnapshot
{
    private const long DefaultLaunchContextLength = 32_768;

    internal static async Task<int> CaptureAsync(
        HttpClient http,
        string providerEndpoint,
        RouteDecisionDto? route,
        CancellationToken cancellationToken)
    {
        // ActiveContextWindow is intentionally cached because /props sits on the chat hot path. A
        // successful router swap is the exception: the old resident's n_ctx is no longer evidence
        // for the new model. Drop that cache entry before taking the one snapshot this turn will use.
        if (route?.Swapped == true) ActiveContextWindow.Invalidate();

        try
        {
            int live = await ActiveContextWindow.GetAsync(http, providerEndpoint, cancellationToken)
                .ConfigureAwait(false);
            if (live > 0) return live;
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        string? path = route?.ModelPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        try
        {
            ContextPreferenceStore.ContextPreference? preference = ContextPreferenceStore.Get(path);
            if (preference is { ContextLength: > 0 })
                return preference.ContextLength > int.MaxValue ? int.MaxValue : (int)preference.ContextLength;

            GgufModelInfo model = GgufMetadataReader.Read(path);
            long declared = model.ContextLength ?? 0;
            long conservative = declared <= 0 ? 0 : Math.Min(declared, DefaultLaunchContextLength);
            return conservative > int.MaxValue ? int.MaxValue : (int)conservative;
        }
        catch { return 0; }
    }
}
