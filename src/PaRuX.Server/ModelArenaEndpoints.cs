using PaRuX.Contracts;

namespace PaRuX.Server;

internal static class ModelArenaEndpoints
{
    internal static void AddModelArenaServices(this IServiceCollection services)
    {
        services.AddSingleton(_ => new LocalBenchmarkHistoryStore(ServerState.StateDirectory));
        services.AddSingleton(_ => new ModelArenaStore(ServerState.StateDirectory));
        services.AddSingleton(sp =>
        {
            ModelDownloadService models = sp.GetRequiredService<ModelDownloadService>();
            RuntimeManagerService runtime = sp.GetRequiredService<RuntimeManagerService>();
            LocalAiProcessManager process = sp.GetRequiredService<LocalAiProcessManager>();
            var gpuInventory = new GpuInventoryService();
            return new ModelArenaService(
                new LlamaServerArenaRunner(process),
                sp.GetRequiredService<ModelArenaStore>(),
                sp.GetRequiredService<LocalBenchmarkHistoryStore>(),
                name => models.ListManagedModels().FirstOrDefault(m =>
                    string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase))?.FilePath,
                () => process.GamingActive,
                () =>
                {
                    long ram = 0;
                    try { ram = runtime.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult()?.RamTotalBytes ?? 0; }
                    catch (Exception) { }
                    return LocalBenchmarkIdentity.HardwareDigest(gpuInventory.GetStatus(), ram, LocalBenchmarkIdentity.CpuLabel());
                });
        });
    }

    internal static void MapModelArenaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/models/arena/tasks", (ModelArenaService arena) => Results.Ok(arena.Tasks()));
        app.MapGet("/api/v1/models/arena/runs", (ModelArenaService arena) => Results.Ok(arena.List()));
        app.MapGet("/api/v1/models/arena/evidence", (ModelArenaService arena) => Results.Ok(arena.GetEvidence()));
        app.MapGet("/api/v1/models/arena/runs/{id}", (string id, ModelArenaService arena) =>
            arena.Get(id) is { } run ? Results.Ok(run) : Results.NotFound(new { error = "Comparison run not found" }));

        app.MapPost("/api/v1/models/arena/runs", (ArenaStartRequest request, ModelArenaService arena) =>
        {
            try { return Results.Accepted("/api/v1/models/arena/runs", arena.Start(request)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/models/arena/runs/{id}/cancel", (string id, ModelArenaService arena) =>
            arena.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active comparison run not found" })).RequireDesktopOwner();

        app.MapPost("/api/v1/models/arena/runs/{id}/judge", (string id, ArenaJudgeRequest request, ModelArenaService arena) =>
        {
            try { return arena.Judge(id, request) is { } run ? Results.Ok(run) : Results.NotFound(new { error = "Comparison run not found" }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();
    }
}
