using Microsoft.AspNetCore.Http;

namespace PaRuX.Server;

internal static class MediaJobStatusEndpoints
{
    public static IResult GetById(string id, MediaJobService jobs) =>
        jobs.Get(id) is { } job
            ? Results.Ok(job)
            : Results.NotFound(new { error = "Job not found" });
}
