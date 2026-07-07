using OpenLogi.Core.Config;

namespace OpenLogi.Core.Gestures;

/// <summary>
/// Which control owns a device's single gesture role: explicitly off, or a named
/// button. Serialized as a bare string ("Off" or a <see cref="ButtonId"/> name).
/// Ported from Rust <c>config::GestureOwner</c>.
/// </summary>
public abstract record GestureOwner
{
    private GestureOwner() { }

    /// <summary>Gestures explicitly turned off for this device.</summary>
    public sealed record Off : GestureOwner;

    /// <summary>The named button owns the gesture role.</summary>
    public sealed record Button(ButtonId Id) : GestureOwner;

    public static readonly Off OffValue = new();
}
