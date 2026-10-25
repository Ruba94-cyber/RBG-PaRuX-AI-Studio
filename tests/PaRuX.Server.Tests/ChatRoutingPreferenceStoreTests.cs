using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatRoutingPreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-chat-routing-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _models = Path.Combine(
        Path.GetTempPath(), "parux-chat-routing-models-" + Guid.NewGuid().ToString("N"));

    public ChatRoutingPreferenceStoreTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_models, recursive: true); } catch { }
    }

    [Fact]
    public void Default_is_balanced_auto_without_creating_a_file()
    {
        ChatRoutingPreferenceDto preference = ChatRoutingPreferenceStore.GetDto(_root);

        Assert.Equal(RouteMode.Auto, preference.Mode);
        Assert.Equal(AutoIntelligenceProfile.Balanced, preference.AutoProfile);
        Assert.Null(preference.ManualModelFileName);
        Assert.Equal("parux-auto:balanced", preference.Selector);
        Assert.False(File.Exists(Path.Combine(_root, "chat-model-routing.json")));
    }

    [Fact]
    public void Manual_lock_is_exact_persistent_and_local_default_does_not_reset_it()
    {
        const string fileName = "Qwen-27B-IQ4_XS.gguf";
        File.WriteAllText(Path.Combine(_models, fileName), "test");

        ChatRoutingPreferenceDto selected = ChatRoutingPreferenceStore.Set(
            new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Manual,
                ManualModelFileName = fileName
            }, _root, _models);

        Assert.Equal(RouteMode.Manual, selected.Mode);
        Assert.Equal(fileName, selected.ManualModelFileName);
        Assert.Equal("parux-manual:" + fileName, selected.Selector);

        ChatRoutingPreferenceStore.Preference legacy =
            ChatRoutingPreferenceStore.ResolveAndPersist("local-default", _root, _models);
        Assert.Equal(RouteMode.Manual, legacy.Mode);
        Assert.Equal(fileName, legacy.ManualModelFileName);

        ChatRoutingPreferenceDto reloaded = ChatRoutingPreferenceStore.GetDto(_root);
        Assert.Equal("parux-manual:" + fileName, reloaded.Selector);
    }

    [Fact]
    public void Returning_to_auto_explicitly_releases_the_manual_lock()
    {
        const string fileName = "Qwen-27B.gguf";
        File.WriteAllText(Path.Combine(_models, fileName), "test");
        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        {
            Mode = RouteMode.Manual,
            ManualModelFileName = fileName
        }, _root, _models);

        ChatRoutingPreferenceDto auto = ChatRoutingPreferenceStore.Set(
            new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Auto,
                AutoProfile = AutoIntelligenceProfile.MaxIntelligence
            }, _root, _models);

        Assert.Equal(RouteMode.Auto, auto.Mode);
        Assert.Equal(AutoIntelligenceProfile.MaxIntelligence, auto.AutoProfile);
        Assert.Null(auto.ManualModelFileName);
        Assert.Equal("parux-auto:max_intelligence", auto.Selector);
    }

    [Fact]
    public void Manual_lock_rejects_missing_model_and_path_traversal()
    {
        Assert.Throws<FileNotFoundException>(() => ChatRoutingPreferenceStore.Set(
            new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Manual,
                ManualModelFileName = "missing.gguf"
            }, _root, _models));

        Assert.Throws<InvalidDataException>(() => ChatRoutingPreferenceStore.Set(
            new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Manual,
                ManualModelFileName = "..\\outside.gguf"
            }, _root, _models));
    }

    [Fact]
    public void Auto_profile_rejects_unknown_values_instead_of_silently_normalizing_them()
    {
        Assert.Throws<InvalidDataException>(() => ChatRoutingPreferenceStore.Set(
            new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Auto,
                AutoProfile = "turbo-magic"
            }, _root, _models));
    }
}
