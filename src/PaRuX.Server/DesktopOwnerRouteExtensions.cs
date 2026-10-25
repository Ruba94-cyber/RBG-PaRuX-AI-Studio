using System.Net;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Desktop-owner gate for a mutating route that changes this machine: its runtimes and installed software, its
/// configuration, its keys and integrations, or the files it reads and writes. Only the desktop itself passes.
/// A browser session is refused even when a same-machine reverse proxy delivers it, because that request also
/// arrives from 127.0.0.1; paired devices and API keys are refused too. Routes that the browser client or the
/// paired-device allowlist already serves are deliberately not marked, so they behave exactly as before.
/// </summary>
internal static class DesktopOwnerRouteExtensions
{
    /// <summary>
    /// Refuses every principal except the desktop owner with 403 before the handler runs. The handler is untouched,
    /// so the desktop reaches it exactly as before.
    /// </summary>
    internal static RouteHandlerBuilder RequireDesktopOwner(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            if (IsDesktopOwner(invocation.HttpContext)) return await next(invocation);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });

    /// <summary>
    /// Keeps ordinary downloads available to paired devices while requiring the desktop owner before
    /// a request can discard a partial or replace a completed managed model.
    /// </summary>
    internal static RouteHandlerBuilder RequireDesktopOwnerForDestructiveModelDownload(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            ModelDownloadRequest? request = invocation.Arguments.OfType<ModelDownloadRequest>().FirstOrDefault();
            if (request is not null
                && (request.DiscardPartial || request.ReplaceExisting)
                && !IsDesktopOwner(invocation.HttpContext))
            {
                return Results.Problem(
                    detail: "Discarding a partial download or replacing a managed model requires the desktop owner.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Desktop owner required",
                    type: "about:blank",
                    extensions: new Dictionary<string, object?> { ["code"] = "desktop_owner_required" });
            }

            return await next(invocation);
        });

    /// <summary>
    /// The one predicate for desktop-only authority. Loopback alone is not evidence of the desktop: a browser session
    /// that reaches PaRuX through a same-machine reverse proxy arrives from 127.0.0.1 too, so the owner that the global
    /// gate stamped must also be the desktop itself.
    /// </summary>
    internal static bool IsDesktopOwner(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)
        && context.Items["PaRuXOwner"] as string == "desktop";
}
