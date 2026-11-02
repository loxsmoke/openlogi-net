using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OpenLogi.Camera.Interop;

/// <summary>
/// Explicit-lifetime COM plumbing over <see cref="StrategyBasedComWrappers"/>.
/// Every wrapper is a unique instance, so <see cref="Release"/> drops exactly the
/// reference it was made for and never one another caller still holds.
/// </summary>
internal static class Com
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Wrap an owned (+1) interface pointer; the wrapper takes over that reference.</summary>
    public static T Wrap<T>(nint pointer) where T : class
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
    }

    /// <summary>
    /// An owned (+1) pointer to <paramref name="iid"/> on the object behind
    /// <paramref name="wrapper"/>, for passing to a native call. Release it after.
    /// </summary>
    public static nint Pointer(object wrapper, in Guid iid)
    {
        if (!ComWrappers.TryGetComInstance(wrapper, out var unknown))
            throw new InvalidOperationException("not a COM wrapper");
        try
        {
            Check(Marshal.QueryInterface(unknown, iid, out var p), "QueryInterface");
            return p;
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>An owned (+1) pointer to <paramref name="iid"/> on a managed [GeneratedComClass] object. Release it after.</summary>
    public static nint PointerFor(object managed, in Guid iid)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(managed, CreateComInterfaceFlags.None);
        try
        {
            Check(Marshal.QueryInterface(unknown, iid, out var p), "QueryInterface");
            return p;
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>Drop the wrapper's reference now rather than at finalization.</summary>
    public static void Release(object? wrapper)
    {
        if (wrapper is ComObject c) c.FinalRelease();
    }

    public static bool Failed(int hr) => hr < 0;

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    /// <summary>Read and free a CoTaskMem string attribute.</summary>
    public static string? AllocatedString(IMFAttributes attributes, in Guid key)
    {
        if (Failed(attributes.GetAllocatedString(key, out var p, out _)) || p == 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static IMFAttributes CreateAttributes(uint size)
    {
        Check(MfNative.MFCreateAttributes(out var p, size), "MFCreateAttributes");
        return Wrap<IMFAttributes>(p);
    }
}
