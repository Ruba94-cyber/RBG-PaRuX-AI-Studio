using System.Reflection;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingUnsupportedMethodCharacterizationTests
{
    [Fact]
    public void New_instance_defaults_method_and_reason_to_empty_string()
    {
        TrainingUnsupportedMethod entry = new();

        Assert.Equal(string.Empty, entry.Method);
        Assert.Equal(string.Empty, entry.Reason);
    }

    [Theory]
    [InlineData("grpo", "CUDA backend not available")]
    [InlineData("tts-sft", "CPU profile: TTS requires the managed runtime")]
    [InlineData("vision-sft", "")]
    public void Properties_round_trip_assigned_values(string method, string reason)
    {
        TrainingUnsupportedMethod entry = new();
        entry.Method = method;
        entry.Reason = reason;

        Assert.Equal(method, entry.Method);
        Assert.Equal(reason, entry.Reason);
    }

    [Fact]
    public void Type_is_a_sealed_class_in_the_PaRuX_Contracts_namespace()
    {
        Type type = typeof(TrainingUnsupportedMethod);

        Assert.True(type.IsSealed);
        Assert.True(type.IsClass);
        Assert.False(type.IsAbstract);
        Assert.Equal("PaRuX.Contracts", type.Namespace);
    }

    [Fact]
    public void Type_exposes_exactly_the_two_public_auto_string_properties()
    {
        PropertyInfo[] properties = typeof(TrainingUnsupportedMethod).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        string[] names = properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "Method", "Reason" }, names);
        Assert.All(properties, property => Assert.Equal(typeof(string), property.PropertyType));
        Assert.All(properties, property => Assert.True(property.CanRead && property.CanWrite));
    }

    [Fact]
    public void Two_instances_are_independent_reference_values()
    {
        TrainingUnsupportedMethod first = new() { Method = "dpo", Reason = "No preference data" };
        TrainingUnsupportedMethod second = new();

        Assert.Equal("dpo", first.Method);
        Assert.Equal("No preference data", first.Reason);
        Assert.Equal(string.Empty, second.Method);
        Assert.Equal(string.Empty, second.Reason);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Entry_records_an_unlisted_method_without_affecting_engine_status_defaults()
    {
        TrainingEngineStatus status = new();

        Assert.Empty(status.UnsupportedMethods);

        status.UnsupportedMethods.Add(new TrainingUnsupportedMethod
        {
            Method = TrainingMethod.Grpo,
            Reason = "Backend is not CUDA"
        });

        Assert.Single(status.UnsupportedMethods);
        Assert.Equal(TrainingMethod.Grpo, status.UnsupportedMethods[0].Method);
        Assert.Equal("Backend is not CUDA", status.UnsupportedMethods[0].Reason);
        Assert.False(status.Ready);
        Assert.Equal("unknown", status.Backend);
        Assert.Equal("unknown", status.HardwareProfile);
    }

    [Fact]
    public void Method_name_is_plainly_settable_to_any_known_training_method()
    {
        foreach (string method in TrainingMethod.All)
        {
            TrainingUnsupportedMethod entry = new() { Method = method, Reason = "unavailable on this backend" };

            Assert.Equal(method, entry.Method);
            Assert.Equal("unavailable on this backend", entry.Reason);
        }
    }
}
