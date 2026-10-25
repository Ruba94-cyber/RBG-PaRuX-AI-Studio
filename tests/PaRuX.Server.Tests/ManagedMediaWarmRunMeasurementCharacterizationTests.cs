using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Characterizes the observable contract of the public
/// <see cref="ManagedMediaWarmRunMeasurement"/> type: default property values,
/// get/set round-trips, boundaries and the exact public surface it exposes.</summary>
public sealed class ManagedMediaWarmRunMeasurementCharacterizationTests
{
    [Fact]
    public void New_instance_defaults_all_numeric_properties_to_zero()
    {
        ManagedMediaWarmRunMeasurement m = new();

        Assert.Equal(0, m.Run);
        Assert.Equal(0, m.TotalMilliseconds);
        Assert.Equal(0, m.WorkerGenerationMilliseconds);
        Assert.Equal(0, m.PeakAllocatedBytes);
        Assert.Equal(0, m.PeakReservedBytes);
    }

    [Fact]
    public void Properties_round_trip_assigned_values()
    {
        ManagedMediaWarmRunMeasurement m = new();

        m.Run = 2;
        m.TotalMilliseconds = 1500;
        m.WorkerGenerationMilliseconds = 1200;
        m.PeakAllocatedBytes = 1024L * 1024L * 1024L;
        m.PeakReservedBytes = 2L * 1024L * 1024L * 1024L;

        Assert.Equal(2, m.Run);
        Assert.Equal(1500, m.TotalMilliseconds);
        Assert.Equal(1200, m.WorkerGenerationMilliseconds);
        Assert.Equal(1024L * 1024L * 1024L, m.PeakAllocatedBytes);
        Assert.Equal(2L * 1024L * 1024L * 1024L, m.PeakReservedBytes);
    }

    [Fact]
    public void Object_initialization_sets_every_property()
    {
        ManagedMediaWarmRunMeasurement m = new()
        {
            Run = 5,
            TotalMilliseconds = 300,
            WorkerGenerationMilliseconds = 250,
            PeakAllocatedBytes = 4096,
            PeakReservedBytes = 8192
        };

        Assert.Equal(5, m.Run);
        Assert.Equal(300, m.TotalMilliseconds);
        Assert.Equal(250, m.WorkerGenerationMilliseconds);
        Assert.Equal(4096, m.PeakAllocatedBytes);
        Assert.Equal(8192, m.PeakReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Run_accepts_any_int_without_validation(int run)
    {
        ManagedMediaWarmRunMeasurement m = new() { Run = run };

        Assert.Equal(run, m.Run);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(-42L)]
    [InlineData(9007199254740993L)]
    public void Millisecond_and_byte_properties_accept_boundary_values_without_validation(long value)
    {
        ManagedMediaWarmRunMeasurement m = new();

        m.TotalMilliseconds = value;
        m.WorkerGenerationMilliseconds = value;
        m.PeakAllocatedBytes = value;
        m.PeakReservedBytes = value;

        Assert.Equal(value, m.TotalMilliseconds);
        Assert.Equal(value, m.WorkerGenerationMilliseconds);
        Assert.Equal(value, m.PeakAllocatedBytes);
        Assert.Equal(value, m.PeakReservedBytes);
    }

    [Fact]
    public void Public_surface_matches_characterized_contract()
    {
        Type type = typeof(ManagedMediaWarmRunMeasurement);

        Assert.True(type.IsSealed);
        Assert.Equal(
            new[] { "PeakAllocatedBytes", "PeakReservedBytes", "Run", "TotalMilliseconds", "WorkerGenerationMilliseconds" },
            type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => p.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        foreach (System.Reflection.PropertyInfo property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            Assert.True(property.CanRead, $"{property.Name} must be readable");
            Assert.True(property.CanWrite, $"{property.Name} must be writable");
        }
    }

    [Fact]
    public void Instance_is_serializable_through_json_and_preserves_values()
    {
        ManagedMediaWarmRunMeasurement original = new()
        {
            Run = 3,
            TotalMilliseconds = 777,
            WorkerGenerationMilliseconds = 700,
            PeakAllocatedBytes = 123,
            PeakReservedBytes = 456
        };

        string json = System.Text.Json.JsonSerializer.Serialize(original);
        ManagedMediaWarmRunMeasurement? restored = System.Text.Json.JsonSerializer.Deserialize<ManagedMediaWarmRunMeasurement>(json);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Run);
        Assert.Equal(777, restored.TotalMilliseconds);
        Assert.Equal(700, restored.WorkerGenerationMilliseconds);
        Assert.Equal(123, restored.PeakAllocatedBytes);
        Assert.Equal(456, restored.PeakReservedBytes);
    }

    [Fact]
    public void Equality_is_reference_semantics_for_distinct_instances_with_identical_values()
    {
        ManagedMediaWarmRunMeasurement a = new()
        {
            Run = 1,
            TotalMilliseconds = 100,
            WorkerGenerationMilliseconds = 90,
            PeakAllocatedBytes = 10,
            PeakReservedBytes = 20
        };
        ManagedMediaWarmRunMeasurement b = new()
        {
            Run = 1,
            TotalMilliseconds = 100,
            WorkerGenerationMilliseconds = 90,
            PeakAllocatedBytes = 10,
            PeakReservedBytes = 20
        };

        Assert.NotSame(a, b);
        Assert.False(a.Equals(b));
    }
}
