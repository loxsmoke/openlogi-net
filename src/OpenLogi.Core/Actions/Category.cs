namespace OpenLogi.Core.Actions;

/// <summary>
/// Grouping for the action-picker section headers. Members are declared in the
/// order the picker shows the groups; the enum is never written to disk, so that
/// order is free to change. Each member names its header text via <see cref="LabelAttribute"/>.
/// </summary>
public enum Category
{
    [Label("Category_Mouse")] Mouse,
    [Label("Category_Keyboard")] Keyboard,
    [Label("Category_Scroll")] Scroll,
    [Label("Category_Navigation")] Navigation,
    [Label("Category_Browser")] Browser,
    [Label("Category_Editing")] Editing,
    [Label("Category_Media")] Media,
    [Label("Category_Dpi")] Dpi,
    [Label("Category_System")] System,
}
