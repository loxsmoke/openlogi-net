using System.Linq;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Tests.App;

/// <summary>
/// The Keyboard Shortcut row of the action pickers: where it sits, how picking it
/// opens the recorder, and how a recorded / cleared / cancelled chord lands in the
/// editor and in the persisted action.
/// </summary>
public class ShortcutPickerTests
{
    private static readonly KeyCombo CtrlShiftP = KeyCombo.Parse("Ctrl+Shift+P");
    private static readonly KeyCombo AltF4 = KeyCombo.Parse("Alt+F4");

    private static GestureDirectionBindingViewModel Editor(
        MouseAction current, List<MouseAction>? edits = null, ShortcutRecorder? recorder = null) =>
        new(GestureDirection.Up, current, ButtonBindingViewModel.Catalog, (_, a) => edits?.Add(a), recorder);

    private static ShortcutRecorder Returns(KeyCombo? result) => _ => Task.FromResult(result);

    /// <summary>The recorder runs fire-and-forget off the setter; give its continuation a moment.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++) await Task.Yield();
    }

    private static int IndexOfKeyboardHeader(ActionPickerEditor vm) =>
        vm.GroupedChoices.ToList().FindIndex(i => i is ActionGroupHeader { Category: Category.Keyboard });

    [Fact]
    public void KeyboardGroupSitsRightAfterTheMouseGroup()
    {
        var vm = Editor(MouseAction.Copy);
        var items = vm.GroupedChoices;
        var header = IndexOfKeyboardHeader(vm);

        Assert.True(header > 0);
        Assert.Equal(Category.Mouse, ((ActionChoice)items[header - 1]).Action.Category());
        Assert.Same(vm.ShortcutRow, items[header + 1]);
        Assert.Equal(Category.Scroll, ((ActionGroupHeader)items[header + 2]).Category);
        Assert.Equal(ActionKind.None, ((ActionChoice)items[^1]).Action.Kind);
    }

    [Fact]
    public void SharedCatalogHasNoShortcutRow_EachEditorOwnsOne()
    {
        Assert.DoesNotContain(ButtonBindingViewModel.GroupedCatalog, i => i is ShortcutChoice);
        Assert.DoesNotContain(ButtonBindingViewModel.GroupedCatalog, i => i is ActionGroupHeader { Category: Category.Keyboard });
        var a = Editor(MouseAction.Copy);
        var b = Editor(MouseAction.Copy);
        Assert.NotSame(a.ShortcutRow, b.ShortcutRow);
        // Everything else is the shared instance, so items from the static list select here too.
        Assert.Contains(ButtonBindingViewModel.GroupedCatalog[1], a.GroupedChoices);
    }

    [Fact]
    public async Task PickingUnassignedRow_OpensRecorderAndSelectsOnResult()
    {
        var edits = new List<MouseAction>();
        var asked = new List<KeyCombo>();
        var tcs = new TaskCompletionSource<KeyCombo?>();
        var vm = Editor(MouseAction.Copy, edits, current => { asked.Add(current); return tcs.Task; });
        var before = vm.Selected;

        vm.SelectedPickerItem = vm.ShortcutRow;

        Assert.Equal([KeyCombo.Empty], asked);
        Assert.Same(before, vm.Selected); // nothing selected or persisted until the dialog returns
        Assert.Empty(edits);

        tcs.SetResult(CtrlShiftP);
        await Settle();

        Assert.Same(vm.ShortcutRow, vm.Selected);
        Assert.Equal([MouseAction.CustomShortcut(CtrlShiftP)], edits);
        Assert.Equal("Ctrl+Shift+P", vm.ShortcutRow.Detail);
        Assert.True(vm.IsShortcutAssigned);
    }

    [Fact]
    public async Task CancelledRecorder_LeavesSelectionAndPersistsNothing()
    {
        var edits = new List<MouseAction>();
        var vm = Editor(MouseAction.Copy, edits, Returns(null));

        vm.SelectedPickerItem = vm.ShortcutRow;
        await Settle();

        Assert.Equal(MouseAction.Copy, vm.Selected.Action);
        Assert.Empty(edits);
        Assert.False(vm.ShortcutRow.IsAssigned);
    }

    [Fact]
    public async Task ClearedRecorder_SelectsRowShowingNone_AndPersistsEmptyShortcut()
    {
        var edits = new List<MouseAction>();
        var vm = Editor(MouseAction.Copy, edits, Returns(KeyCombo.Empty));

        vm.SelectedPickerItem = vm.ShortcutRow;
        await Settle();

        Assert.Same(vm.ShortcutRow, vm.Selected);
        Assert.Equal("None", vm.ShortcutRow.Detail);
        Assert.Equal([MouseAction.CustomShortcut(KeyCombo.Empty)], edits);
        Assert.True(vm.IsShortcutAssigned); // the row is selected, so Change… stays available
    }

    [Fact]
    public async Task ThrowingRecorder_CountsAsCancel()
    {
        var edits = new List<MouseAction>();
        var vm = Editor(MouseAction.Copy, edits, _ => throw new InvalidOperationException());

        vm.SelectedPickerItem = vm.ShortcutRow;
        await Settle();

        Assert.Equal(MouseAction.Copy, vm.Selected.Action);
        Assert.Empty(edits);
    }

    [Fact]
    public void HeaderHop_SkipsUnassignedRow_WithoutOpeningRecorder()
    {
        var asked = 0;
        var vm = Editor(MouseAction.Copy, recorder: _ => { asked++; return Task.FromResult<KeyCombo?>(CtrlShiftP); });
        var header = IndexOfKeyboardHeader(vm);
        var lastMouse = (ActionChoice)vm.GroupedChoices[header - 1];
        var firstScroll = (ActionChoice)vm.GroupedChoices[header + 3];
        vm.SetSelectedSilently(lastMouse.Action);

        vm.SelectedPickerItem = vm.GroupedChoices[header]; // wheel down out of Mouse

        Assert.Same(firstScroll, vm.Selected);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void HeaderHop_LandsOnAssignedRow_AndPersists()
    {
        var edits = new List<MouseAction>();
        var vm = Editor(MouseAction.Copy, edits);
        var header = IndexOfKeyboardHeader(vm);
        vm.SetSelectedSilently(((ActionChoice)vm.GroupedChoices[header - 1]).Action);
        vm.ShortcutRow.Combo = CtrlShiftP;

        vm.SelectedPickerItem = vm.GroupedChoices[header];

        Assert.Same(vm.ShortcutRow, vm.Selected);
        Assert.Equal([MouseAction.CustomShortcut(CtrlShiftP)], edits);
    }

    [Fact]
    public void PickingAssignedRow_PersistsWithoutRecorder()
    {
        var edits = new List<MouseAction>();
        var asked = 0;
        var vm = Editor(MouseAction.Copy, edits, _ => { asked++; return Task.FromResult<KeyCombo?>(AltF4); });
        vm.ShortcutRow.Combo = CtrlShiftP;

        vm.SelectedPickerItem = vm.ShortcutRow;

        Assert.Same(vm.ShortcutRow, vm.Selected);
        Assert.Equal([MouseAction.CustomShortcut(CtrlShiftP)], edits);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task ChangeCommand_PersistsNewChord_WithSelectionUnchanged()
    {
        var edits = new List<MouseAction>();
        var asked = new List<KeyCombo>();
        var vm = Editor(MouseAction.CustomShortcut(CtrlShiftP), edits,
            current => { asked.Add(current); return Task.FromResult<KeyCombo?>(AltF4); });
        Assert.Same(vm.ShortcutRow, vm.Selected);

        await vm.ChangeShortcutCommand.ExecuteAsync(null);

        Assert.Equal([CtrlShiftP], asked); // seeded with the current chord
        Assert.Same(vm.ShortcutRow, vm.Selected);
        Assert.Equal([MouseAction.CustomShortcut(AltF4)], edits);
        Assert.Equal("Alt+F4", vm.ShortcutRow.Detail);
    }

    [Fact]
    public async Task ChangeCommand_CancelKeepsOldChord_ClearPersistsEmpty()
    {
        var edits = new List<MouseAction>();
        KeyCombo? reply = null;
        var vm = Editor(MouseAction.CustomShortcut(CtrlShiftP), edits, _ => Task.FromResult(reply));

        await vm.ChangeShortcutCommand.ExecuteAsync(null);
        Assert.Empty(edits);
        Assert.Equal("Ctrl+Shift+P", vm.ShortcutRow.Detail);

        reply = KeyCombo.Empty;
        await vm.ChangeShortcutCommand.ExecuteAsync(null);
        Assert.Equal([MouseAction.CustomShortcut(KeyCombo.Empty)], edits);
        Assert.Equal("None", vm.ShortcutRow.Detail);
        Assert.Same(vm.ShortcutRow, vm.Selected);
    }

    [Fact]
    public void SetSelectedSilently_ShowsShortcutWithoutPersisting()
    {
        var edits = new List<MouseAction>();
        var vm = Editor(MouseAction.Copy, edits);

        vm.SetSelectedSilently(MouseAction.CustomShortcut(CtrlShiftP));

        Assert.Same(vm.ShortcutRow, vm.Selected);
        Assert.Equal("Ctrl+Shift+P", vm.ShortcutRow.Detail);
        Assert.Empty(edits);
    }

    [Fact]
    public void Apply_BetweenTwoShortcuts_PersistsExactlyOnce()
    {
        var edits = new List<MouseAction>();
        var vm = Editor(MouseAction.CustomShortcut(CtrlShiftP), edits);

        vm.Apply(MouseAction.CustomShortcut(AltF4)); // same row, new chord — e.g. undo

        Assert.Equal([MouseAction.CustomShortcut(AltF4)], edits);
        Assert.Equal("Alt+F4", vm.ShortcutRow.Detail);
    }

    [Fact]
    public async Task StaleRecorderResult_IsDiscarded()
    {
        var edits = new List<MouseAction>();
        var tcs = new TaskCompletionSource<KeyCombo?>();
        var vm = Editor(MouseAction.Copy, edits, _ => tcs.Task);

        vm.SelectedPickerItem = vm.ShortcutRow;   // dialog open…
        vm.SetSelectedSilently(MouseAction.Paste); // …meanwhile the editor was re-seeded
        tcs.SetResult(CtrlShiftP);
        await Settle();

        Assert.Equal(MouseAction.Paste, vm.Selected.Action);
        Assert.Empty(edits);
    }

    [Fact]
    public void ButtonSummaryShowsTheChord()
    {
        var vm = new ButtonBindingViewModel(ButtonId.Back, MouseAction.CustomShortcut(CtrlShiftP),
            ButtonBindingViewModel.Catalog, (_, _) => { });
        Assert.Equal("Ctrl+Shift+P", vm.SummaryLabel);
        Assert.True(vm.IsShortcutAssigned);
    }

    [Fact]
    public void UnknownStoredAction_FallsBackToButtonDefault_NeverLeftClick()
    {
        var edits = new List<MouseAction>();
        var button = new ButtonBindingViewModel(ButtonId.Back, MouseAction.SetDpiPreset(2),
            ButtonBindingViewModel.Catalog, (_, a) => edits.Add(a));
        Assert.Equal(Bindings.DefaultBinding(ButtonId.Back), button.Selected.Action);
        Assert.NotEqual(MouseAction.LeftClick, button.Selected.Action);

        var direction = Editor(MouseAction.SetDpiPreset(2), edits);
        Assert.Equal(MouseAction.None, direction.Selected.Action);

        Assert.Empty(edits); // seeding never persists
    }
}
