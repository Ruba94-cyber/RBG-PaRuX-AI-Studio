using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A Fact for assertions that need the local embedding engine. It is reported as Skipped, with its reason, when the
/// engine is absent from the test host, and it fails when <see cref="OptionalPrerequisite.RequireSwitch"/> requires
/// the engine (see <see cref="OptionalPrerequisite"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class EmbeddingEngineFactAttribute : FactAttribute
{
    public EmbeddingEngineFactAttribute()
    {
        Skip = OptionalPrerequisite.SkipReasonWhenAbsent(
            new EmbeddingService().IsInstalled,
            "the local embedding engine (ai-runtimes/embeddings)",
            OptionalPrerequisite.IsRequired());
    }
}
