using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Core.Actions;

/// <summary>
/// What a single rebindable <see cref="ButtonId"/> does: one <see cref="MouseAction"/>
/// (<see cref="Single"/>), or — for a button in gesture mode — a per-direction map
/// (<see cref="Gesture"/>). Ported from Rust <c>binding::Binding</c>. Immutable;
/// the in-place Rust mutators return a new instance here.
/// </summary>
public abstract record Binding
{
    private Binding() { }

    /// <summary>One action, fired on press.</summary>
    public sealed record Single(MouseAction Action) : Binding;

    /// <summary>Per-direction sub-bindings for a button in gesture mode.</summary>
    public sealed record Gesture : Binding
    {
        public SortedDictionary<GestureDirection, MouseAction> Map { get; }

        public Gesture(SortedDictionary<GestureDirection, MouseAction> map) => Map = map;

        public Gesture(IEnumerable<KeyValuePair<GestureDirection, MouseAction>> entries)
            => Map = new SortedDictionary<GestureDirection, MouseAction>(entries.ToDictionary(e => e.Key, e => e.Value));

        public bool Equals(Gesture? other)
        {
            if (other is null || Map.Count != other.Map.Count) return false;
            foreach (var (k, v) in Map)
                if (!other.Map.TryGetValue(k, out var ov) || !v.Equals(ov)) return false;
            return true;
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var (k, v) in Map) { hash.Add(k); hash.Add(v); }
            return hash.ToHashCode();
        }
    }

    /// <summary>The plain-click action: the <see cref="Single"/> action, or the gesture
    /// map's <see cref="GestureDirection.Click"/> entry (falling back to <see cref="MouseAction.None"/>).</summary>
    public MouseAction ClickAction() => this switch
    {
        Single s => s.Action,
        Gesture g => g.Map.TryGetValue(GestureDirection.Click, out var a) ? a : MouseAction.None,
        _ => MouseAction.None,
    };

    /// <summary>The action bound to <paramref name="direction"/>, if this is a gesture binding.</summary>
    public MouseAction? DirectionAction(GestureDirection direction) => this switch
    {
        Gesture g => g.Map.TryGetValue(direction, out var a) ? a : null,
        _ => null,
    };

    /// <summary>Whether this binding drives raw-XY swipe capture.</summary>
    public bool IsGesture() => this is Gesture;

    /// <summary>
    /// Promote a <see cref="Single"/> to a <see cref="Gesture"/>, keeping its action as
    /// the <see cref="GestureDirection.Click"/> entry. A no-op when already a gesture.
    /// </summary>
    public Binding UpgradeToGesture() => this switch
    {
        Single s => new Gesture(new SortedDictionary<GestureDirection, MouseAction>
        {
            [GestureDirection.Click] = s.Action,
        }),
        _ => this,
    };

    /// <summary>
    /// Fill any unbound directions of a gesture binding with their canonical
    /// default, so a promoted button exposes the full five-direction set.
    /// A no-op on <see cref="Single"/>; existing user choices are preserved.
    /// </summary>
    public Binding FillGestureDefaults()
    {
        if (this is not Gesture g) return this;
        var map = new SortedDictionary<GestureDirection, MouseAction>(g.Map);
        foreach (var dir in GestureDirectionExtensions.All)
            if (!map.ContainsKey(dir))
                map[dir] = Bindings.DefaultGestureBinding(dir);
        return new Gesture(map);
    }

    public static implicit operator Binding(MouseAction action) => new Single(action);
}
