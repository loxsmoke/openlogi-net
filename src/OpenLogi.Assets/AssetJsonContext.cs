using System.Text.Json.Serialization;

namespace OpenLogi.Assets;

/// <summary>
/// Source-generated JSON metadata for the asset-registry types. Deserializing
/// through this context instead of the reflection-based <c>JsonSerializer</c>
/// overloads keeps the asset code trim-safe, so the app can be published
/// trimmed. The three roots cover every shape fetched from the asset host:
/// the <c>index.json</c> registry, a depot <c>manifest.json</c>, and per-depot
/// hotspot <c>metadata.json</c>.
/// </summary>
[JsonSerializable(typeof(AssetIndex))]
[JsonSerializable(typeof(DepotManifest))]
[JsonSerializable(typeof(Metadata))]
internal sealed partial class AssetJsonContext : JsonSerializerContext;
