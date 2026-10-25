using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Builds one immutable challenge for the cold and resident-warm halves of a Diffusers benchmark.
/// Normalization happens once so a benchmark cannot accidentally compare different seeds, sizes,
/// prompts or step counts and call the result a warm-start improvement.
/// </summary>
internal static class ManagedDiffusersWarmBenchmarkPolicy
{
    internal const int MaximumWarmRuns = 10;

    internal static NormalizedDiffusersBenchmark Normalize(
        ManagedDiffusersWarmBenchmarkRequest request,
        Func<long>? seedFactory = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaJobRequest job = request.Job
            ?? throw new InvalidDataException("A Diffusers benchmark media job is required.");
        if (!string.Equals((job.Type ?? string.Empty).Trim(), "image", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Diffusers cold/warm benchmark supports text-to-image jobs only.");
        if (string.IsNullOrWhiteSpace(job.Prompt))
            throw new InvalidDataException("A benchmark prompt is required.");
        if (request.WarmRuns < 1 || request.WarmRuns > MaximumWarmRuns)
            throw new InvalidDataException($"WarmRuns must be between 1 and {MaximumWarmRuns}.");

        int width = MediaGenerationService.NormalizeDimension(job.Width, 1024);
        int height = MediaGenerationService.NormalizeDimension(job.Height, 1024);
        int steps = Math.Clamp(job.Steps <= 0 ? 9 : job.Steps, 4, 60);
        long seed = job.Seed > 0
            ? job.Seed
            : (seedFactory ?? (() => Random.Shared.NextInt64(1, long.MaxValue)))();
        if (seed <= 0) throw new InvalidDataException("The benchmark seed must be positive.");

        var challenge = new MediaJobRequest
        {
            Type = "image",
            Prompt = job.Prompt.Trim(),
            Width = width,
            Height = height,
            Steps = steps,
            Seed = seed
        };
        return new NormalizedDiffusersBenchmark(challenge, request.WarmRuns, request.IncludeFinalArtifact);
    }

    internal sealed record NormalizedDiffusersBenchmark(
        MediaJobRequest Challenge,
        int WarmRuns,
        bool IncludeFinalArtifact);
}
