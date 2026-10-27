using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record SpeechSynthesisArtifactResult(string FileName, double DurationSeconds, bool ArtifactRegistered, long? TtfaMilliseconds = null);

    public sealed class SpeechClient
    {
        public static SpeechClient Current { get; } = new SpeechClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(3));
        private static readonly JsonSerializerOptions StreamJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        private SpeechClient() { }

        public Task<SpeechEngineStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<SpeechEngineStatus>(HttpMethod.Get, "/api/v1/speech/engine", null, ct);

        /// <summary>
        /// Synthesizes text and returns the WAV bytes only. Unlike <see cref="SynthesizeAsync"/> it
        /// writes no managed file and registers no Artifact, so transient playback (Read aloud)
        /// does not fill Artifact Workspace.
        /// </summary>
        public async Task<byte[]> SynthesizeBytesAsync(string text, string? voice, CancellationToken ct)
        {
            SynthesizeResponse result = await SendAsync<SynthesizeResponse>(HttpMethod.Post, "/api/v1/speech/synthesize",
                new SynthesizeRequest { Text = text, Voice = voice ?? string.Empty }, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Base64Data))
                throw new LocalAiException("The speech engine returned no audio.");
            return Convert.FromBase64String(result.Base64Data);
        }

        public async Task<SynthesizeResponse> SynthesizeAsync(string text, string? voice, CancellationToken ct)
        {
            SynthesizeResponse result = await SendAsync<SynthesizeResponse>(HttpMethod.Post, "/api/v1/speech/synthesize",
                new SynthesizeRequest { Text = text, Voice = voice ?? string.Empty }, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.Base64Data))
            {
                try
                {
                    byte[] audioBytes = Convert.FromBase64String(result.Base64Data);
                    AppPaths.EnsureCreated();
                    StorageQuotaService.EnsureCapacity(audioBytes.LongLength);
                    string managedPath = Path.Combine(AppPaths.GeneratedAudioDir, Guid.NewGuid().ToString("N") + ".wav");
                    await File.WriteAllBytesAsync(managedPath, audioBytes, CancellationToken.None).ConfigureAwait(false);
                    await ArtifactCaptureService.Current.CaptureFileAsync(
                        managedPath,
                        "audio",
                        string.IsNullOrWhiteSpace(result.FileName) ? "speech.wav" : result.FileName,
                        "speech-synthesis",
                        new
                        {
                            voice = voice ?? string.Empty,
                            mediaType = result.MediaType,
                            durationSeconds = result.DurationSeconds,
                            textLength = text?.Length ?? 0
                        },
                        markFinal: true,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Write("Speech artifact capture skipped after successful synthesis: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Opens the low-latency v2 Kokoro stream. The response stays open until a done/error frame
        /// or cancellation. Request-id and sequence validation is duplicated client-side so a stale
        /// frame can never be played merely because a server/transport regression emitted it.
        /// </summary>
        public async IAsyncEnumerable<SpeechStreamFrame> StreamSynthesizeAsync(
            string text,
            string? voice,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                server.Endpoint.TrimEnd('/') + "/api/v2/speech/synthesize/stream");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(new SynthesizeRequest
            {
                Text = text ?? string.Empty,
                Voice = voice ?? string.Empty
            });

            using HttpResponseMessage response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Continuous speech request failed (" + (int)response.StatusCode + "): " + detail);
            }

            string expectedRequestId = response.Headers.TryGetValues("X-PaRuX-Request-Id", out IEnumerable<string>? ids)
                ? string.Join(string.Empty, ids)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(expectedRequestId))
                throw new LocalAiException("PaRuX Core did not identify the continuous speech stream.");

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            int expectedSequence = 0;
            bool sawStart = false;
            while (true)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                    throw new LocalAiException("Continuous speech stream ended without a terminal frame.");
                if (string.IsNullOrWhiteSpace(line)) continue;

                SpeechStreamFrame? frame;
                try { frame = JsonSerializer.Deserialize<SpeechStreamFrame>(line, StreamJson); }
                catch (JsonException ex) { throw new LocalAiException("PaRuX Core returned an invalid continuous speech frame: " + ex.Message); }
                if (frame == null)
                    throw new LocalAiException("PaRuX Core returned an empty continuous speech frame.");
                if (!string.Equals(frame.RequestId, expectedRequestId, StringComparison.Ordinal))
                    throw new LocalAiException("Continuous speech stream returned a frame for a different request.");
                if (frame.Sequence != expectedSequence)
                    throw new LocalAiException(
                        "Continuous speech sequence mismatch: expected " + expectedSequence + ", received " + frame.Sequence + ".");

                if (!sawStart)
                {
                    if (!string.Equals(frame.Type, "start", StringComparison.Ordinal))
                        throw new LocalAiException("Continuous speech stream did not begin with a start frame.");
                    sawStart = true;
                }
                else if (string.Equals(frame.Type, "start", StringComparison.Ordinal))
                {
                    throw new LocalAiException("Continuous speech stream emitted a duplicate start frame.");
                }

                expectedSequence++;
                yield return frame;

                if (string.Equals(frame.Type, "done", StringComparison.Ordinal)
                    || string.Equals(frame.Type, "error", StringComparison.Ordinal))
                    yield break;
                if (!string.Equals(frame.Type, "audio", StringComparison.Ordinal)
                    && !string.Equals(frame.Type, "start", StringComparison.Ordinal))
                    throw new LocalAiException("Continuous speech stream returned unsupported frame type '" + frame.Type + "'.");
            }
        }

        /// <summary>
        /// Stores continuous PCM frames directly as a local WAV instead of retaining a complete
        /// base64 response and decoded byte array in memory. The file is exposed to Artifact
        /// Workspace only after a valid terminal done frame has finalized the WAV header.
        /// </summary>
        public async Task<SpeechSynthesisArtifactResult> SynthesizeStreamToArtifactAsync(
            string text,
            string? voice,
            CancellationToken ct,
            IProgress<long>? ttfaProgress = null)
        {
            AppPaths.EnsureCreated();
            string managedPath = Path.Combine(AppPaths.GeneratedAudioDir, Guid.NewGuid().ToString("N") + ".wav");
            SpeechPcmWavStreamResult audio;
            long? observedTtfa = null;
            try
            {
                async IAsyncEnumerable<SpeechStreamFrame> TrackFrames(
                    IAsyncEnumerable<SpeechStreamFrame> frames,
                    [EnumeratorCancellation] CancellationToken token)
                {
                    await foreach (SpeechStreamFrame frame in frames.WithCancellation(token).ConfigureAwait(false))
                    {
                        if (frame != null)
                        {
                            if (!observedTtfa.HasValue && string.Equals(frame.Type, "audio", StringComparison.Ordinal))
                            {
                                observedTtfa = frame.TtfaMilliseconds;
                                if (frame.TtfaMilliseconds > 0)
                                {
                                    ttfaProgress?.Report(frame.TtfaMilliseconds);
                                }
                            }
                            yield return frame;
                        }
                    }
                }

                audio = await SpeechPcmWavStreamWriter.WriteAsync(
                    TrackFrames(StreamSynthesizeAsync(text, voice, ct), ct),
                    managedPath,
                    ct).ConfigureAwait(false);

                StorageQuotaService.EnsureCapacity(audio.FileBytes);
            }
            catch
            {
                try { if (File.Exists(managedPath)) File.Delete(managedPath); } catch { }
                throw;
            }

            ArtifactDto? artifact = null;
            try
            {
                artifact = await ArtifactCaptureService.Current.CaptureFileAsync(
                    managedPath,
                    "audio",
                    "speech.wav",
                    "speech-synthesis",
                    new
                    {
                        voice = voice ?? string.Empty,
                        mediaType = "audio/wav",
                        durationSeconds = audio.DurationSeconds,
                        textLength = text?.Length ?? 0,
                        streamedPcm = true,
                        ttfaMilliseconds = observedTtfa
                    },
                    markFinal: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Speech artifact registration skipped after successful synthesis: " + ex.Message);
            }

            return new SpeechSynthesisArtifactResult("speech.wav", audio.DurationSeconds, artifact != null, observedTtfa);
        }

        public Task<TranscribeResponse> TranscribeAsync(string audioBase64, string fileName, CancellationToken ct) =>
            SendAsync<TranscribeResponse>(HttpMethod.Post, "/api/v1/speech/transcribe",
                new TranscribeRequest { AudioBase64 = audioBase64, FileName = fileName }, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Speech request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty speech response.");
            return value;
        }
    }
}
