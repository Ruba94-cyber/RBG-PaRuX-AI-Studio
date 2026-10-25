using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Maps the external-model import handler with ASP.NET's request-aborted token binding.</summary>
public static class ExternalModelImportEndpoint
{
    public const string Path = "/api/v1/models/library/import-external";

    public static RouteHandlerBuilder Map(
        IEndpointRouteBuilder endpoints,
        Func<ExternalModelStoreImporter.StoreRoots> roots,
        Func<string> managedDirectory) =>
        endpoints.MapPost(Path,
            (ExternalModelImportRequest request, CancellationToken requestAborted) =>
            {
                try
                {
                    return Results.Ok(ExternalModelStoreImporter.Import(
                        request.SourcePath, request.CopyConsent, roots(), managedDirectory(),
                        cancellationToken: requestAborted));
                }
                catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
                catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
            });
}
