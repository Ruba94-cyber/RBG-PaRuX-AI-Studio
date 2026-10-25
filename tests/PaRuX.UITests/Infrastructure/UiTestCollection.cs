using Xunit;

namespace PaRuX.UITests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiTestCollection
{
    public const string Name = "PaRuX desktop UI";
}
