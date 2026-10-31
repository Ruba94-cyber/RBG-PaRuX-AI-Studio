using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public static class GamingEndpoints
{
    public static void MapGamingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/library", (ManualGameLibrary library) =>
        {
            try { return Results.Ok(library.List()) as IResult; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be read."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library", (AddManualGameRequest request, ManualGameLibrary library) =>
        {
            try { return Results.Ok(library.Add(request)) as IResult; }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid game name or executable path." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "Game already exists or the library is full." }); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be saved."); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/library/{id}", (string id, ManualGameLibrary library) =>
        {
            try { return library.Remove(id) ? Results.NoContent() : Results.NotFound() as IResult; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Results.Problem("The local game library could not be saved."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/status", (WorkloadOrchestrator orchestrator) =>
            Results.Ok(orchestrator.GetStatus()));

        app.MapPost("/api/v1/gaming/enter", async (GamingActionRequest request, WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.EnterGamingSessionAsync(request.ProfileName, ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/exit", async (WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.ExitGamingSessionAsync(ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/suspend", async (WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.SuspendActiveGameAsync(ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/resume", async (WorkloadOrchestrator orchestrator, CancellationToken ct) =>
        {
            bool success = await orchestrator.ResumeSuspendedGameAsync(ct);
            return success ? Results.Ok(orchestrator.GetStatus()) : Results.BadRequest(orchestrator.GetStatus());
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/profiles", (GamingProfileStore profiles) =>
            Results.Ok(profiles.List()));

        app.MapPost("/api/v1/gaming/profiles", (GamingProfileDto profile, GamingProfileStore profiles) =>
        {
            GamingProfileDto saved = profiles.Save(profile);
            return Results.Ok(saved);
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/profiles/{id}", (string id, GamingProfileStore profiles) =>
        {
            bool deleted = profiles.Delete(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/providers", (WorkloadOrchestrator orchestrator) =>
            Results.Ok(orchestrator.GetProviderStatuses()));
    }
}
