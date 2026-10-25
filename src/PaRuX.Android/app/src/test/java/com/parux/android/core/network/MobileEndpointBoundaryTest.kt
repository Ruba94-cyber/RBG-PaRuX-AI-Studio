package com.parux.android.core.network

import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class MobileEndpointBoundaryTest {

    @Test
    fun `desktop-only installer raw-path and privileged V2 operations are blocked locally`() {
        listOf(
            "POST" to "/api/v1/threed/engine/install",
            "POST" to "/api/v1/training/jobs",
            "GET" to "/api/v1/training/engine",
            "POST" to "/api/v1/models/benchmarks",
            "GET" to "/api/v1/models/benchmarks/models",
            "POST" to "/api/v1/runtime/install",
            "POST" to "/api/v1/backup/import",
            "POST" to "/api/v1/mcp/servers",
            "POST" to "/api/v2/plugins/install",
            "GET" to "/api/v2/semantic-index/repositories",
            "POST" to "/api/v2/visual-context/project/p1/snapshots",
            "POST" to "/api/v2/media/engine-race/image",
            "PUT" to "/api/v2/execution-profiles/models/model-a",
            "POST" to "/api/v2/virtual-context/ledger"
        ).forEach { (method, path) ->
            assertNotNull("Expected $method $path to be desktop-only", MobileEndpointBoundary.desktopOnlyReason(method, path))
        }
    }

    @Test
    fun `intended mobile control plane including sanitized V2 remains available`() {
        listOf(
            "GET" to "/api/v1/projects",
            "POST" to "/api/v1/chat/completions",
            "POST" to "/api/v1/models/downloads",
            "POST" to "/api/v1/orchestrator/tasks",
            "POST" to "/api/v1/threed/generate",
            "POST" to "/api/v1/workflows/w1/run",
            "GET" to "/api/v2/mobile/execution-profile",
            "PUT" to "/api/v2/mobile/execution-profile/quality",
            "GET" to "/api/v2/mobile/observability/dashboard",
            "POST" to "/api/v2/mobile/goals",
            "POST" to "/api/v2/mobile/goals/tasks"
        ).forEach { (method, path) ->
            assertNull("Expected $method $path to remain mobile-capable", MobileEndpointBoundary.desktopOnlyReason(method, path))
        }
    }

    @Test
    fun `query string cannot bypass desktop-only classification`() {
        assertNotNull(MobileEndpointBoundary.desktopOnlyReason("GET", "/api/v1/models/benchmarks/models?x=1"))
        assertNotNull(MobileEndpointBoundary.desktopOnlyReason("GET", "/api/v2/semantic-index/repositories?include=all"))
    }
}
