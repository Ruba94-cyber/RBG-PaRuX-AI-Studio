using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalInferenceRegistryTruthTests
{
    [Fact]
    public void Built_in_registry_never_advertises_planned_or_experimental_engines()
    {
        var registry = new LocalInferenceEngineRegistry();
        IReadOnlyList<LocalInferenceEngineDescriptor> descriptors = registry.ListDescriptors();

        Assert.NotEmpty(descriptors);
        Assert.DoesNotContain(descriptors, descriptor =>
            descriptor.ImplementationState is EngineImplementationState.Planned or EngineImplementationState.Experimental);

        Assert.All(
            descriptors.Where(descriptor => descriptor.ImplementationState != EngineImplementationState.Disabled),
            descriptor => Assert.Equal(EngineImplementationState.Integrated, descriptor.ImplementationState));
    }

    [Fact]
    public void Unsupported_mlc_format_is_not_mistaken_for_a_serveable_engine()
    {
        var registry = new LocalInferenceEngineRegistry();

        Assert.Empty(registry.CompatibleDescriptors(
            ManagedModelFormat.Mlc,
            hardwareFamily: "vulkan",
            integratedOnly: false));
    }
}
