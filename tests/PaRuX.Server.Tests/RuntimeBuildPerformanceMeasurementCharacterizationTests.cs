using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeBuildPerformanceMeasurementCharacterizationTests
{
    [Fact]
    public void Construction_stores_all_ten_positional_parameters()
    {
        RuntimeBuildPerformanceMeasurement m = new(
            PromptTokensPerSecond: 120.5,
            DecodeTokensPerSecond: 45.25,
            TimeToFirstTokenMilliseconds: 350.0,
            LoadMilliseconds: 1200.0,
            SustainedDecodeTokensPerSecond: 44.75,
            PeakGpuMemoryUsedMib: 8192.0,
            MaxGpuTemperatureC: 72.5,
            AverageGpuPowerWatts: 185.0,
            Backend: "text-cuda",
            HardwareKey: "gpu-0");

        Assert.Equal(120.5, m.PromptTokensPerSecond);
        Assert.Equal(45.25, m.DecodeTokensPerSecond);
        Assert.Equal(350.0, m.TimeToFirstTokenMilliseconds);
        Assert.Equal(1200.0, m.LoadMilliseconds);
        Assert.Equal(44.75, m.SustainedDecodeTokensPerSecond);
        Assert.Equal(8192.0, m.PeakGpuMemoryUsedMib);
        Assert.Equal(72.5, m.MaxGpuTemperatureC);
        Assert.Equal(185.0, m.AverageGpuPowerWatts);
        Assert.Equal("text-cuda", m.Backend);
        Assert.Equal("gpu-0", m.HardwareKey);
    }

    [Fact]
    public void Equality_is_structural_and_all_fields_must_match()
    {
        RuntimeBuildPerformanceMeasurement a = new(
            100, 50, 200, 800, 49, 4096, 65, 150, "text-vulkan", "hw-1");
        RuntimeBuildPerformanceMeasurement b = new(
            100, 50, 200, 800, 49, 4096, 65, 150, "text-vulkan", "hw-1");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0, "x", "i")]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 1, "x", "h")]
    [InlineData(0, 0, 0, 0, 0, 0, 1, 0, "x", "h")]
    [InlineData(0, 0, 0, 0, 0, 1, 0, 0, "x", "h")]
    [InlineData(0, 0, 0, 0, 1, 0, 0, 0, "x", "h")]
    [InlineData(0, 0, 0, 1, 0, 0, 0, 0, "x", "h")]
    [InlineData(0, 0, 1, 0, 0, 0, 0, 0, "x", "h")]
    [InlineData(0, 1, 0, 0, 0, 0, 0, 0, "x", "h")]
    [InlineData(1, 0, 0, 0, 0, 0, 0, 0, "x", "h")]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0, "y", "h")]
    public void Any_single_field_difference_breaks_equality(
        double prompt, double decode, double ttft, double load,
        double sustained, double mem, double temp, double power,
        string backend, string hw)
    {
        RuntimeBuildPerformanceMeasurement baseline = new(
            0, 0, 0, 0, 0, 0, 0, 0, "x", "h");
        RuntimeBuildPerformanceMeasurement mutated = new(
            prompt, decode, ttft, load, sustained, mem, temp, power, backend, hw);

        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void With_returns_a_new_instance_with_updated_fields()
    {
        RuntimeBuildPerformanceMeasurement original = new(
            100, 50, 200, 800, 49, 4096, 65, 150, "text-vulkan", "hw-1");

        RuntimeBuildPerformanceMeasurement updated = original with { DecodeTokensPerSecond = 99.0 };

        Assert.Equal(99.0, updated.DecodeTokensPerSecond);
        Assert.Equal(100, updated.PromptTokensPerSecond);
        Assert.Equal(50, original.DecodeTokensPerSecond);
        Assert.NotEqual(original, updated);
    }

    [Fact]
    public void ToString_includes_all_field_values()
    {
        RuntimeBuildPerformanceMeasurement m = new(
            111, 222, 333, 444, 555, 666, 777, 888, "text-cuda", "hw-42");

        string s = m.ToString() ?? string.Empty;

        Assert.Contains("111", s);
        Assert.Contains("222", s);
        Assert.Contains("333", s);
        Assert.Contains("444", s);
        Assert.Contains("555", s);
        Assert.Contains("666", s);
        Assert.Contains("777", s);
        Assert.Contains("888", s);
        Assert.Contains("text-cuda", s);
        Assert.Contains("hw-42", s);
    }

    [Fact]
    public void Accepts_negative_and_zero_values_for_double_fields()
    {
        RuntimeBuildPerformanceMeasurement m = new(
            -1.5, -2.5, -3.5, -4.5, -5.5, -6.5, -7.5, -8.5, "", "");

        Assert.Equal(-1.5, m.PromptTokensPerSecond);
        Assert.Equal(-2.5, m.DecodeTokensPerSecond);
        Assert.Equal(-3.5, m.TimeToFirstTokenMilliseconds);
        Assert.Equal(-4.5, m.LoadMilliseconds);
        Assert.Equal(-5.5, m.SustainedDecodeTokensPerSecond);
        Assert.Equal(-6.5, m.PeakGpuMemoryUsedMib);
        Assert.Equal(-7.5, m.MaxGpuTemperatureC);
        Assert.Equal(-8.5, m.AverageGpuPowerWatts);
        Assert.Equal("", m.Backend);
        Assert.Equal("", m.HardwareKey);
    }

    [Fact]
    public void Accepts_null_string_for_backend_and_hardware_key()
    {
        RuntimeBuildPerformanceMeasurement m = new(
            1, 2, 3, 4, 5, 6, 7, 8, null!, null!);

        Assert.Null(m.Backend);
        Assert.Null(m.HardwareKey);
    }
}
