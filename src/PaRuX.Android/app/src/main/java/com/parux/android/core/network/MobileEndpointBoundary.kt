package com.parux.android.core.network

/**
 * UX mirror of Core's authoritative paired-device capability boundary. Security is enforced by
 * PaRuX Core; this object only prevents the Android UI from needlessly sending operations that are
 * intentionally desktop-only and returns a useful local explanation instead of a generic HTTP 403.
 */
object MobileEndpointBoundary {
    fun desktopOnlyReason(method: String, encodedPath: String): String? {
        val verb = method.uppercase()
        val path = encodedPath.substringBefore('?').trimEnd('/').ifEmpty { "/" }

        if (verb == "POST" && path == "/api/v1/threed/engine/install")
            return "Install the 3D engine from PaRuX Windows. Android can use it after the workstation has configured it."
        if (path.startsWith("/api/v1/training/"))
            return "Training setup and dataset-path selection stay on PaRuX Windows. Android supervises workstation tasks without accepting raw Windows paths."
        if (path.startsWith("/api/v1/models/benchmarks"))
            return "Run model benchmarks from PaRuX Windows. Android model routing already receives sanitized measured performance evidence."
        if (path.startsWith("/api/v1/runtime/") || path.startsWith("/api/v1/components/"))
            return "Runtime and component installation is a desktop-only PaRuX Windows operation."
        if (path.startsWith("/api/v1/backup/") || path.startsWith("/api/v1/mcp/") || path.startsWith("/api/v1/local-api/"))
            return "This administrative capability is available only in PaRuX Windows."

        if (path.startsWith("/api/v2/plugins/"))
            return "Plugin inspection, trust and installation remain owned by PaRuX Windows."
        if (path.startsWith("/api/v2/semantic-index/"))
            return "Repository/index administration stays on Windows because it operates on workstation repositories and paths."
        if (path.startsWith("/api/v2/visual-context/"))
            return "Visual Context authoring is desktop-owned. Android can supervise resulting tasks without receiving raw desktop capture authority."
        if (path.startsWith("/api/v2/media/engine-race"))
            return "Engine Race and manual performance benchmarking run only on the Windows workstation GPU."
        if (path.startsWith("/api/v2/execution-profiles"))
            return "Android uses the sanitized mobile Execution Profile projection. Generic/per-model V2 tuning remains workstation-owned."
        if (path.startsWith("/api/v2/observability"))
            return "Android uses the sanitized mobile observability projection so workstation model paths never cross the LAN boundary."
        if (path.startsWith("/api/v2/goals"))
            return "Android uses the mobile Goal Mode projection, which strips caller-controlled capability grants and workstation task details."
        if (path.startsWith("/api/v2/virtual-context/ledger"))
            return "Virtual Context ledger authoring remains Core/desktop-owned; Android receives the resulting conversation/task behavior."
        return null
    }
}
