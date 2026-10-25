using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RoutingBackupMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-routing-mirror-" + Guid.NewGuid().ToString("N"));

    public RoutingBackupMirrorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Restored_settings_seed_manual_lock_even_when_model_is_not_downloaded_yet()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), JsonSerializer.Serialize(new
        {
            ChatRoutingMirrorVersion = 1,
            ChatModelSelection = "parux-manual:Qwen-27B-restored.gguf",
            ChatRoutingAutoProfile = "max_intelligence"
        }));

        ChatRoutingPreferenceDto restored = ChatRoutingPreferenceStore.GetDto(_root);

        Assert.Equal(RouteMode.Manual, restored.Mode);
        Assert.Equal("Qwen-27B-restored.gguf", restored.ManualModelFileName);
        Assert.Equal(AutoIntelligenceProfile.MaxIntelligence, restored.AutoProfile);
        Assert.True(File.Exists(Path.Combine(_root, "chat-model-routing.json")));
    }

    [Fact]
    public void Manual_restore_preserves_previous_custom_auto_profile_when_lock_is_released()
    {
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "Qwen-27B-restored.gguf"), "x");
        File.WriteAllText(Path.Combine(_root, "settings.json"), JsonSerializer.Serialize(new
        {
            ChatRoutingMirrorVersion = 1,
            ChatModelSelection = "parux-manual:Qwen-27B-restored.gguf",
            ChatRoutingAutoProfile = "custom"
        }));

        ChatRoutingPreferenceDto restored = ChatRoutingPreferenceStore.GetDto(_root);
        Assert.Equal(AutoIntelligenceProfile.Custom, restored.AutoProfile);

        ChatRoutingPreferenceDto auto = ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        {
            Mode = RouteMode.Auto
        }, _root, models);

        Assert.Equal(RouteMode.Auto, auto.Mode);
        Assert.Equal(AutoIntelligenceProfile.Custom, auto.AutoProfile);
        Assert.Equal("parux-auto:custom", auto.Selector);
    }

    [Fact]
    public void Restored_settings_seed_custom_auto_tuning()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), JsonSerializer.Serialize(new
        {
            ChatRoutingMirrorVersion = 1,
            ChatModelSelection = "parux-auto:custom",
            ChatRoutingAutoProfile = "custom",
            ChatAutoMinimumCapabilityRatio = 0.82,
            ChatAutoComplexityFloorBoost = 0.11,
            ChatAutoBaseCapabilityWeight = 0.61,
            ChatAutoBasePerformanceWeight = 0.17,
            ChatAutoCompatibilityWeight = 0.15,
            ChatAutoLoadedReuseWeight = 0.07,
            ChatAutoComplexityCapabilityBoost = 0.19,
            ChatAutoComplexityPerformancePenalty = 0.09
        }));

        ChatRoutingPreferenceDto preference = ChatRoutingPreferenceStore.GetDto(_root);
        AutoRoutingTuningDto tuning = AutoRoutingTuningStore.Get(_root);

        Assert.Equal(AutoIntelligenceProfile.Custom, preference.AutoProfile);
        Assert.Equal(0.82, tuning.MinimumCapabilityRatio, 3);
        Assert.Equal(0.61, tuning.BaseCapabilityWeight, 3);
        Assert.True(File.Exists(Path.Combine(_root, "auto-routing-tuning.json")));
    }

    [Fact]
    public void Invalid_mirrored_custom_tuning_does_not_overwrite_valid_sidecar()
    {
        AutoRoutingTuningStore.Set(new SetAutoRoutingTuningRequest
        {
            MinimumCapabilityRatio = 0.77,
            ComplexityFloorBoost = 0.20,
            BaseCapabilityWeight = 0.50,
            BasePerformanceWeight = 0.25,
            CompatibilityWeight = 0.17,
            LoadedReuseWeight = 0.08,
            ComplexityCapabilityBoost = 0.21,
            ComplexityPerformancePenalty = 0.12
        }, _root);

        string settingsPath = Path.Combine(_root, "settings.json");
        using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var broken = new Dictionary<string, object?>();
        foreach (JsonProperty property in existing.RootElement.EnumerateObject())
            broken[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
        broken["ChatAutoMinimumCapabilityRatio"] = 9.0;
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(broken));

        AutoRoutingTuningDto loaded = AutoRoutingTuningStore.Get(_root);

        Assert.Equal(0.77, loaded.MinimumCapabilityRatio, 3);
        Assert.Equal(0.50, loaded.BaseCapabilityWeight, 3);
    }

    [Fact]
    public void Core_mutations_update_the_versioned_backup_mirror()
    {
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "Qwen-27B.gguf"), "x");

        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        {
            Mode = RouteMode.Auto,
            AutoProfile = AutoIntelligenceProfile.Custom
        }, _root, models);
        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        {
            Mode = RouteMode.Manual,
            ManualModelFileName = "Qwen-27B.gguf"
        }, _root, models);
        AutoRoutingTuningStore.Set(new SetAutoRoutingTuningRequest
        {
            MinimumCapabilityRatio = 0.77,
            ComplexityFloorBoost = 0.20,
            BaseCapabilityWeight = 0.50,
            BasePerformanceWeight = 0.25,
            CompatibilityWeight = 0.17,
            LoadedReuseWeight = 0.08,
            ComplexityCapabilityBoost = 0.21,
            ComplexityPerformancePenalty = 0.12
        }, _root);

        string json = File.ReadAllText(Path.Combine(_root, "settings.json"));
        Assert.Contains("\"ChatRoutingMirrorVersion\": 1", json);
        Assert.Contains("parux-manual:Qwen-27B.gguf", json);
        Assert.Contains("\"ChatRoutingAutoProfile\": \"custom\"", json);
        Assert.Contains("\"ChatAutoMinimumCapabilityRatio\": 0.77", json);
    }
}
