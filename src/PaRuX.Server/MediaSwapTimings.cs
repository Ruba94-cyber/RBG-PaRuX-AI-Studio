using System.Diagnostics;

namespace PaRuX.Server;

/// <summary>
/// Wall-clock evidence for the text &lt;-&gt; media GPU swap, so a live run can show where the time goes and whether a change helped.
/// Each stage is one line in <c>media-swap-timing.log</c> (bounded, rotated by truncation) and one trace line. Nothing here changes behaviour.
/// </summary>
internal static class MediaSwapTimings
{
    private const long MaxLogBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Action<string, long, string?>?> Sink = new();

    internal static IDisposable CaptureForTests(Action<string, long, string?> sink)
    {
        Action<string, long, string?>? previous = Sink.Value;
        Sink.Value = sink;
        return new Scope(() => Sink.Value = previous);
    }

    internal static void Record(string stage, TimeSpan elapsed, string? detail = null)
    {
        long ms = (long)elapsed.TotalMilliseconds;
        string line = DateTime.UtcNow.ToString("u") + " " + stage + " " + ms + " ms" + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " (" + detail + ")");
        Trace.TraceInformation("media-swap " + line);
        try { Sink.Value?.Invoke(stage, ms, detail); } catch { }
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LocalAiPaths.LogsPath);
                string path = Path.Combine(LocalAiPaths.LogsPath, "media-swap-timing.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes) File.Delete(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* diagnostics only */ }
    }

    private sealed class Scope : IDisposable
    {
        private Action? _action;
        public Scope(Action action) => _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
