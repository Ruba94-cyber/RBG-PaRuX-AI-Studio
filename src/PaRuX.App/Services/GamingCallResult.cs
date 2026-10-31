using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Outcome of one gaming control-plane call. A refused or unreachable request carries its reason,
    /// so the page can show it instead of treating every failure as a silent null.
    /// </summary>
    public sealed class GamingCallResult<T>
    {
        private GamingCallResult(T? value, string? error)
        {
            Value = value;
            Error = error;
        }

        public T? Value { get; }

        /// <summary>Null when the call succeeded; otherwise the reason it did not.</summary>
        public string? Error { get; }

        public bool Succeeded => Error is null;

        public static GamingCallResult<T> Ok(T? value) => new(value, null);

        public static GamingCallResult<T> Fail(string error) => new(default, error);
    }

    /// <summary>Reads gaming control-plane responses into <see cref="GamingCallResult{T}"/>.</summary>
    public static class GamingResponseReader
    {
        public static async Task<GamingCallResult<T>> ReadAsync<T>(
            HttpResponseMessage response, JsonSerializerOptions options, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return GamingCallResult<T>.Fail(DescribeRefusal(response.StatusCode, body));
            }

            if (response.StatusCode == HttpStatusCode.NoContent) return GamingCallResult<T>.Ok(default);

            T? value = await response.Content.ReadFromJsonAsync<T>(options, ct).ConfigureAwait(false);
            return GamingCallResult<T>.Ok(value);
        }

        /// <summary>
        /// Uses the message the server returned with a refusal ("message", "error" or "title", any casing),
        /// otherwise names the HTTP status. Refusals from the gaming endpoints carry the status object,
        /// whose "message" field says why the request was refused. Never returns an empty string.
        /// </summary>
        public static string DescribeRefusal(HttpStatusCode status, string? body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (string preferred in new[] { "message", "error", "title" })
                        {
                            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                            {
                                if (!string.Equals(property.Name, preferred, StringComparison.OrdinalIgnoreCase)) continue;
                                if (property.Value.ValueKind != JsonValueKind.String) continue;
                                string? text = property.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Not JSON: fall through to the status-based wording.
                }
            }

            return "The gaming service refused the request (HTTP " + (int)status + ").";
        }
    }

    /// <summary>Wording for a gaming action the server refused, as the Settings page shows it.</summary>
    public static class GamingActionNotice
    {
        /// <summary>The server's reason when it gave one; otherwise a pointer to the log. Never empty.</summary>
        public static string Refused(string action, string? reason) =>
            string.IsNullOrWhiteSpace(reason)
                ? action + " did not complete. See the log for details."
                : action + " did not complete: " + reason.Trim();
    }
}
