using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;

namespace OpenLogi.Tests;

public class ActionTests
{
    [Fact]
    public void CatalogHasAtLeast29Entries()
    {
        Assert.True(MouseAction.Catalog().Count >= 29);
    }

    [Fact]
    public void CatalogExcludesCustomShortcut()
    {
        Assert.DoesNotContain(MouseAction.Catalog(), a => a.Kind == ActionKind.CustomShortcut);
    }

    [Fact]
    public void KeyComboRenderedLabelUsesDisplayWhenSet()
    {
        var combo = new KeyCombo { Modifiers = 0, KeyCode = 0, Display = "preset" };
        Assert.Equal("preset", combo.RenderedLabel());
    }

    [Fact]
    public void KeyComboRenderedLabelFallsBackToModifiersPlusKey()
    {
        var combo = new KeyCombo
        {
            Modifiers = (byte)(KeyCombo.ModCmd | KeyCombo.ModShift),
            KeyCode = 0x23, // P
            Display = "",
        };
        Assert.Equal("⇧⌘P", combo.RenderedLabel());
    }

    [Fact]
    public void DpiToggleDefaultIsCycleDpiPresets()
    {
        Assert.Equal(MouseAction.CycleDpiPresets, Bindings.DefaultBinding(ButtonId.DpiToggle));
    }

    [Fact]
    public void SetDpiPresetLabelIsOneBased()
    {
        Assert.Equal("DPI Preset 3", MouseAction.SetDpiPreset(2).Label());
    }

    [Theory]
    [InlineData(ActionKind.Copy, Category.Editing)]
    [InlineData(ActionKind.BrowserBack, Category.Browser)]
    [InlineData(ActionKind.PlayPause, Category.Media)]
    [InlineData(ActionKind.LeftClick, Category.Mouse)]
    [InlineData(ActionKind.CycleDpiPresets, Category.Dpi)]
    [InlineData(ActionKind.ScrollUp, Category.Scroll)]
    [InlineData(ActionKind.TaskView, Category.Navigation)]
    [InlineData(ActionKind.LockScreen, Category.System)]
    public void CategoryAssignment(ActionKind kind, Category expected)
    {
        Assert.Equal(expected, MouseAction.Unit(kind).Category());
    }

    [Fact]
    public void CategoryLabelsAreNonEmpty()
    {
        foreach (Category c in Enum.GetValues<Category>())
            Assert.NotEqual("", c.Label());
    }

    [Fact]
    public void UnitFactoryRejectsPayloadKinds()
    {
        Assert.Throws<ArgumentException>(() => MouseAction.Unit(ActionKind.SetDpiPreset));
        Assert.Throws<ArgumentException>(() => MouseAction.Unit(ActionKind.CustomShortcut));
    }
}
