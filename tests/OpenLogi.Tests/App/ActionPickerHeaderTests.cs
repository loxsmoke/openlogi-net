using System.Linq;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Tests.App;

/// <summary>
/// The action pickers list group headers alongside choices. Headers are click-proof
/// (IsEnabled), but wheel and arrow-key selection walks the list by index and hands a
/// header to the bound property regardless (issue: wheeling the Gestures panel's picker
/// up to row 0, the "Mouse Gestures" header, threw System.InvalidCastException).
/// </summary>
public class ActionPickerHeaderTests
{
    private static IReadOnlyList<IActionPickerItem> Grouped => ButtonBindingViewModel.GroupedCatalog;

    private static GestureDirectionBindingViewModel Editor(
        MouseAction current, Action<GestureDirection, MouseAction>? persist = null) =>
        new(GestureDirection.Up, current, ButtonBindingViewModel.Catalog, persist ?? ((_, _) => { }));

    [Fact]
    public void FirstRowIsAHeader_SoTheWheelCanReachOne()
    {
        Assert.IsType<ActionGroupHeader>(Grouped[0]);
    }

    [Fact]
    public void HeaderAtTopOfList_KeepsCurrentSelection()
    {
        var first = (ActionChoice)Grouped[1];
        var vm = Editor(first.Action);

        vm.SelectedPickerItem = Grouped[0]; // wheel up off the first action onto the header

        Assert.Equal(first, vm.Selected);
    }

    [Fact]
    public void HeaderBelowSelection_LandsOnFirstActionAfterIt()
    {
        var header = Grouped.Skip(1).OfType<ActionGroupHeader>().First();
        var index = Grouped.ToList().IndexOf(header);
        var before = (ActionChoice)Grouped[index - 1];
        var after = (ActionChoice)Grouped[index + 1];
        var vm = Editor(before.Action);

        vm.SelectedPickerItem = header; // wheel down out of one category into the next

        Assert.Equal(after, vm.Selected);
    }

    [Fact]
    public void HeaderAboveSelection_LandsOnLastActionBeforeIt()
    {
        var header = Grouped.Skip(1).OfType<ActionGroupHeader>().First();
        var index = Grouped.ToList().IndexOf(header);
        var before = (ActionChoice)Grouped[index - 1];
        var after = (ActionChoice)Grouped[index + 1];
        var vm = Editor(after.Action);

        vm.SelectedPickerItem = header; // wheel up out of one category into the previous

        Assert.Equal(before, vm.Selected);
    }

    [Fact]
    public void HeaderDoesNotPersistAnEdit()
    {
        var edits = new List<MouseAction>();
        var first = (ActionChoice)Grouped[1];
        var vm = Editor(first.Action, (_, a) => edits.Add(a));

        vm.SelectedPickerItem = Grouped[0];

        Assert.Empty(edits);
    }

    [Fact]
    public void ChoiceStillSelectsAndPersists()
    {
        var edits = new List<MouseAction>();
        var choice = Grouped.OfType<ActionChoice>().Last();
        var vm = Editor(((ActionChoice)Grouped[1]).Action, (_, a) => edits.Add(a));

        vm.SelectedPickerItem = choice;

        Assert.Equal(choice, vm.Selected);
        Assert.Equal([choice.Action], edits);
    }

    [Fact]
    public void ButtonPicker_HeaderKeepsCurrentSelectionAndPersistsNothing()
    {
        var edits = new List<MouseAction>();
        var first = (ActionChoice)Grouped[1];
        var vm = new ButtonBindingViewModel(
            ButtonId.Back, first.Action, ButtonBindingViewModel.Catalog, (_, a) => edits.Add(a));

        vm.SelectedPickerItem = Grouped[0];

        Assert.Equal(first, vm.Selected);
        Assert.Empty(edits);
    }
}
