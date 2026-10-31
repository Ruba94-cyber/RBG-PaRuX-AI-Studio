using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services;

/// <summary>Coordinates one optional speech warmup; stale completions cannot change a newer attempt.</summary>
internal sealed class SpeechWarmupCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _warmed;
    private Task? _task;

    internal Task? CurrentTask { get { lock (_gate) return _task; } }

    internal void Start(Func<CancellationToken, Task> warm, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_warmed) return;
            var source = new CancellationTokenSource(timeout);
            _warmed = true;
            _current = source;
            _task = Task.Run(async () =>
            {
                try { await warm(source.Token).ConfigureAwait(false); }
                catch
                {
                    lock (_gate)
                        if (ReferenceEquals(_current, source)) _warmed = false;
                }
                finally
                {
                    lock (_gate)
                        if (ReferenceEquals(_current, source)) _current = null;
                    source.Dispose();
                }
            });
        }
    }

    internal Exception? Cancel()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _current;
            _current = null;
            _warmed = false;
            _task = null;
        }
        try { source?.Cancel(); return null; }
        catch (ObjectDisposedException) { return null; }
        catch (AggregateException ex) { return ex; }
    }
}
