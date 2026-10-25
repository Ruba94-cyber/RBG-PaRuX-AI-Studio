using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Each typed character is a press and a release, and Windows can refuse either one. Before this, the SendInput results
/// were discarded, so a character Windows refused was counted as typed. The decision is tested with a fake driver.
/// </summary>
public sealed class ComputerUseKeyboardTests
{
    private sealed class KeyboardDriver : IComputerUseInputDriver
    {
        /// <summary>The Nth keyboard event (1-based) reports zero inserted events; every other event succeeds.</summary>
        public int RefuseEventNumber { get; set; } = int.MaxValue;

        public int Events { get; private set; }
        public (ushort VirtualKey, ushort Scan, uint Flags) Last { get; private set; }

        public bool MoveCursor(int x, int y) => true;

        public uint SendMouseEvent(uint flags, uint mouseData) => 1u;

        public uint SendKeyboardEvent(ushort virtualKey, ushort scanCode, uint flags)
        {
            Events++;
            Last = (virtualKey, scanCode, flags);
            return Events == RefuseEventNumber ? 0u : 1u;
        }
    }

    [Fact]
    public void A_character_is_sent_as_a_unicode_press_followed_by_a_unicode_release()
    {
        var driver = new KeyboardDriver();

        bool sent = ComputerUseKeyboard.TypeCharacter(driver, 'é');

        Assert.True(sent);
        Assert.Equal(2, driver.Events);
        Assert.Equal((ushort)0, driver.Last.VirtualKey);
        Assert.Equal((ushort)'é', driver.Last.Scan);
        Assert.Equal(ComputerUseKeyboard.KeyEventUnicode | ComputerUseKeyboard.KeyEventKeyUp, driver.Last.Flags);
    }

    [Fact]
    public void A_refused_press_is_reported_as_not_typed()
    {
        var driver = new KeyboardDriver { RefuseEventNumber = 1 };

        Assert.False(ComputerUseKeyboard.TypeCharacter(driver, 'x'));
    }

    [Fact]
    public void A_refused_release_is_reported_as_not_typed_so_a_stuck_key_is_never_hidden()
    {
        var driver = new KeyboardDriver { RefuseEventNumber = 2 };

        Assert.False(ComputerUseKeyboard.TypeCharacter(driver, 'x'));
    }
}
