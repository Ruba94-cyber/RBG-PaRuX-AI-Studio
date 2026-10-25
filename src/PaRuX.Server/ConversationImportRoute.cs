using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PaRuX.Server;

/// <summary>Maps POST /api/v1/conversations/import: the raw export file (JSON or zip) as the request body.</summary>
public static class ConversationImportRoute
{
    public const string Path = "/api/v1/conversations/import";

    public static RouteHandlerBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, async (HttpRequest request, ConversationStore store, string? fileName, CancellationToken ct) =>
        {
            if (request.ContentLength is > ConversationImporter.MaximumInputBytes)
                return Results.Json(new { error = "The file is larger than the import limit." }, statusCode: StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > ConversationImporter.MaximumInputBytes)
                    return Results.Json(new { error = "The file is larger than the import limit." }, statusCode: StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, read);
            }
            var report = ConversationImporter.ImportInto(store, buffer.ToArray(), fileName ?? string.Empty);
            var body = new
            {
                report.Source, report.Imported, report.Skipped, report.SkipReasons, report.Error, Summary = report.Summary()
            };
            return report.Error != null ? Results.BadRequest(body) : Results.Ok(body);
        });
}
