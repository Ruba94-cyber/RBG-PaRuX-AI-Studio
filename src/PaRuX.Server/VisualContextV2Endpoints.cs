using PaRuX.Contracts;

namespace PaRuX.Server;

public static class VisualContextV2Endpoints
{
    public static void MapVisualContextV2Endpoints(this WebApplication app)
    {
        app.MapPost("/api/v2/visual-context/snapshots", (CreateVisualSnapshotRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                VisualSnapshotDto snapshot = VisualContextV2Store.Current.CreateSnapshot(request);
                if (string.Equals(snapshot.Source, "computer-use", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(snapshot.Source, "ui-verification", StringComparison.OrdinalIgnoreCase))
                    _ = new VisualContextRetentionService().PruneGeneratedEvidence();
                return Results.Ok(snapshot);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/visual-context/retention", (HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(new VisualContextRetentionService().GetStatus()); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v2/visual-context/retention/prune", (HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(new VisualContextRetentionService().PruneGeneratedEvidence()); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/visual-context/{scope}/{scopeId}/snapshots", (
            string scope, string scopeId, int? limit, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(VisualContextV2Store.Current.List(scope, scopeId, limit ?? 50)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/visual-context/snapshots/{id}", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                VisualSnapshotDetailDto? detail = VisualContextV2Store.Current.GetDetail(id);
                return detail == null ? Results.NotFound() : Results.Ok(detail);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/v2/visual-context/snapshots/{id}", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return VisualContextV2Store.Current.DeleteSnapshot(id) ? Results.NoContent() : Results.NotFound(); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v2/visual-context/snapshots/{id}/annotations", (
            string id, AddVisualRegionRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(VisualContextV2Store.Current.AddAnnotation(id, request)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v2/visual-context/comparisons", (CreateVisualComparisonRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(VisualContextV2Store.Current.CreateComparison(request)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/visual-context/{scope}/{scopeId}/bundle", (
            string scope, string scopeId, int? maxSnapshots, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(VisualContextV2Store.Current.BuildBundle(scope, scopeId, maxSnapshots ?? 4)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static bool IsDesktopOwner(HttpContext context) =>
        string.Equals(context.Items["PaRuXOwner"] as string, "desktop", StringComparison.Ordinal);
}
