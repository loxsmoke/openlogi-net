namespace OpenLogi.Core.Gestures;

/// <summary>
/// One of the five gesture-button sub-bindings: hold + swipe up/down/left/right,
/// or a plain click. Variant names are TOML-stable. Ported from Rust
/// <c>binding::GestureDirection</c>.
/// </summary>
public enum GestureDirection { Up, Down, Left, Right, Click }
