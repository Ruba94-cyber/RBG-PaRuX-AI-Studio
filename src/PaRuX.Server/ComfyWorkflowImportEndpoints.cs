using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace PaRuX.Server;

public sealed class ImportComfyWorkflowRequest
{
    public string Name { get; set; } = string.Empty;
    public string WorkflowJson { get; set; } = string.Empty;
    /// <summary>Explicit confirmation to replace the user's own earlier template of the same name. Built-ins are never replaced.</summary>
    public bool ReplaceExisting { get; set; }
    /// <summary>Explicit acknowledgement that the workflow may use custom (non-core) ComfyUI nodes.</summary>
    public bool AcknowledgeUnverifiedNodes { get; set; }
}

public sealed class PreviewComfyWorkflowRequest
{
    public string WorkflowJson { get; set; } = string.Empty;
    public bool AcknowledgeUnverifiedNodes { get; set; }
}

public sealed class LoraPlanRequest
{
    public string Query { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public bool IncludeNsfw { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string VersionId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
}

public sealed class LoraInstallBody
{
    public string Query { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public bool IncludeNsfw { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string VersionId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public bool Consent { get; set; }
    public string PlanHash { get; set; } = string.Empty;
}

public sealed class LoraTokenBody { public string? Token { get; set; } }

internal static class ComfyWorkflowImportEndpoints
{
    private static readonly HttpClient CatalogHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/media/comfy-workflows", ([FromServices] ComfyWorkflowImportService svc) => Results.Ok(svc.List()));

        app.MapGet("/api/v1/media/comfy-workflows/{id}", (string id, [FromServices] ComfyWorkflowImportService svc) =>
            svc.Get(id) is { } info ? Results.Ok(info) : Results.NotFound(new { error = "Unknown imported workflow." }));

        app.MapPost("/api/v1/media/comfy-workflows/preview", async (PreviewComfyWorkflowRequest request,
            [FromServices] ComfyWorkflowImportService svc, [FromServices] ComfyWorkflowRunner runner, CancellationToken ct) =>
        {
            ComfyWorkflowAnalysis a = svc.Preview(request.WorkflowJson, await TryReadCatalogAsync(runner, ct), request.AcknowledgeUnverifiedNodes);
            return Results.Ok(new
            {
                ok = a.Ok, errors = a.Errors, warnings = a.Warnings, workflowType = a.WorkflowType, saveNodeId = a.SaveNodeId,
                saveNodeClass = a.SaveNodeClass, requiresSourceImage = a.RequiresSourceImage, nodeCount = a.NodeCount,
                verifiedAgainstComfyUi = a.VerifiedAgainstComfyUi, parameters = a.Parameters
            });
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/media/comfy-workflows", async (ImportComfyWorkflowRequest request,
            [FromServices] ComfyWorkflowImportService svc, [FromServices] ComfyWorkflowRunner runner, CancellationToken ct) =>
        {
            try { return Results.Ok(svc.Save(request.Name, request.WorkflowJson, await TryReadCatalogAsync(runner, ct), request.ReplaceExisting, request.AcknowledgeUnverifiedNodes)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/media/comfy-workflows/{id}", (string id, [FromServices] ComfyWorkflowImportService svc) =>
            svc.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "Unknown imported workflow." })).RequireDesktopOwner();

        app.MapGet("/api/v1/media/loras", () => Results.Ok(LoraLibrary.List()));

        app.MapGet("/api/v1/media/loras/search", async (string q, string? baseModel, int? page, bool? includeNsfw, CancellationToken ct) =>
        {
            try
            {
                var adapter = new LoraCivitAiAdapter(CatalogHttp);
                LoraSearchPage result = await adapter.SearchAsync(new LoraSearchQuery(q, baseModel, page ?? 1, 10, includeNsfw ?? false), ct);
                return Results.Ok(result);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.Json(new { error = "The LoRA catalogue is not reachable." }, statusCode: StatusCodes.Status502BadGateway); }
        });

        // Read-only: describes what installing would do. Nothing is downloaded by this route.
        app.MapPost("/api/v1/media/loras/plan", async (LoraPlanRequest request, CancellationToken ct) =>
        {
            try
            {
                var adapter = new LoraCivitAiAdapter(CatalogHttp);
                LoraSearchPage result = await adapter.SearchAsync(new LoraSearchQuery(request.Query, null, request.Page, 20, request.IncludeNsfw), ct);
                LoraCatalogItem? item = result.Items.FirstOrDefault(i => i.ModelId == request.ModelId);
                LoraVersionInfo? version = item?.Versions.FirstOrDefault(v => v.VersionId == request.VersionId);
                LoraFileInfo? file = version?.Files.FirstOrDefault(f => f.FileId == request.FileId);
                if (item == null || version == null || file == null) return Results.NotFound(new { error = "That LoRA file was not found in the search results." });
                return Results.Ok(LoraInstaller.Plan(item, version, file));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.Json(new { error = "The LoRA catalogue is not reachable." }, statusCode: StatusCodes.Status502BadGateway); }
        }).RequireDesktopOwner();

        // Explicit, desktop-owner-only install. The plan is re-derived from the catalogue and must match the hash the user
        // reviewed; the transfer runs in the background and is verified against the plan's size and SHA-256.
        app.MapPost("/api/v1/media/loras/install", async (LoraInstallBody body, [FromServices] LoraInstallJobs jobs, CancellationToken ct) =>
        {
            try
            {
                var adapter = new LoraCivitAiAdapter(CatalogHttp);
                LoraSearchPage result = await adapter.SearchAsync(new LoraSearchQuery(body.Query, null, body.Page, 20, body.IncludeNsfw), ct);
                LoraCatalogItem? item = result.Items.FirstOrDefault(i => i.ModelId == body.ModelId);
                LoraVersionInfo? version = item?.Versions.FirstOrDefault(v => v.VersionId == body.VersionId);
                LoraFileInfo? file = version?.Files.FirstOrDefault(f => f.FileId == body.FileId);
                if (item == null || version == null || file == null) return Results.NotFound(new { error = "That LoRA file was not found in the search results." });
                LoraInstallPlan plan = LoraInstaller.Plan(item, version, file);
                var job = jobs.Start(plan, new LoraInstallRequest(body.Consent, body.PlanHash));
                return job.State == "refused" ? Results.Conflict(new { error = job.Message }) : Results.Accepted("/api/v1/media/loras/install/" + job.Id, job);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.Json(new { error = "The LoRA catalogue is not reachable." }, statusCode: StatusCodes.Status502BadGateway); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/media/loras/install/{id}", (string id, [FromServices] LoraInstallJobs jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Unknown install." })).RequireDesktopOwner();

        app.MapPost("/api/v1/media/loras/install/{id}/cancel", (string id, [FromServices] LoraInstallJobs jobs) =>
            jobs.Cancel(id) ? Results.Accepted("/api/v1/media/loras/install/" + id) : Results.NotFound(new { error = "No running install with that id." })).RequireDesktopOwner();

        // Optional CivitAI token. Write-only: no route returns it.
        app.MapPut("/api/v1/media/loras/token", (LoraTokenBody body) =>
        {
            try { LoraTokenStore.Save(ServerState.StateDirectory, (body.Token ?? string.Empty).Trim()); return Results.NoContent(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();
        app.MapDelete("/api/v1/media/loras/token", () => { LoraTokenStore.Clear(ServerState.StateDirectory); return Results.NoContent(); }).RequireDesktopOwner();
    }

    /// <summary>The live node catalogue, or null when ComfyUI is not running (the preview then says it was not verified).</summary>
    private static async Task<ComfyNodeCatalog?> TryReadCatalogAsync(ComfyWorkflowRunner runner, CancellationToken ct)
    {
        try { return await ComfyLivePreflight.ReadCatalogAsync(CatalogHttp, new Uri(runner.MediaBaseUrl), ct); }
        catch (Exception ex) when (ex is MediaDependencyException or HttpRequestException or OperationCanceledException or UriFormatException)
        { ct.ThrowIfCancellationRequested(); return null; }
    }
}
