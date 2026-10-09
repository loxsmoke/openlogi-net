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
    public void CatalogIsEveryPayloadFreeKind()
    {
        var expected = Enum.GetValues<ActionKind>().Where(k => MouseAction.Info(k).Payload == ActionPayload.None).ToList();
        Assert.Equal(expected, MouseAction.Catalog().Select(a => a.Kind).ToList());
    }

    [Fact]
    public void EveryActionKindHasActionAttribute()
    {
        foreach (var kind in Enum.GetValues<ActionKind>())
            Assert.NotNull(MouseAction.Info(kind));
    }

    [Fact]
    public void EveryCategoryHasLabelAttribute()
    {
        foreach (var c in Enum.GetValues<Category>())
            Assert.StartsWith("Category_", EnumMetadata.Get<Category, LabelAttribute>(c).ResourceKey);
    }

    [Fact]
    public void KeyboardGroupFollowsMouseGroup()
    {
        Assert.Equal(Category.Mouse, CategoryExtensions.PickerOrder[0]);
        Assert.Equal(Category.Keyboard, CategoryExtensions.PickerOrder[1]);
        Assert.Equal(Category.Keyboard, MouseAction.CustomShortcut(KeyCombo.Parse("Ctrl+P")).Category());
    }

    [Fact]
    public void KeyComboTextIsCtrlAltShiftWinThenKey()
    {
        var all = ShortcutModifiers.Ctrl | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Win;
        Assert.Equal("Ctrl+Alt+Shift+Win+P", new KeyCombo(all, 0x50).ToString());
        Assert.Equal("Ctrl+Shift+P", new KeyCombo(ShortcutModifiers.Shift | ShortcutModifiers.Ctrl, 0x50).ToString());
        Assert.Equal("F5", new KeyCombo(ShortcutModifiers.None, 0x74).ToString());
        Assert.Equal("", KeyCombo.Empty.ToString());
    }

    [Fact]
    public void KeyComboParsesCaseInsensitivelyWithAliases()
    {
        Assert.Equal(new KeyCombo(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, 0x50), KeyCombo.Parse("ctrl+shift+p"));
        Assert.Equal(new KeyCombo(ShortcutModifiers.Win, 0x25), KeyCombo.Parse("Win + Left"));
        Assert.Equal(new KeyCombo(ShortcutModifiers.Ctrl, 0x1B), KeyCombo.Parse("Control+Escape"));
        Assert.Equal(new KeyCombo(ShortcutModifiers.Alt, 0x21), KeyCombo.Parse("Alt+PgUp"));
        Assert.Equal(KeyCombo.Empty, KeyCombo.Parse(""));
        Assert.Equal(KeyCombo.Empty, KeyCombo.Parse("  "));
    }

    [Theory]
    [InlineData("Ctrl+Bogus")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+P")]
    public void KeyComboRejectsUnknownOrKeylessText(string text)
    {
        Assert.Throws<FormatException>(() => KeyCombo.Parse(text));
        Assert.False(KeyCombo.TryParse(text, out _));
    }

    [Fact]
    public void EveryKeyNameRoundtrips()
    {
        foreach (var (vk, name) in KeyCombo.KeyNames)
        {
            var combo = new KeyCombo(ShortcutModifiers.Ctrl, vk);
            Assert.Equal(combo, KeyCombo.Parse(combo.ToString()));
            Assert.Equal(name, KeyCombo.KeyName(vk));
        }
    }

    [Fact]
    public void UnmappedKeyRendersAsVkAndParsesBack()
    {
        var combo = new KeyCombo(ShortcutModifiers.Alt, 0xE7);
        Assert.Equal("Alt+VK0xE7", combo.ToString());
        Assert.Equal(combo, KeyCombo.Parse("alt+vk0xe7"));
    }

    [Fact]
    public void ShortcutModifiersMatchAvaloniaKeyModifiers()
    {
        // Avalonia.Input.KeyModifiers: Alt = 1, Control = 2, Shift = 4, Meta = 8 — a plain cast must work.
        Assert.Equal(1, (int)ShortcutModifiers.Alt);
        Assert.Equal(2, (int)ShortcutModifiers.Ctrl);
        Assert.Equal(4, (int)ShortcutModifiers.Shift);
        Assert.Equal(8, (int)ShortcutModifiers.Win);
    }

    [Fact]
    public void KeyComboLabelIsNoneWhenEmpty()
    {
        Assert.Equal("None", KeyCombo.Empty.Label());
        Assert.Equal("None", MouseAction.CustomShortcut(KeyCombo.Empty).Label());
        Assert.Equal("Ctrl+Shift+P", MouseAction.CustomShortcut(KeyCombo.Parse("Ctrl+Shift+P")).Label());
    }

    [Fact]
    public void CustomShortcutEqualityIsByChord()
    {
        Assert.Equal(MouseAction.CustomShortcut(KeyCombo.Parse("Ctrl+P")), MouseAction.CustomShortcut(KeyCombo.Parse("ctrl+p")));
        Assert.NotEqual(MouseAction.CustomShortcut(KeyCombo.Parse("Ctrl+P")), MouseAction.CustomShortcut(KeyCombo.Parse("Ctrl+Q")));
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
