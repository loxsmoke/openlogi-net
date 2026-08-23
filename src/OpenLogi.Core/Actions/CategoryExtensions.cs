using OpenLogi.Core.Localization;

namespace OpenLogi.Core.Actions;

public static class CategoryExtensions
{
    /// <summary>Display order of the groups in the action picker.</summary>
    public static readonly Category[] PickerOrder =
    [
        Category.Mouse, Category.Scroll, Category.Navigation, Category.Browser,
        Category.Editing, Category.Media, Category.Dpi, Category.System,
    ];

    /// <summary>Group-header label for the action picker.</summary>
    public static string Label(this Category c) => c switch
    {
        Category.Editing => Loc.Current["Category_Editing"],
        Category.Browser => Loc.Current["Category_Browser"],
        Category.Media => Loc.Current["Category_Media"],
        Category.Mouse => Loc.Current["Category_Mouse"],
        Category.Dpi => Loc.Current["Category_Dpi"],
        Category.Scroll => Loc.Current["Category_Scroll"],
        Category.Navigation => Loc.Current["Category_Navigation"],
        Category.System => Loc.Current["Category_System"],
        _ => c.ToString(),
    };
}
