using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeUpdateRegressionGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-runtime-regression-" + Guid.NewGuid().ToString("N"));

    public RuntimeUpdateRegressionGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(10.0, 10.2, 1.0, true)]
    [InlineData(10.0, 10.05, 1.0, false)]
    [InlineData(10.0, 9.9, 0.0, false)]
    [InlineData(0.0, 5.0, 1.0, true)]
    [InlineData(10.0, 0.0, 1.0, false)]
    public void Promotion_requires_real_threshold_gain(
        double original, double candidate, double threshold, bool expected)
    {
        Assert.Equal(expected, RuntimeUpdateRegressionGuard.ShouldPromote(original, candidate, threshold));
    }

    [Fact]
    public void Evidence_store_is_durable_and_newest_first()
    {
        string path = Path.Combine(_root, "evidence.json");
        var store = new RuntimeRegressionEvidenceStore(path);
        store.Append(new RuntimeRegressionEvidenceDto
        {
            Id = "old", Backend = "text-cuda", OriginalTag = "b1", CandidateTag = "b2",
            ActiveTag = "b1", Decision = "rejected", Reason = "slower", MeasuredUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        store.Append(new RuntimeRegressionEvidenceDto
        {
            Id = "new", Backend = "text-cuda", OriginalTag = "b1", CandidateTag = "b3",
            ActiveTag = "b3", Decision = "accepted", Reason = "faster", MeasuredUtc = DateTime.UtcNow
        });

        var reopened = new RuntimeRegressionEvidenceStore(path);
        IReadOnlyList<RuntimeRegressionEvidenceDto> items = reopened.List();

        Assert.Equal(2, items.Count);
        Assert.Equal("new", items[0].Id);
        Assert.Equal("old", items[1].Id);
    }

    [Fact]
    public void Evidence_store_is_bounded_to_one_hundred_rows()
    {
        string path = Path.Combine(_root, "bounded.json");
        var store = new RuntimeRegressionEvidenceStore(path);
        for (int i = 0; i < 120; i++)
            store.Append(new RuntimeRegressionEvidenceDto
            {
                Id = i.ToString(), Backend = "text-cuda", OriginalTag = "old", CandidateTag = "new",
                ActiveTag = "old", Decision = "rejected", Reason = "test", MeasuredUtc = DateTime.UtcNow.AddSeconds(i)
            });

        IReadOnlyList<RuntimeRegressionEvidenceDto> items = store.List();
        Assert.Equal(100, items.Count);
        Assert.Equal("119", items[0].Id);
        Assert.DoesNotContain(items, item => item.Id == "0");
    }
}
