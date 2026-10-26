using Xunit;

namespace PaRuX.UITests.Infrastructure;

/// <summary>
/// Opt-in real-model fact: reported as SKIPPED (with the reason) unless every required environment variable is set,
/// instead of failing in the test body. When all are present it behaves like a normal <see cref="FactAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RealCodingFactAttribute : FactAttribute
{
    public RealCodingFactAttribute(params string[] additionalRequiredVariables)
    {
        string[] missing = new[]
            {
                RealCodingProduct.AppVariable, RealCodingProduct.EnvRootVariable, RealCodingProduct.ArtifactsVariable
            }
            .Concat(additionalRequiredVariables)
            .Where(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToArray();
        if (missing.Length > 0)
            Skip = "Opt-in real-model test; set " + string.Join(", ", missing) + " to run it.";
    }
}
