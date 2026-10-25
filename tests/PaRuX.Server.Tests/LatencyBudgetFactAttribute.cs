using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A Fact for a wall-clock latency budget. The budget is enforced only when <see cref="SwitchName"/> is 1, on an
/// idle host. Otherwise the test is reported as Skipped, with that reason, at discovery. A concurrent build or scan
/// moves these numbers without any product change, so they must not decide the default run.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LatencyBudgetFactAttribute : FactAttribute
{
    public const string SwitchName = "PARUX_RUN_LATENCY_BUDGETS";

    public LatencyBudgetFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(SwitchName), "1", StringComparison.Ordinal))
            Skip = "Wall-clock latency budget not enforced on this run. Set " + SwitchName + "=1 on an idle host to enforce it.";
    }
}
