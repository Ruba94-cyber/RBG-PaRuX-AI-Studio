using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local speech: Parakeet-TDT-0.6B-v3 for STT and Kokoro-82M for TTS through a PaRuX-managed
/// Python environment outside the repository. The venv and its interpreter are created through
/// ManagedPythonRuntime, so a user-installed Python/uv is never a prerequisite. Long-lived JSONL
/// workers keep the heavy STT/TTS models warm between turns; a worker failure falls back to the
/// one-shot path and the following request attempts a clean worker restart. Gaming Mode remains a
/// hard-off: resident speech workers are killed and neither warm nor one-shot inference may start.
/// </summary>
public sealed class SpeechService : IDisposable, IContinuousSpeechStatusProvider
{
    private static string RuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "speech");

    private static string VenvRoot => Path.Combine(RuntimeRoot, ".venv");
    private static string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private static string ScriptPath => Path.Combine(RuntimeRoot, "parux_speech.py");

    private readonly object _workerGate = new();
    private readonly LocalRuntimeService? _runtime;
    private SpeechWarmWorker? _sttWorker;
    private SpeechWarmWorker? _ttsWorker;
    private bool _disposed;

    public SpeechService() { }

    public SpeechService(LocalRuntimeService runtime)
    {
        _runtime = runtime;
    }

    public async Task<SpeechEngineStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!ManagedPythonRuntime.IsManagedVenv(VenvRoot)) return new SpeechEngineStatus { SttInstalled = false, TtsInstalled = false };
        try
        {
            (int exitCode, string stdout, _) = await RunCapturedAsync(VenvPython,
                "-c \"import importlib.util as u; print(u.find_spec('parakeet_tdt') is not None or u.find_spec('nemo') is not None or u.find_spec('transformers') is not None); print(u.find_spec('kokoro') is not None)\"",
                ct, TimeSpan.FromSeconds(20));
            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new SpeechEngineStatus
            {
                SttInstalled = exitCode == 0 && lines.Length > 0 && lines[0] == "True",
                TtsInstalled = exitCode == 0 && lines.Length > 1 && lines[1] == "True"
            };
        }
        catch
        {
            return new SpeechEngineStatus { SttInstalled = false, TtsInstalled = false };
        }
    }

    /// <summary>
    /// Kokoro's English G2P (misaki[en]) needs the spaCy en_core_web_sm model but tries to download it
    /// on the first request. A PaRuX-managed venv has no pip and uv is not on PATH there, so that lazy
    /// download fails and the streaming worker exits on the first English turn (found in the BIC-13 live
    /// run). The MIT-licensed model wheel is installed up front, pinned to the spaCy 3.8 series.
    /// </summary>
    internal const string SpacyModelRequirement =
        "en_core_web_sm @ https://github.com/explosion/spacy-models/releases/download/en_core_web_sm-3.8.0/en_core_web_sm-3.8.0-py3-none-any.whl";

    internal static readonly string[] SpeechPackages =
    {
        "transformers", "soundfile", "numpy", "librosa", "kokoro", "misaki[en]", SpacyModelRequirement
    };

    public async Task<string> InstallEngineAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        var log = new System.Text.StringBuilder();
        TorchHardwareProfile torchHardware = await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false);
        TorchInstallChannelPlan torchPlan = TorchInstallChannelPolicy.Select(torchHardware);
        if (!torchPlan.CanInstall) throw new InvalidOperationException(torchPlan.Reason);

        await ManagedPythonRuntime.EnsureVenvAsync(VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct)
            .ConfigureAwait(false);

        log.AppendLine(torchPlan.Reason);
        log.Append(await ManagedPythonRuntime.PipInstallAsync(
            VenvRoot,
            torchPlan.PipArguments,
            ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
        log.Append(await ManagedPythonRuntime.PipInstallAsync(
            VenvRoot,
            SpeechPackages,
            ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
        WriteScript();
        return log.ToString();
    }

    /// <summary>Loads one or both local models before the first live voice turn.</summary>
    public async Task PrewarmAsync(bool stt, bool tts, CancellationToken ct)
    {
        EnsureInferenceAllowed();
        WriteScript();
        var tasks = new List<Task>(2);
        if (stt) tasks.Add(GetWorker("transcribe").PrewarmAsync(ct, TimeSpan.FromMinutes(3)));
        if (tts) tasks.Add(GetWorker("synthesize").PrewarmAsync(ct, TimeSpan.FromMinutes(3)));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<TranscribeResponse> TranscribeAsync(TranscribeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AudioBase64)) throw new InvalidDataException("audioBase64 is required.");
        EnsureInferenceAllowed();
        WriteScript();

        byte[] audioBytes;
        try { audioBytes = Convert.FromBase64String(request.AudioBase64); }
        catch (FormatException) { throw new InvalidDataException("audioBase64 is not valid Base64 audio data."); }
        if (audioBytes.Length == 0) throw new InvalidDataException("audioBase64 decoded to an empty audio payload.");

        string extension;
        try { extension = Path.GetExtension(request.FileName); }
        catch (ArgumentException) { extension = string.Empty; }
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12) extension = ".wav";

        string audioPath = Path.Combine(Path.GetTempPath(), "parux-stt-" + Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(audioPath, audioBytes, ct);
        try
        {
            JsonElement result;
            try
            {
                result = await GetWorker("transcribe").ExecuteAsync(new
                {
                    audioPath,
                    language = request.Language ?? string.Empty
                }, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                EnsureInferenceAllowed();
                result = await RunTranscribeOneShotAsync(audioPath, request.Language ?? string.Empty, ct).ConfigureAwait(false);
            }

            return new TranscribeResponse
            {
                Text = result.GetProperty("text").GetString() ?? string.Empty,
                LanguageDetected = result.TryGetProperty("language", out JsonElement lang) ? lang.GetString() ?? string.Empty : string.Empty,
                DurationSeconds = result.TryGetProperty("duration", out JsonElement dur) ? dur.GetDouble() : 0
            };
        }
        finally
        {
            try { File.Delete(audioPath); } catch { }
        }
    }

    public async Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new InvalidDataException("text is required.");
        EnsureInferenceAllowed();
        WriteScript();

        string outputPath = Path.Combine(Path.GetTempPath(), "parux-tts-" + Guid.NewGuid().ToString("N") + ".wav");
        string textArgPath = Path.Combine(Path.GetTempPath(), "parux-tts-text-" + Guid.NewGuid().ToString("N") + ".txt");
        string voice = SpeechVoiceSelector.Resolve(request.Voice, request.Text);
        if (voice.Length > 80 || voice.Contains('"') || voice.Any(char.IsControl))
            throw new InvalidDataException("voice contains unsupported characters.");

        try
        {
            await File.WriteAllTextAsync(textArgPath, request.Text, ct);
            JsonElement result;
            try
            {
                result = await GetWorker("synthesize").ExecuteAsync(new
                {
                    textPath = textArgPath,
                    voice,
                    outputPath
                }, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                EnsureInferenceAllowed();
                result = await RunSynthesizeOneShotAsync(textArgPath, voice, outputPath, ct).ConfigureAwait(false);
            }

            if (!File.Exists(outputPath)) throw new InvalidOperationException("The speech engine produced no output file.");
            byte[] bytes = await File.ReadAllBytesAsync(outputPath, ct);
            return new SynthesizeResponse
            {
                FileName = "speech_" + Guid.NewGuid().ToString("N")[..8] + ".wav",
                MediaType = "audio/wav",
                Base64Data = Convert.ToBase64String(bytes),
                DurationSeconds = result.TryGetProperty("duration", out JsonElement dur) ? dur.GetDouble() : 0
            };
        }
        finally
        {
            try { File.Delete(textArgPath); } catch { }
            try { File.Delete(outputPath); } catch { }
        }
    }

    /// <summary>Gaming Guard calls this before the global runtime shutdown. Worker objects remain
    /// reusable after gaming ends, but their Python children and resident model VRAM are gone.</summary>
    public void StopWorkersForGaming()
    {
        lock (_workerGate)
        {
            _sttWorker?.Stop();
            _ttsWorker?.Stop();
        }
    }

    private async Task<JsonElement> RunTranscribeOneShotAsync(string audioPath, string language, CancellationToken ct)
    {
        (int exitCode, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
            "\"" + ScriptPath + "\" transcribe \"" + audioPath + "\" \"" + language + "\"", ct, TimeSpan.FromMinutes(3));
        if (exitCode != 0) throw new InvalidOperationException("Transcription failed: " + Trim(stderr.Length > 0 ? stderr : stdout));
        return ParseLastJsonObject(stdout);
    }

    private async Task<JsonElement> RunSynthesizeOneShotAsync(string textPath, string voice, string outputPath, CancellationToken ct)
    {
        (int exitCode, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
            "\"" + ScriptPath + "\" synthesize \"" + textPath + "\" \"" + voice + "\" \"" + outputPath + "\"", ct, TimeSpan.FromMinutes(3));
        if (exitCode != 0) throw new InvalidOperationException("Synthesis failed: " + Trim(stderr.Length > 0 ? stderr : stdout));
        return ParseLastJsonObject(stdout);
    }

    private SpeechWarmWorker GetWorker(string mode)
    {
        lock (_workerGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpeechService));
            return mode == "transcribe"
                ? _sttWorker ??= new SpeechWarmWorker(mode, VenvPython, ScriptPath)
                : _ttsWorker ??= new SpeechWarmWorker(mode, VenvPython, ScriptPath);
        }
    }

    private void EnsureInferenceAllowed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeechService));
        if (_runtime?.GamingMode == true)
            throw new InvalidOperationException("Gaming Mode is active, so local speech inference is unavailable.");
        if (!ManagedPythonRuntime.IsManagedVenv(VenvRoot))
            throw new InvalidOperationException("The speech engine is not installed on this machine yet.");
    }

    private static JsonElement ParseLastJsonObject(string output)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate = line.Trim();
            if (!candidate.StartsWith('{') || !candidate.EndsWith('}')) continue;
            try { return JsonDocument.Parse(candidate).RootElement.Clone(); } catch (JsonException) { }
        }
        throw new InvalidDataException("The speech engine returned an unreadable result.");
    }

    private static void WriteScript()
    {
        Directory.CreateDirectory(RuntimeRoot);
        File.WriteAllText(ScriptPath, ScriptSource);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan timeout, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        ManagedPythonRuntime.ApplyEnvironment(psi);
        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The local speech operation timed out.");
        }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    public void Dispose()
    {
        lock (_workerGate)
        {
            if (_disposed) return;
            _disposed = true;
            _sttWorker?.Dispose();
            _ttsWorker?.Dispose();
            _sttWorker = null;
            _ttsWorker = null;
        }
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 800 ? value : value[..800] + "…";
    }

    private const string ScriptSource = @"
