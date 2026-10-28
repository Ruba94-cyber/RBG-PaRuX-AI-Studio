using System.Diagnostics;

namespace PaRuX.Server;

/// <summary>
/// Environment PaRuX gives the ComfyUI process. ComfyUI's own Python files (comfy/, comfy_extras/, main.py) are not shipped
/// pre-compiled; with PYTHONDONTWRITEBYTECODE inherited from the parent (developer shells, CI, some launchers) every start
/// recompiles all of them, which measured 25 s of a 33 s cold start against 13 s once the bytecode cache exists.
/// </summary>
internal static class ComfyLaunchEnvironment
{
    internal static void Apply(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        // Python treats any non-empty value as "do not write .pyc"; removing it restores the default (write and reuse __pycache__).
        psi.Environment.Remove("PYTHONDONTWRITEBYTECODE");
        // A redirected pycache prefix would send the cache somewhere PaRuX did not choose; the default sits beside the sources.
        psi.Environment.Remove("PYTHONPYCACHEPREFIX");
    }
}
