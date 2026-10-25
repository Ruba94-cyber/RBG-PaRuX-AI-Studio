using PaRuX.Contracts;
using PaRuX.Server.Automation;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutomationDefinitionValidatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-automation-validation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Malformed_trigger_json_is_rejected_before_persistence()
    {
        var store = new AutomationStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(Request("{not json}", "{\"goalTemplate\":\"g\"}")));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Malformed_action_json_is_rejected_before_persistence()
    {
        var store = new AutomationStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(Request("{}", "{not json}")));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31536001)]
    public void Schedule_interval_is_bounded(int seconds)
    {
        var store = new AutomationStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(Request(
            "{\"intervalSeconds\":" + seconds + "}", "{\"goalTemplate\":\"g\"}")));
    }

    [Fact]
    public void Schedule_zero_remains_a_valid_immediate_trigger()
    {
        var store = new AutomationStore(_tempDir);
        AutomationDto dto = store.Create(Request("{\"intervalSeconds\":0}", "{\"goalTemplate\":\"g\"}"));
        Assert.NotNull(store.Get(dto.Id));
    }

    [Fact]
    public void Empty_action_goal_is_rejected()
    {
        var store = new AutomationStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(Request("{}", "{\"goalTemplate\":\"\"}")));
    }

    [Fact]
    public void Unknown_action_role_is_rejected_but_real_desktop_role_is_allowed()
    {
        var store = new AutomationStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(Request("{}",
            "{\"goalTemplate\":\"g\",\"role\":\"wizard\"}")));

        AutomationDto valid = store.Create(Request("{}",
            "{\"goalTemplate\":\"g\",\"role\":\"coding\"}"));
        Assert.NotNull(store.Get(valid.Id));
    }

    [Fact]
    public void File_trigger_requires_folder_and_a_filename_filter_not_a_path()
    {
        var store = new AutomationStore(_tempDir);
        CreateAutomationRequest missing = Request("{}", "{\"goalTemplate\":\"g\"}");
        missing.TriggerType = AutomationTriggerType.FileCreated;
        Assert.Throws<InvalidDataException>(() => store.Create(missing));

        CreateAutomationRequest pathFilter = Request(
            "{\"folderPath\":\"C:\\\\temp\",\"filter\":\"sub\\\\*.txt\"}",
            "{\"goalTemplate\":\"g\"}");
        pathFilter.TriggerType = AutomationTriggerType.FileCreated;
        Assert.Throws<InvalidDataException>(() => store.Create(pathFilter));
    }

    [Fact]
    public void Git_trigger_rejects_flag_shaped_repository_path_at_creation_time()
    {
        var store = new AutomationStore(_tempDir);
        CreateAutomationRequest request = Request(
            "{\"repositoryPath\":\"--output=evil\",\"branch\":\"main\"}",
            "{\"goalTemplate\":\"g\"}");
        request.TriggerType = AutomationTriggerType.GitCommit;
        Assert.Throws<InvalidDataException>(() => store.Create(request));
    }

    private static CreateAutomationRequest Request(string triggerJson, string actionJson) => new()
    {
        Name = "test",
        TriggerType = AutomationTriggerType.Schedule,
        TriggerConfigJson = triggerJson,
        ActionType = AutomationActionType.CreateAgentTask,
        ActionConfigJson = actionJson
    };
}