import contextlib, json, sys, time

mode = sys.argv[1]


def load_stt():
    with contextlib.redirect_stdout(sys.stderr):
        from transformers import pipeline
        return pipeline('automatic-speech-recognition', model='nvidia/parakeet-tdt-0.6b-v3', device=0)


def transcribe(asr, audio_path, language):
    start = time.monotonic()
    with contextlib.redirect_stdout(sys.stderr):
        result = asr(audio_path)
    elapsed = time.monotonic() - start
    text = result['text'] if isinstance(result, dict) else str(result)
    return {'text': text, 'language': language or '', 'duration': elapsed}


def load_tts_modules():
    with contextlib.redirect_stdout(sys.stderr):
        from kokoro import KPipeline
        import soundfile as sf
        import numpy as np
    return KPipeline, sf, np


def synthesize(KPipeline, sf, np, pipelines, text_path, voice, output_path):
    with open(text_path, encoding='utf-8') as f:
        text = f.read()
    lang_code = voice[0] if voice else 'a'
    if lang_code not in pipelines:
        with contextlib.redirect_stdout(sys.stderr):
            pipelines[lang_code] = KPipeline(lang_code=lang_code)
    start = time.monotonic()
    chunks = []
    with contextlib.redirect_stdout(sys.stderr):
        for _, _, audio in pipelines[lang_code](text, voice=voice):
            chunks.append(audio)
    full = np.concatenate(chunks) if chunks else np.zeros(0, dtype='float32')
    sf.write(output_path, full, 24000)
    # 'duration' is the audio length (SynthesizeResponse.DurationSeconds); generation time is separate.
    return {'duration': len(full) / 24000.0, 'generationSeconds': time.monotonic() - start}


