using OpenLogi.Core.Localization;

namespace OpenLogi.Core.Actions;

public static class CategoryExtensions
{
    /// <summary>Display order of the groups in the action picker: the enum's declaration order.</summary>
    public static readonly Category[] PickerOrder = Enum.GetValues<Category>();

    /// <summary>Group-header label for the action picker, from the member's <see cref="LabelAttribute"/>.</summary>
    public static string Label(this Category c) =>
        Loc.Current[EnumMetadata.Get<Category, LabelAttribute>(c).ResourceKey];
}
