using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OpenLogi.Camera.Interop;

// Media Foundation and the two DirectShow control interfaces a capture source
// implements, declared for source-generated COM. Every method keeps its HRESULT
// ([PreserveSig]) and every interface pointer crosses as a raw nint, so the
// wrappers' lifetimes stay explicit (see Com.Wrap / Com.Release): the source
// reader recycles samples, and a cached wrapper over a recycled pointer would
// outlive the reference it was made for.

[GeneratedComInterface]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal partial interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, nint value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, nint value, out int result);
    [PreserveSig] int Compare(nint theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, nint buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out nint value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, nint buffer, uint size, out uint written);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out nint buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid iid, out nint value);
    [PreserveSig] int SetItem(in Guid key, nint value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, nint value);
    [PreserveSig] int SetBlob(in Guid key, nint buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, nint value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
    [PreserveSig] int CopyAllItems(nint dest);
}

[GeneratedComInterface]
[Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67")]
internal partial interface IMFActivate : IMFAttributes
{
    [PreserveSig] int ActivateObject(in Guid iid, out nint obj);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[GeneratedComInterface]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
internal partial interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid major);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(nint other, out uint flags);
    [PreserveSig] int GetRepresentation(Guid representation, out nint value);
    [PreserveSig] int FreeRepresentation(Guid representation, nint value);
}

[GeneratedComInterface]
[Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D")]
internal partial interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, out nint mediaEvent);
    [PreserveSig] int BeginGetEvent(nint callback, nint state);
    [PreserveSig] int EndGetEvent(nint result, out nint mediaEvent);
    [PreserveSig] int QueueEvent(uint type, in Guid extendedType, int status, nint value);
}

[GeneratedComInterface]
[Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66")]
internal partial interface IMFMediaSource : IMFMediaEventGenerator
{
    [PreserveSig] int GetCharacteristics(out uint characteristics);
    [PreserveSig] int CreatePresentationDescriptor(out nint descriptor);
    [PreserveSig] int Start(nint descriptor, nint timeFormat, nint startPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[GeneratedComInterface]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
internal partial interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint stream, out int selected);
    [PreserveSig] int SetStreamSelection(uint stream, int selected);
    [PreserveSig] int GetNativeMediaType(uint stream, uint index, out nint mediaType);
    [PreserveSig] int GetCurrentMediaType(uint stream, out nint mediaType);
    [PreserveSig] int SetCurrentMediaType(uint stream, nint reserved, nint mediaType);
    [PreserveSig] int SetCurrentPosition(in Guid timeFormat, nint position);
    [PreserveSig] int ReadSample(uint stream, uint controlFlags, out uint actualStream, out uint flags, out long timestamp, out nint sample);
    [PreserveSig] int Flush(uint stream);
    [PreserveSig] int GetServiceForStream(uint stream, in Guid service, in Guid iid, out nint obj);
    [PreserveSig] int GetPresentationAttribute(uint stream, in Guid attribute, nint value);
}

[GeneratedComInterface]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
internal partial interface IMFSample : IMFAttributes
{
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out nint buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out nint buffer);
    [PreserveSig] int AddBuffer(nint buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(nint buffer);
}

[GeneratedComInterface]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
internal partial interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out nint buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

/// <summary>DirectShow lens controls (zoom/pan/tilt/focus/exposure), implemented by a capture source.</summary>
[GeneratedComInterface]
[Guid("C6E13370-30AC-11d0-A18C-00A0C9118956")]
internal partial interface IAMCameraControl
{
    [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);
    [PreserveSig] int Set(int property, int value, int flags);
    [PreserveSig] int Get(int property, out int value, out int flags);
}

/// <summary>DirectShow image controls (brightness/contrast/…), implemented by a capture source.</summary>
[GeneratedComInterface]
[Guid("C6E13360-30AC-11d0-A18C-00A0C9118956")]
internal partial interface IAMVideoProcAmp
{
    [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);
    [PreserveSig] int Set(int property, int value, int flags);
    [PreserveSig] int Get(int property, out int value, out int flags);
}

internal static partial class MfNative
{
    private const uint MfVersion = 0x0002_0070;
    private const uint MfStartupFull = 0;

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out nint mediaType);

    [LibraryImport("mf.dll")]
    public static partial int MFEnumDeviceSources(nint attributes, out nint activates, out uint count);

    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSourceReaderFromMediaSource(nint source, nint attributes, out nint reader);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    public const uint CoInitMultithreaded = 0;

    private static readonly Lock StartupGate = new();
    private static bool _started;

    /// <summary>
    /// Start Media Foundation once for the process. MFStartup is reference-counted
    /// and process-wide; it is never shut down, as the app keeps scanning cameras
    /// for its lifetime.
    /// </summary>
    public static void EnsureStarted()
    {
        lock (StartupGate)
        {
            if (_started) return;
            Com.Check(MFStartup(MfVersion, MfStartupFull), "MFStartup");
            _started = true;
        }
    }
}

/// <summary>Media Foundation GUIDs and constants used here.</summary>
internal static class Mf
{
    public static readonly Guid DevSourceAttributeSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static readonly Guid DevSourceAttributeSourceTypeVidcapGuid = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static readonly Guid DevSourceAttributeSourceTypeVidcapSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    public static readonly Guid DevSourceAttributeFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    /// <summary>
    /// MF_DEVSOURCE_ATTRIBUTE_FRAMESERVER_SHARE_MODE (Windows 11 24H2+): 1 opens the
    /// camera in sharing mode, so another app can hold it in controlling mode at the
    /// same time. Older Windows ignores the attribute and opens in controlling mode.
    /// </summary>
    public static readonly Guid DevSourceAttributeFrameServerShareMode = new("44d1a9bc-2999-4238-ae43-0730ceb2ab1b");
    public static readonly Guid SourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid VideoFormatRgb24 = new("00000014-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatYuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    public static readonly Guid MtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");

    public static readonly Guid IidMfAttributes = typeof(IMFAttributes).GUID;
    public static readonly Guid IidMfMediaSource = typeof(IMFMediaSource).GUID;
    public static readonly Guid IidMfMediaType = typeof(IMFMediaType).GUID;

    /// <summary>MF_SOURCE_READER_FIRST_VIDEO_STREAM.</summary>
    public const uint FirstVideoStream = 0xFFFF_FFFC;
    public const uint ReaderFlagError = 0x1;
    public const uint ReaderFlagEndOfStream = 0x2;
    public const uint ReaderFlagNativeMediaTypeChanged = 0x10;
    public const uint ReaderFlagCurrentMediaTypeChanged = 0x20;

    public const int EAccessDenied = unchecked((int)0x8007_0005);
    public const int HwMftFailedStartStreaming = unchecked((int)0xC00D_3704);
    /// <summary>HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION): another controlling client holds the camera.</summary>
    public const int ESharingViolation = unchecked((int)0x8007_0020);
    /// <summary>MF_E_VIDEO_RECORDING_DEVICE_LOCKED.</summary>
    public const int VideoRecordingDeviceLocked = unchecked((int)0xC00D_3E85);
    /// <summary>MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED.</summary>
    public const int VideoRecordingDevicePreempted = unchecked((int)0xC00D_3E86);
}