if mode == 'transcribe':
    asr = load_stt()
    payload = transcribe(asr, sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else '')
    print(json.dumps(payload), flush=True)

elif mode == 'synthesize':
    KPipeline, sf, np = load_tts_modules()
    payload = synthesize(KPipeline, sf, np, {}, sys.argv[2], sys.argv[3], sys.argv[4])
    print(json.dumps(payload), flush=True)

elif mode == 'worker-transcribe':
    asr = load_stt()
    print(json.dumps({'ready': True}), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            payload = transcribe(asr, request['audioPath'], request.get('language', ''))
            payload['ok'] = True
            print(json.dumps(payload), flush=True)
        except Exception as exc:
            print(json.dumps({'ok': False, 'error': str(exc)[:800]}), flush=True)

elif mode == 'worker-synthesize':
    KPipeline, sf, np = load_tts_modules()
    pipelines = {}
    print(json.dumps({'ready': True}), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            payload = synthesize(KPipeline, sf, np, pipelines, request['textPath'], request['voice'], request['outputPath'])
            payload['ok'] = True
            print(json.dumps(payload), flush=True)
        except Exception as exc:
            print(json.dumps({'ok': False, 'error': str(exc)[:800]}), flush=True)

else:
    print(json.dumps({'error': 'unknown mode ' + mode}), flush=True)
    sys.exit(1)
";
}
