using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The sign-in surface for the browser client: the login page, and the three calls that create,
/// inspect and end a browser session.
///
/// These are the only paths PaRuX serves without credentials, and only while browser access is
/// switched on. Everything else - including the client itself at /app - is behind the session.
/// </summary>
public static class WebAccessEndpoints
{
    internal const string LoginPath = "/app/login";
    internal const string SessionPath = "/api/v1/web/session";

    /// <summary>
    /// Reachable without a session from loopback only. Deliberately tiny: the page that collects
    /// the passphrase, and the call that checks it. Both are rate-limited by the service's lockout.
    /// </summary>
    internal static bool IsPublicPath(HttpRequest request, bool loopback) =>
        loopback && ((request.Path == LoginPath && HttpMethods.IsGet(request.Method))
            || (request.Path == SessionPath && HttpMethods.IsPost(request.Method)));

    /// <summary>
    /// A person typing an address into a browser, as opposed to a script or a CLI calling the API.
    /// Only these are redirected to the sign-in page; everything else keeps the 401 it can handle,
    /// so a redirect never turns a machine client's clear failure into a confusing page of HTML.
    /// </summary>
    internal static bool IsBrowserNavigation(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;
        if (request.Path.StartsWithSegments("/api")) return false;
        string accept = request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    public static void MapWebAccessEndpoints(this WebApplication app)
    {
        // The login page. Served only when browser access is on, so a PaRuX that never enabled it
        // does not advertise that the feature exists.
        app.MapGet(LoginPath, (HttpContext context, WebAccessService web) =>
        {
            if (!web.Enabled) return Results.NotFound();
            ApplyClientHeaders(context);
            return Results.Content(WebClientEndpoint.LoginHtml, "text/html; charset=utf-8");
        });

        app.MapPost(SessionPath, (WebSessionRequest request, HttpContext context, WebAccessService web) =>
        {
            if (!web.Enabled) return Results.NotFound();

            if (web.IsLockedOut(out TimeSpan retryAfter))
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                return Results.Json(
                    new { error = "Too many failed attempts. Try again in " + Math.Ceiling(retryAfter.TotalMinutes) + " minute(s)." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            WebAccessService.Session? session = web.TryAuthenticate(request?.Passphrase);
            if (session == null)
                return Results.Json(new { error = "That passphrase is not correct." }, statusCode: StatusCodes.Status401Unauthorized);

            context.Response.Cookies.Append(WebAccessService.SessionCookieName, session.Id, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true
            });

            // The CSRF token is returned in the body, not a cookie: the page keeps it in memory and
            // echoes it in a header, which is the half a cross-site attacker cannot reproduce.
            return Results.Ok(new WebSessionResponse { Authenticated = true, CsrfToken = session.CsrfToken });
        });

        app.MapGet(SessionPath, (HttpContext context) =>
        {
            if (context.Items["PaRuXWebSession"] is not WebAccessService.Session session)
                return Results.Ok(new WebSessionResponse { Authenticated = false });
            return Results.Ok(new WebSessionResponse { Authenticated = true, CsrfToken = session.CsrfToken });
        });

        app.MapDelete(SessionPath, (HttpContext context, WebAccessService web) =>
        {
            web.Revoke(context.Request.Cookies[WebAccessService.SessionCookieName]);
            context.Response.Cookies.Delete(WebAccessService.SessionCookieName, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
            return Results.NoContent();
        });
    }

    /// <summary>
    /// The same headers the client itself is served with. The page is tested for having no external
    /// references; this makes that a property the browser enforces rather than one a test observed.
    /// </summary>
    internal static void ApplyClientHeaders(HttpContext context)
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; "
            + "connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; "
            + "frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers.CacheControl = "no-store";
    }
}
