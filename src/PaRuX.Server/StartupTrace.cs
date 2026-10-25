using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PaRuX.Server;

/// <summary>
/// Opt-in startup timing. Set PARUX_STARTUP_TRACE to a file path and Core appends one line per milestone
/// ("ms since process start") and one per hosted service start. Off by default: with the variable unset every
/// call is a no-op and hosted services are not wrapped, so normal runs carry no measurement overhead.
/// </summary>
internal static class StartupTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("PARUX_STARTUP_TRACE") is { Length: > 0 } p ? p : null;

    internal static bool Enabled => Path != null;

    internal static double MillisecondsSinceProcessStart()
    {
        using Process self = Process.GetCurrentProcess();
        return (DateTime.Now - self.StartTime).TotalMilliseconds;
    }

    internal static void Mark(string name)
    {
        if (Path == null) return;
        try { File.AppendAllText(Path, $"{MillisecondsSinceProcessStart():F0}\t{name}{Environment.NewLine}"); }
        catch (IOException) { /* tracing must never affect startup */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Wraps every registered hosted service so its construction + StartAsync time is recorded. No-op when tracing is off.</summary>
    internal static void WrapHostedServices(IServiceCollection services)
    {
        if (Path == null) return;
        foreach (ServiceDescriptor descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(descriptor);
            services.AddSingleton<IHostedService>(sp =>
            {
                var constructed = Stopwatch.StartNew();
                IHostedService inner = descriptor.ImplementationInstance as IHostedService
                    ?? (descriptor.ImplementationFactory != null
                        ? (IHostedService)descriptor.ImplementationFactory(sp)
                        : (IHostedService)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
                return new TimedHostedService(inner, inner.GetType().Name, constructed.Elapsed.TotalMilliseconds);
            });
        }
    }

    private sealed class TimedHostedService(IHostedService inner, string name, double constructMs) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
            Mark($"hosted {name}: construct={constructMs:F1}ms start={clock.Elapsed.TotalMilliseconds:F1}ms");
        }

        public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);
    }
}
