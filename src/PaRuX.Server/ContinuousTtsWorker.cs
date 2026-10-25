using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// One process-wide Kokoro streaming resident per LocalRuntimeService. The Python generator emits
/// PCM chunks as Kokoro produces them; no full-wave assembly exists on this path. Requests are
/// serialized because the resident KPipeline is not assumed re-entrant. Cancellation/barge-in or
/// Gaming Mode kills the child instead of draining potentially stale audio; the next turn starts a
/// clean worker and reloads Kokoro.
/// </summary>
internal sealed class ContinuousTtsWorker : IDisposable, IContinuousSpeechStreamWorker
{
    private static readonly ConditionalWeakTable<LocalRuntimeService, ContinuousTtsWorker> Workers = new();
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    private readonly LocalRuntimeService _runtime;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _processGate = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private CancellationTokenRegistration _gamingRegistration;
    private bool _disposed;

    private static string RuntimeRoot => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "speech");
    private static string VenvRoot => Path.Combine(RuntimeRoot, ".venv");
    private static string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private static string ScriptPath => Path.Combine(RuntimeRoot, "parux_tts_stream.py");

    private ContinuousTtsWorker(LocalRuntimeService runtime) => _runtime = runtime;

    internal static ContinuousTtsWorker For(LocalRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return Workers.GetValue(runtime, static key => new ContinuousTtsWorker(key));
    }

    internal async Task StreamAsync(
        SynthesizeRequest request,
        string requestId,
        Func<SpeechStreamFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        string voice = SpeechStreamingProtocol.NormalizeVoice(request);
        if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidDataException("A streaming request id is required.");
        ArgumentNullException.ThrowIfNull(onFrame);
        ThrowIfUnavailable();

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _runtime.GamingCancellationToken);
            turnCts.CancelAfter(TurnTimeout);
            CancellationToken turnToken = turnCts.Token;
            var ttfaClock = Stopwatch.StartNew();

            try
            {
                Process process = await EnsureStartedAsync(turnToken).ConfigureAwait(false);
                string command = JsonSerializer.Serialize(new
                {
                    apiVersion = SpeechStreamingProtocol.ApiVersion,
                    id = requestId,
                    op = "synthesize",
                    text = request.Text,
                    voice
                });
                await process.StandardInput.WriteLineAsync(command.AsMemory(), turnToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(turnToken).ConfigureAwait(false);

                int expectedSequence = 1;
                bool sawAudio = false;
                while (true)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(turnToken).ConfigureAwait(false);
                    if (line == null)
                        throw new InvalidDataException(
                            "Continuous speech worker exited before the request reached a terminal frame: " + Trim(ReadStderr()));

                    SpeechStreamFrame frame = SpeechStreamingProtocol.ParseWorkerFrame(
                        line, requestId, expectedSequence);
                    expectedSequence++;

                    if (frame.Type == "audio")
                    {
                        if (!sawAudio)
                        {
                            sawAudio = true;
                            frame.TtfaMilliseconds = ttfaClock.ElapsedMilliseconds;
                        }
                        await onFrame(frame, turnToken).ConfigureAwait(false);
                        continue;
                    }

                    if (frame.Type == "error")
                        throw new InvalidOperationException(frame.Error ?? "Continuous speech worker failed.");

                    if (frame.Type == "done")
                    {
                        if (!sawAudio && frame.Samples > 0)
                            throw new InvalidDataException("Continuous speech worker reported samples without emitting audio frames.");
                        await onFrame(frame, turnToken).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
            {
                KillCurrentProcess();
                cancellationToken.ThrowIfCancellationRequested();
                if (_runtime.GamingMode)
                    throw new InvalidOperationException("Gaming Mode interrupted continuous speech and released its resident worker.");
                throw new TimeoutException("Continuous speech exceeded the three-minute turn budget and its resident worker was reset.");
            }
            catch
            {
                // A protocol or synthesis failure invalidates stream synchronization. Never reuse
                // that resident process for a later turn where stale frames could be misattributed.
                KillCurrentProcess();
                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    Task IContinuousSpeechStreamWorker.StreamAsync(
        SynthesizeRequest request,
        string requestId,
        Func<SpeechStreamFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken) =>
        StreamAsync(request, requestId, onFrame, cancellationToken);

    internal void Stop() => KillCurrentProcess();

    private async Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_processGate)
        {
            if (_process is { HasExited: false } healthy) return healthy;
        }

        KillCurrentProcess();
        ThrowIfUnavailable();
        Directory.CreateDirectory(RuntimeRoot);
        await File.WriteAllTextAsync(ScriptPath, PythonScriptSource, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var info = new ProcessStartInfo
        {
            FileName = VenvPython,
            WorkingDirectory = RuntimeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(ScriptPath);
        ManagedPythonRuntime.ApplyEnvironment(info);
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["DO_NOT_TRACK"] = "1";
        info.Environment["TOKENIZERS_PARALLELISM"] = "false";

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) => AppendStderr(args.Data);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("PaRuX could not start the continuous Kokoro worker.");
        }
        ChildProcessJob.Assign(process);
        process.BeginErrorReadLine();
        lock (_processGate) _process = process;

        _gamingRegistration.Dispose();
        _gamingRegistration = _runtime.GamingCancellationToken.Register(
            static state => ((ContinuousTtsWorker)state!).KillCurrentProcess(), this);

        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _runtime.GamingCancellationToken);
        readyCts.CancelAfter(ReadyTimeout);
        try
        {
            string? readyLine = await process.StandardOutput.ReadLineAsync(readyCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(readyLine))
                throw new InvalidDataException("Continuous Kokoro worker exited before readiness: " + Trim(ReadStderr()));
            using JsonDocument document = JsonDocument.Parse(readyLine);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type)
                || !string.Equals(type.GetString(), "ready", StringComparison.Ordinal)
                || !root.TryGetProperty("apiVersion", out JsonElement version)
                || !string.Equals(version.GetString(), SpeechStreamingProtocol.ApiVersion, StringComparison.Ordinal)
                || !root.TryGetProperty("sampleRate", out JsonElement sampleRate)
                || sampleRate.GetInt32() != SpeechStreamingProtocol.SampleRate
                || !root.TryGetProperty("channels", out JsonElement channels)
                || channels.GetInt32() != SpeechStreamingProtocol.Channels
                || !root.TryGetProperty("format", out JsonElement format)
                || !string.Equals(format.GetString(), SpeechStreamingProtocol.Format, StringComparison.Ordinal))
                throw new InvalidDataException("Continuous Kokoro worker returned an incompatible readiness frame.");
            return process;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_runtime.GamingMode)
        {
            KillCurrentProcess();
            throw new TimeoutException("Continuous Kokoro worker did not become ready within three minutes.");
        }
        catch
        {
            KillCurrentProcess();
            throw;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active, so continuous speech is unavailable.");
        if (!ManagedPythonRuntime.IsManagedVenv(VenvRoot) || !File.Exists(VenvPython))
            throw new InvalidOperationException("The PaRuX-managed speech engine is not installed on this machine yet.");
    }

    private void KillCurrentProcess()
    {
        Process? process;
        lock (_processGate)
        {
            process = _process;
            _process = null;
        }
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.Dispose(); } catch { }
    }

    private void AppendStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_stderr)
        {
            if (_stderr.Length > 6000) _stderr.Remove(0, Math.Min(3000, _stderr.Length));
            _stderr.AppendLine(line);
        }
    }

    private string ReadStderr()
    {
        lock (_stderr) return _stderr.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gamingRegistration.Dispose();
        KillCurrentProcess();
        _requestGate.Dispose();
    }

    private static string Trim(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= 800 ? text : text[..800] + "…";
    }

    internal const string PythonScriptSource = """
import base64
import contextlib
import json
import sys
import time

import numpy as np
with contextlib.redirect_stdout(sys.stderr):
    from kokoro import KPipeline

SAMPLE_RATE = 24000
# Kokoro's default split_pattern is newlines only, so a single-paragraph reply was one chunk and the first
# audio frame waited for the whole synthesis. Split at sentence ends so audio streams per sentence.
SENTENCE_SPLIT = r"(?<=[.!?])\s+|\n+"
pipelines = {}


def emit(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def pipeline_for(voice):
    lang_code = voice[0] if voice else "a"
    if lang_code not in pipelines:
        with contextlib.redirect_stdout(sys.stderr):
            pipelines[lang_code] = KPipeline(lang_code=lang_code)
    return pipelines[lang_code]


def pcm16(audio):
    if hasattr(audio, "detach"):
        audio = audio.detach().cpu().numpy()
    values = np.asarray(audio, dtype=np.float32).reshape(-1)
    values = np.clip(values, -1.0, 1.0)
    return (values * 32767.0).astype("<i2", copy=False).tobytes()


emit({
    "type": "ready",
    "apiVersion": "1",
    "sampleRate": SAMPLE_RATE,
    "channels": 1,
    "format": "pcm_s16le",
})

for raw in sys.stdin:
    command = {}
    if not raw.strip():
        continue
    request_id = ""
    sequence = 1
    try:
        command = json.loads(raw)
        if str(command.get("apiVersion", "")) != "1":
            raise ValueError("unsupported streaming speech apiVersion")
        if command.get("op") != "synthesize":
            raise ValueError("unsupported streaming speech operation")
        request_id = str(command.get("id", ""))
        if not request_id:
            raise ValueError("missing request id")
        text = str(command.get("text", ""))
        voice = str(command.get("voice", "af_heart"))
        if not text.strip():
            raise ValueError("empty synthesis text")

        started = time.perf_counter()
        first_audio_at = None
        total_samples = 0
        pipeline = pipeline_for(voice)
        with contextlib.redirect_stdout(sys.stderr):
            iterator = iter(pipeline(text, voice=voice, split_pattern=SENTENCE_SPLIT))
        while True:
            try:
                with contextlib.redirect_stdout(sys.stderr):
                    _, _, audio = next(iterator)
            except StopIteration:
                break
            payload = pcm16(audio)
            if not payload:
                continue
            if first_audio_at is None:
                first_audio_at = time.perf_counter()
            samples = len(payload) // 2
            total_samples += samples
            emit({
                "id": request_id,
                "type": "audio",
                "sequence": sequence,
                "base64": base64.b64encode(payload).decode("ascii"),
                "samples": samples,
            })
            sequence += 1

        emit({
            "id": request_id,
            "type": "done",
            "sequence": sequence,
            "samples": total_samples,
            "durationSeconds": total_samples / SAMPLE_RATE,
            "ttfaMilliseconds": 0 if first_audio_at is None else round((first_audio_at - started) * 1000),
        })
    except Exception as exc:
        emit({
            "id": request_id,
            "type": "error",
            "sequence": sequence,
            "error": str(exc)[:800],
        })
""";
}
