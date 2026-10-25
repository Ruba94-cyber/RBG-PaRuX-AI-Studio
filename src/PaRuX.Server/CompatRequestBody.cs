using System.Text;

namespace PaRuX.Server;

/// <summary>
/// Bounded body reader shared by the Ollama, Responses, Messages and Chat Completions surfaces. A
/// body above <see cref="MaxBytes"/> is rejected before any parsing or model work, and each surface
/// reports it as a 413 in its own error shape (Kestrel's 64 MiB hard limit would otherwise surface a
/// shapeless transport error).
/// </summary>
internal static class CompatRequestBody
{
    /// <summary>Above the largest bounded image payload (16 Mi characters) with headroom for several images and text.</summary>
    internal const int MaxBytes = 48 * 1024 * 1024;

    /// <summary>Returns the UTF-8 body, or null after writing the surface's 413 through <paramref name="writeTooLarge"/>.</summary>
    internal static async Task<string?> ReadAsync(
        HttpRequest request, Func<int, string, Task> writeTooLarge, CancellationToken ct)
    {
        string message = "Request body exceeds the " + (MaxBytes / (1024 * 1024)) + " MiB limit.";
        if (request.ContentLength is long declared && declared > MaxBytes)
        {
            await writeTooLarge(StatusCodes.Status413PayloadTooLarge, message).ConfigureAwait(false);
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                await writeTooLarge(StatusCodes.Status413PayloadTooLarge, message).ConfigureAwait(false);
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
