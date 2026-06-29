using System.Text.Json.Serialization;

namespace OpenLogi.Assets;

/// <summary>
/// Per-depot hotspot metadata (<c>core_metadata.json</c> / <c>metadata.json</c>).
/// Ported from Rust <c>metadata.rs</c>. <c>marker.{x,y}</c> are percentages 0..100
/// of the silhouette bbox (<see cref="Origin"/>); <c>label.{x,y}</c> are direction
/// codes (-1 left, 0 centre, +1 right) for the annotation card.
/// </summary>
public sealed class Metadata
{
    [JsonPropertyName("images")] public List<ImageEntry> Images { get; set; } = [];

    /// <summary>Image dimensions (the first entry; both entries share the same origin in practice).</summary>
    public Origin? OriginOf() => Images.Count > 0 ? Images[0].Origin : null;

    /// <summary>Assignments on the <c>device_buttons_image</c> entry (the hotspot markers).</summary>
    public IEnumerable<Assignment> Assignments() =>
        Images.FirstOrDefault(i => i.Key == "device_buttons_image")?.Assignments ?? [];
}

public sealed class ImageEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("origin")] public Origin Origin { get; set; } = new();
    [JsonPropertyName("assignments")] public List<Assignment> Assignments { get; set; } = [];
}

public sealed class Origin
{
    [JsonPropertyName("width")] public uint Width { get; set; }
    [JsonPropertyName("height")] public uint Height { get; set; }
}

public sealed class Assignment
{
    [JsonPropertyName("slotName")] public string SlotName { get; set; } = "";
    [JsonPropertyName("marker")] public Point Marker { get; set; } = new();
    [JsonPropertyName("label")] public Direction Label { get; set; } = new();
}

public sealed class Point
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
}

public sealed class Direction
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}
