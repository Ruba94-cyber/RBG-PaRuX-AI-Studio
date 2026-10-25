using Microsoft.AspNetCore.Http;

namespace PaRuX.Server;

/// <summary>
/// The exact set of paths an API key may call: the OpenAI dialect at /v1 and the Ollama dialect
/// beside it.
///
/// A key exists so another program can use this PaRuX as a model provider. That is all it should
/// buy. PaRuX's control plane under /api/v1 can change the LAN boundary, connect folders on this
/// PC, drive Computer Use and read saved conversations, and a key pasted into a third-party
/// application must not reach any of it. Keeping the allowed surface explicit means a route added
/// later is closed until somebody deliberately opens it.
/// </summary>
internal static class ProviderSurface
{
    private static readonly string[] ExactPaths =
    {
        "/api/version",
        "/api/tags",
        "/api/show",
        "/api/chat",
        "/api/generate",
        "/api/ps",
        "/api/embeddings",
        "/api/embed",
        // Additive, read-only protocol discovery. This contains no machine state, credentials,
        // model paths or control-plane authority; it only states which Ollama fields PaRuX can
        // translate faithfully in the current version.
        "/api/parux/capabilities"
    };

    internal static bool Covers(PathString path)
    {
        if (path.StartsWithSegments("/v1")) return true;
        string value = path.Value ?? string.Empty;
        foreach (string allowed in ExactPaths)
        {
            if (string.Equals(value, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
