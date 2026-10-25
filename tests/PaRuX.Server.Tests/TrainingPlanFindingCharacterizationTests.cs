using System.Reflection;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingPlanFindingCharacterizationTests
{
    [Fact]
    public void New_instance_defaults_severity_to_warning()
    {
        TrainingPlanFinding finding = new();
        Assert.Equal("Warning", finding.Severity);
    }

    [Fact]
    public void New_instance_defaults_field_and_message_to_empty_string()
    {
        TrainingPlanFinding finding = new();
        Assert.Equal(string.Empty, finding.Field);
        Assert.Equal(string.Empty, finding.Message);
    }

    [Theory]
    [InlineData("Blocking", "lora_rank", "Rank too high for dataset size")]
    [InlineData("Warning", "max_steps", "Steps below recommended minimum")]
    [InlineData("Info", "", "")]
    public void Properties_round_trip_assigned_values(string severity, string field, string message)
    {
        TrainingPlanFinding finding = new();
        finding.Severity = severity;
        finding.Field = field;
        finding.Message = message;

        Assert.Equal(severity, finding.Severity);
        Assert.Equal(field, finding.Field);
        Assert.Equal(message, finding.Message);
    }

    [Fact]
    public void Type_is_a_sealed_class_in_the_PaRuX_Contracts_namespace()
    {
        Type type = typeof(TrainingPlanFinding);
        Assert.True(type.IsSealed);
        Assert.True(type.IsClass);
        Assert.False(type.IsAbstract);
        Assert.Equal("PaRuX.Contracts", type.Namespace);
    }

    [Fact]
    public void Type_exposes_exactly_the_three_public_auto_properties()
    {
        PropertyInfo[] properties = typeof(TrainingPlanFinding).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        string[] names = properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "Field", "Message", "Severity" }, names);
        Assert.All(properties, property => Assert.Equal(typeof(string), property.PropertyType));
        Assert.All(properties, property => Assert.True(property.CanRead && property.CanWrite));
    }

    [Fact]
    public void Two_instances_are_independent_reference_values()
    {
        TrainingPlanFinding first = new();
        TrainingPlanFinding second = new();

        first.Severity = "Blocking";
        first.Field = "max_sequence_length";
        first.Message = "Truncated samples detected";

        Assert.Equal("Warning", second.Severity);
        Assert.Equal(string.Empty, second.Field);
        Assert.Equal(string.Empty, second.Message);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Severity_ordering_matches_cli_blocking_first_rule()
    {
        TrainingPlanFinding blocking = new() { Severity = "Blocking", Field = "train_jsonl", Message = "Dataset missing" };
        TrainingPlanFinding warning = new() { Severity = "Warning", Field = "max_steps", Message = "Low steps" };

        List<TrainingPlanFinding> findings = new() { warning, blocking };

        Assert.Equal(new[] { blocking, warning }, findings.OrderBy(value => value.Severity == "Blocking" ? 0 : 1).ToArray());
    }
}
