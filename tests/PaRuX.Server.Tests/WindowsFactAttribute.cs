using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A Fact that is reported as Skipped, with the given reason, on a host where it cannot run. The skip is set at
/// discovery (see <see cref="OptionalPrerequisite"/> for why a run-time skip is not used).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows()) Skip = reason;
    }
}
