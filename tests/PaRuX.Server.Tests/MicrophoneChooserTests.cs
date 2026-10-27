using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Real run: the Windows default capture device was "Steam Streaming Microphone", which records only silence, so voice input never heard anything.</summary>
public sealed class MicrophoneChooserTests
{
    private static MicrophoneChooser.Device D(string id, string name, bool def = false) => new(id, name, def);

    [Fact]
    public void A_real_default_microphone_is_kept()
    {
        var devices = new[] { D("a", "Microphone (Realtek(R) Audio)", true), D("b", "Microphone (Steam Streaming Microphone)") };
        Assert.Equal("a", MicrophoneChooser.Choose(devices));
    }

    [Fact]
    public void A_virtual_default_is_replaced_by_the_first_real_microphone()
    {
        var devices = new[] { D("b", "Microphone (Steam Streaming Microphone)", true), D("a", "Microphone (Realtek(R) Audio)") };
        Assert.Equal("a", MicrophoneChooser.Choose(devices));
    }

    [Fact]
    public void When_only_virtual_devices_exist_the_default_is_used_and_a_saved_choice_wins()
    {
        var virtualOnly = new[] { D("b", "Virtual Cable Output", true) };
        Assert.Equal("b", MicrophoneChooser.Choose(virtualOnly));
        var devices = new[] { D("a", "Real mic", true), D("c", "USB headset") };
        Assert.Equal("c", MicrophoneChooser.Choose(devices, "c"));
        Assert.Null(MicrophoneChooser.Choose(Array.Empty<MicrophoneChooser.Device>()));
    }
}
