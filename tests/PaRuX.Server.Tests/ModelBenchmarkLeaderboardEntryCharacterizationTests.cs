using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Characterizes the observable contract of the public
/// <see cref="ModelBenchmarkLeaderboardEntry"/> type: default property values,
/// get/set round-trips and the exact public surface it exposes.</summary>
public sealed class ModelBenchmarkLeaderboardEntryCharacterizationTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 21, 12, 30, 45, DateTimeKind.Utc);

    [Fact]
    public void New_entry_defaults_string_properties_to_empty_and_numeric_properties_to_zero()
    {
        ModelBenchmarkLeaderboardEntry entry = new();

        Assert.Equal(string.Empty, entry.ModelPath);
        Assert.Equal(string.Empty, entry.Label);
        Assert.Equal(string.Empty, entry.Backend);
        Assert.Equal(string.Empty, entry.HardwareKey);
        Assert.Equal(0d, entry.TokensPerSecond);
        Assert.Equal(0, entry.Repetitions);
        Assert.Equal(DateTime.MinValue, entry.MeasuredUtc);
    }

    [Fact]
    public void String_properties_accept_arbitrary_values_including_null()
    {
        ModelBenchmarkLeaderboardEntry entry = new();

        entry.ModelPath = "C:\\models\\llama.gguf";
        entry.Label = null!;
        entry.Backend = "llama.cpp";
        entry.HardwareKey = "gpu:0";

        Assert.Equal("C:\\models\\llama.gguf", entry.ModelPath);
        Assert.Null(entry.Label);
        Assert.Equal("llama.cpp", entry.Backend);
        Assert.Equal("gpu:0", entry.HardwareKey);
    }

    [Fact]
    public void Numeric_properties_round_trip_assigned_values()
    {
        ModelBenchmarkLeaderboardEntry entry = new();

        entry.TokensPerSecond = 42.5;
        entry.Repetitions = 7;

        Assert.Equal(42.5, entry.TokensPerSecond);
        Assert.Equal(7, entry.Repetitions);
    }

    [Fact]
    public void TokensPerSecond_accepts_boundary_values_without_validation()
    {
        ModelBenchmarkLeaderboardEntry entry = new();

        entry.TokensPerSecond = double.MaxValue;
        Assert.Equal(double.MaxValue, entry.TokensPerSecond);

        entry.TokensPerSecond = double.NegativeInfinity;
        Assert.Equal(double.NegativeInfinity, entry.TokensPerSecond);

        entry.TokensPerSecond = double.NaN;
        Assert.True(double.IsNaN(entry.TokensPerSecond));
    }

    [Fact]
    public void Repetitions_accepts_negative_values_without_validation()
    {
        ModelBenchmarkLeaderboardEntry entry = new();

        entry.Repetitions = int.MinValue;
        Assert.Equal(int.MinValue, entry.Repetitions);

        entry.Repetitions = -3;
        Assert.Equal(-3, entry.Repetitions);
    }

    [Fact]
    public void MeasuredUtc_round_trips_assigned_datetime()
    {
        ModelBenchmarkLeaderboardEntry entry = new();

        entry.MeasuredUtc = FixedUtc;

        Assert.Equal(FixedUtc, entry.MeasuredUtc);
        Assert.Equal(DateTimeKind.Utc, entry.MeasuredUtc.Kind);
    }

    [Fact]
    public void Object_initialization_sets_every_property()
    {
        ModelBenchmarkLeaderboardEntry entry = new()
        {
            ModelPath = "models\\mistral.gguf",
            Label = "Mistral 7B",
            Backend = "winml",
            HardwareKey = "cpu:8",
            TokensPerSecond = 12.75,
            Repetitions = 5,
            MeasuredUtc = FixedUtc
        };

        Assert.Equal("models\\mistral.gguf", entry.ModelPath);
        Assert.Equal("Mistral 7B", entry.Label);
        Assert.Equal("winml", entry.Backend);
        Assert.Equal("cpu:8", entry.HardwareKey);
        Assert.Equal(12.75, entry.TokensPerSecond);
        Assert.Equal(5, entry.Repetitions);
        Assert.Equal(FixedUtc, entry.MeasuredUtc);
    }

    [Fact]
    public void Public_surface_matches_characterized_contract()
    {
        Type type = typeof(ModelBenchmarkLeaderboardEntry);

        Assert.True(type.IsSealed);
        Assert.Equal(
            new[] { "Backend", "HardwareKey", "Label", "MeasuredUtc", "ModelPath", "Repetitions", "TokensPerSecond" },
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
    public void Entry_is_serializable_through_json_and_preserves_values()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        ModelBenchmarkLeaderboardEntry original = new()
        {
            ModelPath = "m.gguf",
            Label = "L",
            Backend = "b",
            HardwareKey = "h",
            TokensPerSecond = 1.5,
            Repetitions = 2,
            MeasuredUtc = FixedUtc
        };

        string json = System.Text.Json.JsonSerializer.Serialize(original, options);
        ModelBenchmarkLeaderboardEntry? restored = System.Text.Json.JsonSerializer.Deserialize<ModelBenchmarkLeaderboardEntry>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(original.ModelPath, restored!.ModelPath);
        Assert.Equal(original.Label, restored.Label);
        Assert.Equal(original.Backend, restored.Backend);
        Assert.Equal(original.HardwareKey, restored.HardwareKey);
        Assert.Equal(original.TokensPerSecond, restored.TokensPerSecond);
        Assert.Equal(original.Repetitions, restored.Repetitions);
        Assert.Equal(original.MeasuredUtc, restored.MeasuredUtc);
    }
}
