using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Characterizes the observable contract of the public
/// <see cref="ObservabilityStatusCount"/> type: default property values,
/// get/set round-trips, boundaries and the exact public surface it exposes.</summary>
public sealed class ObservabilityStatusCountCharacterizationTests
{
    [Fact]
    public void New_instance_defaults_Name_to_empty_string_and_Count_to_zero()
    {
        ObservabilityStatusCount count = new();

        Assert.Equal(string.Empty, count.Name);
        Assert.Equal(0, count.Count);
    }

    [Fact]
    public void Name_round_trips_assigned_values_including_empty_and_null()
    {
        ObservabilityStatusCount count = new();

        count.Name = "running";
        Assert.Equal("running", count.Name);

        count.Name = string.Empty;
        Assert.Equal(string.Empty, count.Name);

        count.Name = null!;
        Assert.Null(count.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void Count_round_trips_assigned_values_including_boundaries(int value)
    {
        ObservabilityStatusCount count = new();

        count.Count = value;

        Assert.Equal(value, count.Count);
    }

    [Fact]
    public void Count_accepts_negative_values_without_validation()
    {
        ObservabilityStatusCount count = new();

        count.Count = -3;

        Assert.Equal(-3, count.Count);
    }

    [Fact]
    public void Object_initialization_sets_every_property()
    {
        ObservabilityStatusCount count = new()
        {
            Name = "completed",
            Count = 7
        };

        Assert.Equal("completed", count.Name);
        Assert.Equal(7, count.Count);
    }

    [Fact]
    public void Public_surface_matches_characterized_contract()
    {
        Type type = typeof(ObservabilityStatusCount);

        Assert.True(type.IsSealed);
        Assert.Equal(
            new[] { "Count", "Name" },
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
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        ObservabilityStatusCount original = new()
        {
            Name = "queued",
            Count = 3
        };

        string json = System.Text.Json.JsonSerializer.Serialize(original, options);
        ObservabilityStatusCount? restored = System.Text.Json.JsonSerializer.Deserialize<ObservabilityStatusCount>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(original.Name, restored!.Name);
        Assert.Equal(original.Count, restored.Count);
    }
}
