using OpenLogi.Core.Actions;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Core.Config;

/// <summary>Settings scoped to a single physical device.</summary>
public sealed class DeviceConfig
{
    public GestureOwner? GestureOwner { get; set; }
    public DeviceIdentity? Identity { get; set; }
    public SortedDictionary<ButtonId, Binding> Bindings { get; set; } = new();
    public SortedDictionary<string, SortedDictionary<ButtonId, Actions.MouseAction>> PerAppBindings { get; set; } = new(StringComparer.Ordinal);
    public List<uint> DpiPresets { get; set; } = [];
    public uint? Dpi { get; set; }
    public Lighting? Lighting { get; set; }
    public SmartShift? SmartShift { get; set; }
    public bool InvertScroll { get; set; }
    /// <summary>Hi-res smooth scrolling (HiResWheel 0x2121 diverted + re-injected while the agent runs).</summary>
    public bool SmoothScroll { get; set; }
}
