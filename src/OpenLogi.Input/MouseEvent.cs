using OpenLogi.Core.Config;

namespace OpenLogi.Input;

/// <summary>Best-effort identity for the device that produced an OS event. Ported from Rust <c>EventDevice</c>.</summary>
public sealed record EventDevice(uint? VendorId = null, uint? ProductId = null, string? ProductName = null);

/// <summary>An event captured at the OS layer. Ported from Rust <c>openlogi_hook::MouseEvent</c>.</summary>
public abstract record MouseEvent
{
    private MouseEvent() { }

    /// <summary>A mouse button was pressed (<c>Pressed</c>) or released.</summary>
    public sealed record Button(ButtonId Id, bool Pressed) : MouseEvent;

    /// <summary>A scroll tick. <c>DeltaY &gt; 0</c> is scroll-up; <c>DeltaX &gt; 0</c> is right.</summary>
    public sealed record Scroll(float DeltaX, float DeltaY, bool FromTrackpad = false, EventDevice? Device = null) : MouseEvent;

    /// <summary>Pointer movement in device units (for gesture-swipe accumulation).</summary>
    public sealed record Moved(int DeltaX, int DeltaY) : MouseEvent;

    /// <summary>The OS interrupted capture; any in-progress gesture hold must be cancelled.</summary>
    public sealed record CaptureInterrupted : MouseEvent;
}

/// <summary>What the hook callback wants the OS to do with a captured event.</summary>
public enum EventDisposition
{
    /// <summary>Let the event reach its original target unchanged.</summary>
    PassThrough,
    /// <summary>Drop the event; the target application never sees it.</summary>
    Suppress,
}

/// <summary>Errors from installing or running the mouse hook.</summary>
public sealed class HookException(string message) : Exception(message);
