using System.Collections.Frozen;
using System.Reflection;

namespace OpenLogi.Core.Actions;

/// <summary>The <c>Strings.resx</c> key of an enum member's display text.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class LabelAttribute(string resourceKey) : Attribute
{
    public string ResourceKey { get; } = resourceKey;
}

/// <summary>What extra data an <see cref="ActionKind"/> carries, and so how its label is built.</summary>
public enum ActionPayload
{
    /// <summary>The kind alone describes the action; the label is the resource string as-is.</summary>
    None,
    /// <summary>Carries a DPI preset index; the label is the resource string formatted with the 1-based index.</summary>
    DpiPreset,
    /// <summary>Carries a <see cref="KeyCombo"/>; the label is the combo text (the resource string names the picker row).</summary>
    Shortcut,
}

/// <summary>
/// Everything the UI and the config model need to know about an <see cref="ActionKind"/>
/// member, declared next to the member: its picker group, its label resource key and,
/// for the two kinds that carry data, what that data is.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class ActionAttribute(Category category, string labelKey, ActionPayload payload = ActionPayload.None) : Attribute
{
    public Category Category { get; } = category;
    public string LabelKey { get; } = labelKey;
    public ActionPayload Payload { get; } = payload;
}

/// <summary>
/// Attribute lookup for enum members, built once per (enum, attribute) pair. A member
/// without the attribute is a programming error and fails loudly on first use.
/// </summary>
public static class EnumMetadata
{
    public static TAttr Get<TEnum, TAttr>(TEnum value)
        where TEnum : struct, Enum
        where TAttr : Attribute =>
        Cache<TEnum, TAttr>.Map.Value.TryGetValue(value, out var attr)
            ? attr
            : throw new ArgumentOutOfRangeException(nameof(value), value, null);

    private static class Cache<TEnum, TAttr>
        where TEnum : struct, Enum
        where TAttr : Attribute
    {
        public static readonly Lazy<FrozenDictionary<TEnum, TAttr>> Map = new(Build);

        private static FrozenDictionary<TEnum, TAttr> Build()
        {
            var map = new Dictionary<TEnum, TAttr>();
            foreach (var value in Enum.GetValues<TEnum>())
            {
                var name = value.ToString();
                var attr = typeof(TEnum).GetField(name)?.GetCustomAttribute<TAttr>()
                    ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{name}: missing [{typeof(TAttr).Name}]");
                map[value] = attr;
            }
            return map.ToFrozenDictionary();
        }
    }
}
