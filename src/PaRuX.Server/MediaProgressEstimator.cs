using System.Diagnostics;

namespace PaRuX.Server;

/// <summary>
/// Converts ComfyUI's sampler progress into a stable remaining-time estimate.
/// ComfyUI can emit duplicate progress values while loading/offloading tensors;
/// those samples must not make ETA grow just because the clock kept running.
/// </summary>
internal sealed class MediaProgressEstimator
{
    private const double MinSampleSeconds = 0.25;
    private const double RateSmoothing = 0.30;
    private const int MaximumEtaSeconds = 60 * 60;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _initialEstimateSeconds;
    private int _lastPercent;
    private double _lastSampleSeconds;
    private double? _progressPerSecond;
    private int? _lastEtaSeconds;

    public MediaProgressEstimator(int initialEstimateSeconds)
    {
        _initialEstimateSeconds = Math.Max(1, initialEstimateSeconds);
    }

    public int? Estimate(int rawPercent)
    {
        int percent = Math.Clamp(rawPercent, 0, 100);
        double now = _clock.Elapsed.TotalSeconds;

        if (percent > _lastPercent)
        {
            double elapsed = now - _lastSampleSeconds;
            if (_lastPercent > 0 && elapsed >= MinSampleSeconds)
            {
                double sampleRate = (percent - _lastPercent) / elapsed;
                _progressPerSecond = _progressPerSecond.HasValue
                    ? (_progressPerSecond.Value * (1 - RateSmoothing)) + (sampleRate * RateSmoothing)
                    : sampleRate;
            }

            _lastPercent = percent;
            _lastSampleSeconds = now;
        }

        if (percent >= 100) return 0;
        if (percent <= 0 || !_progressPerSecond.HasValue || _progressPerSecond.Value <= 0)
            return _initialEstimateSeconds;

        int eta = (int)Math.Round((100 - percent) / _progressPerSecond.Value);
        eta = Math.Clamp(eta, 0, MaximumEtaSeconds);

        // A short-lived slow/fast sample should not make the UI oscillate. Rate
        // smoothing handles normal noise; this second guard handles a delayed
        // websocket burst after ComfyUI has been busy with the same percentage.
        if (_lastEtaSeconds.HasValue)
        {
            int previous = _lastEtaSeconds.Value;
            eta = Math.Clamp(eta, Math.Max(0, previous / 2), Math.Min(MaximumEtaSeconds, Math.Max(1, previous * 2)));
        }

        _lastEtaSeconds = eta;
        return eta;
    }
}
