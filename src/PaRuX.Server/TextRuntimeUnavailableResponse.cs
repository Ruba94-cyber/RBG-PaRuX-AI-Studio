namespace PaRuX.Server;

/// <summary>
/// One typed 503 for <see cref="TextRuntimeStateUnavailableException"/> everywhere it can surface (model load, media
/// start, speech, chat start, runtime actions), so clients never see an empty 500 or an untyped message for the same
/// condition. The body shape matches the original /models/library/{f}/load response.
/// </summary>
internal static class TextRuntimeUnavailableResponse
{
    internal static object Body(TextRuntimeStateUnavailableException exception) =>
        new { success = false, error = TextRuntimeStateUnavailableException.ErrorCode, message = exception.Message };

    internal static IResult Result(TextRuntimeStateUnavailableException exception) =>
        Results.Json(Body(exception), statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>Writes the typed 503 when the response has not started; returns false when it already has.</summary>
    internal static async Task<bool> TryWriteAsync(HttpResponse response, TextRuntimeStateUnavailableException exception,
        CancellationToken cancellationToken)
    {
        if (response.HasStarted) return false;
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await response.WriteAsJsonAsync(Body(exception), cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Safety net for any endpoint that lets the exception escape: answer the typed 503 instead of an empty 500.</summary>
internal sealed class TextRuntimeUnavailableMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context).ConfigureAwait(false); }
        catch (TextRuntimeStateUnavailableException exception) when (!context.Response.HasStarted)
        {
            await TextRuntimeUnavailableResponse.TryWriteAsync(context.Response, exception, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
