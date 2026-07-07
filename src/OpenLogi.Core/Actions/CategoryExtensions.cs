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
        Category.Editing => "Editing",
        Category.Browser => "Browser & Tabs",
        Category.Media => "Media & Volume",
        Category.Mouse => "Mouse Buttons",
        Category.Dpi => "DPI & Wheel",
        Category.Scroll => "Scrolling",
        Category.Navigation => "Windows & Desktops",
        Category.System => "System",
        _ => c.ToString(),
    };
}
