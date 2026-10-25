using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentCompetitiveCorpusContractTests
{
    [Fact]
    public void HeldOutCorpus_IsRepresentativeVersionedAndIndependentlyVerified()
    {
        string repo = FindRepositoryRoot();
        string path = Path.Combine(repo, "benchmarks", "coding-agent", "parux-eval-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("schemaVersion").GetInt32() >= 4);

        JsonElement policy = root.GetProperty("evaluationPolicy");
        Assert.True(policy.GetProperty("heldOutForOptimization").GetBoolean());
        double floor = policy.GetProperty("absoluteSuccessFloorPercent").GetDouble();
        Assert.InRange(floor, 1.0, 100.0);

        JsonElement tasks = root.GetProperty("tasks");
        int taskCount = tasks.GetArrayLength();
        Assert.InRange(taskCount, 30, 50);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement task in tasks.EnumerateArray())
        {
            string id = task.GetProperty("id").GetString()!;
            string category = task.GetProperty("category").GetString()!;
            Assert.True(ids.Add(id), $"Duplicate task id: {id}");
            categoryCounts[category] = categoryCounts.TryGetValue(category, out int count) ? count + 1 : 1;

            JsonElement acceptance = task.GetProperty("acceptance");
            Assert.True(acceptance.GetProperty("requireVerificationSuccess").GetBoolean());
            Assert.True(acceptance.GetProperty("verificationCommands").GetArrayLength() > 0);
            Assert.True(acceptance.GetProperty("allowedChangedPathPrefixes").GetArrayLength() > 0);
        }

        JsonElement requiredCounts = policy.GetProperty("requiredCategoryCounts");
        foreach (JsonProperty required in requiredCounts.EnumerateObject())
        {
            Assert.True(categoryCounts.TryGetValue(required.Name, out int actual), $"Missing category: {required.Name}");
            Assert.True(actual >= required.Value.GetInt32(), $"Category {required.Name}: expected >= {required.Value.GetInt32()}, actual {actual}");
        }

        var mandatoryIds = policy.GetProperty("mandatoryCaseIds")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Assert.NotEmpty(mandatoryIds);
        Assert.All(mandatoryIds, id => Assert.Contains(id, ids));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PARUX_EXECUTION_PLAN.md")))
                return current.FullName;
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PARUX_EXECUTION_PLAN.md")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX repository root.");
    }
}
