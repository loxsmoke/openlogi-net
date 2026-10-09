using OpenLogi.App.ViewModels;
using OpenLogi.Core.Actions;

namespace OpenLogi.Tests.App;

/// <summary>
/// The chord recorder's state machine: live modifier mirroring while waiting for a
/// key, a frozen chord once one is recorded, Clear to start over.
/// </summary>
public class ShortcutRecorderViewModelTests
{
    private const ushort VK_SHIFT = 0x10, VK_LCONTROL = 0xA2, VK_LWIN = 0x5B, VK_MENU = 0x12;
    private const ushort VK_ESCAPE = 0x1B, VK_TAB = 0x09, VK_P = 0x50;

    private static ShortcutRecorderViewModel Fresh() => new(KeyCombo.Empty);

    [Fact]
    public void StartsCapturingWithTheSeededChord()
    {
        var vm = new ShortcutRecorderViewModel(KeyCombo.Parse("Ctrl+Shift+P"));
        Assert.True(vm.HasKey);
        Assert.False(vm.Capturing);
        Assert.True(vm.Ctrl);
        Assert.True(vm.Shift);
        Assert.Equal("Ctrl+Shift+P", vm.Preview);

        Assert.True(Fresh().Capturing);
        Assert.Equal("Press a key…", Fresh().Preview);
    }

    [Fact]
    public void WhileCapturing_ModifierKeysMirrorIntoTheToggles()
    {
        var vm = Fresh();
        vm.KeyDown(VK_SHIFT);
        vm.KeyDown(VK_LCONTROL);
        Assert.True(vm.Shift);
        Assert.True(vm.Ctrl);

        vm.KeyUp(VK_SHIFT);
        Assert.False(vm.Shift);
        Assert.True(vm.Ctrl);
        Assert.True(vm.Capturing); // modifiers alone record nothing
        Assert.Equal(KeyCombo.Empty, vm.Result);
    }

    [Fact]
    public void FirstOtherKey_IsRecordedWithTheTogglesAsShown()
    {
        var vm = Fresh();
        vm.KeyDown(VK_LWIN);
        vm.Alt = true; // clicked by hand
        vm.KeyDown(VK_TAB);

        Assert.False(vm.Capturing);
        Assert.Equal(KeyCombo.Parse("Alt+Win+Tab"), vm.Result);
        Assert.Equal("Alt+Win+Tab", vm.Preview);
    }

    [Fact]
    public void EscapeIsAnOrdinaryRecordableKey()
    {
        var vm = Fresh();
        vm.KeyDown(VK_ESCAPE);
        Assert.Equal(KeyCombo.Parse("Esc"), vm.Result);
    }

    [Fact]
    public void OnceRecorded_KeysChangeNothing_ButTogglesStillDo()
    {
        var vm = Fresh();
        vm.KeyDown(VK_P);
        vm.KeyUp(VK_P);

        vm.KeyDown(VK_SHIFT);   // a held modifier no longer mirrors…
        vm.KeyDown(VK_TAB);     // …and another key does not replace P
        vm.KeyUp(VK_SHIFT);
        Assert.Equal(KeyCombo.Parse("P"), vm.Result);

        vm.Ctrl = true;         // …but the toggle buttons still work
        Assert.Equal(KeyCombo.Parse("Ctrl+P"), vm.Result);
    }

    [Fact]
    public void ModifierReleaseAfterRecording_DoesNotDropTheRecordedModifier()
    {
        var vm = Fresh();
        vm.KeyDown(VK_MENU);
        vm.KeyDown(VK_TAB);     // Alt+Tab recorded while Alt is still physically down
        vm.KeyUp(VK_TAB);
        vm.KeyUp(VK_MENU);      // Alt let go afterwards
        Assert.Equal(KeyCombo.Parse("Alt+Tab"), vm.Result);
    }

    [Fact]
    public void HintFollowsTheState()
    {
        var vm = Fresh();
        Assert.StartsWith("Press the key combination", vm.Hint);
        vm.KeyDown(VK_P);
        Assert.StartsWith("To assign a different key, clear", vm.Hint);
        vm.Clear();
        Assert.StartsWith("Press the key combination", vm.Hint);
    }

    [Fact]
    public void Clear_ReturnsToCapturing()
    {
        var vm = new ShortcutRecorderViewModel(KeyCombo.Parse("Ctrl+Shift+P"));
        vm.Clear();
        Assert.True(vm.Capturing);
        Assert.False(vm.Ctrl);
        Assert.False(vm.Shift);
        Assert.Equal(KeyCombo.Empty, vm.Result);

        vm.KeyDown(VK_ESCAPE);  // and a new key can be recorded
        Assert.Equal(KeyCombo.Parse("Esc"), vm.Result);
    }

    [Theory]
    [InlineData(0x10, ShortcutModifiers.Shift)]
    [InlineData(0xA1, ShortcutModifiers.Shift)]
    [InlineData(0xA3, ShortcutModifiers.Ctrl)]
    [InlineData(0xA4, ShortcutModifiers.Alt)]
    [InlineData(0x5C, ShortcutModifiers.Win)]
    public void ModifierOfRecognisesGenericLeftAndRightVariants(int vk, ShortcutModifiers expected)
    {
        Assert.Equal(expected, ShortcutRecorderViewModel.ModifierOf((ushort)vk));
        Assert.Null(ShortcutRecorderViewModel.ModifierOf(VK_P));
    }
}
