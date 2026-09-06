using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

[assembly: InternalsVisibleTo("VoiceChanger.Tests")]

namespace VoiceChanger.Audio.Interop;

#region Enumerations & Structs

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

internal enum AUDCLNT_SHAREMODE
{
    AUDCLNT_SHAREMODE_SHARED = 0,
    AUDCLNT_SHAREMODE_EXCLUSIVE = 1
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

[StructLayout(LayoutKind.Explicit)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(2)] public ushort wReserved1;
    [FieldOffset(4)] public ushort wReserved2;
    [FieldOffset(6)] public ushort wReserved3;
    [FieldOffset(8)] public nint pwszVal;
    [FieldOffset(8)] public uint uintVal;
    [FieldOffset(8)] public long hVal;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

#endregion

#region COM Interfaces

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

    [PreserveSig]
    int GetDevice(string pwstrId, out IMMDevice ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint pClient);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint dwClsCtx, nint pActivationParams, out nint ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId(out nint ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

[GeneratedComInterface]
[Guid("1BE09788-68F9-4170-BBE8-174296078026")]
internal partial interface IMMEndpoint
{
    [PreserveSig]
    int GetDataFlow(out int pDataFlow);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    [PreserveSig]
    int GetValue(in PROPERTYKEY key, out PROPVARIANT pv);

    [PreserveSig]
    int SetValue(in PROPERTYKEY key, in PROPVARIANT propvar);

    [PreserveSig]
    int Commit();
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        int shareMode,
        uint streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        nint pFormat,
        in Guid audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, nint pFormat, out nint ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out nint ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(in Guid riid, out nint ppv);
}

[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out nint ppData,
        out uint pNumFramesToRead,
        out uint pdwFlags,
        out ulong pu64DevicePosition,
        out ulong pu64QPCPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint numFramesRequested, out nint ppData);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
}

[GeneratedComInterface]
[Guid("7ED4FD07-DC52-4734-B0C5-E19E970C6332")]
internal partial interface IAudioClient3
{
    // IAudioClient methods (1-12)
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, nint pFormat, in Guid audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, nint pFormat, out nint ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out nint ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(in Guid riid, out nint ppv);

    // IAudioClient2 methods (13-15)
    [PreserveSig]
    int IsOffloadCapable(int category, out int pbOffloadCapable);

    [PreserveSig]
    int SetClientProperties(nint pProperties);

    [PreserveSig]
    int GetBufferSizeLimits(nint pFormat, int bEventDriven, out long phnsMinBufferDuration, out long phnsMaxBufferDuration);

    // IAudioClient3 methods (16-18)
    [PreserveSig]
    int GetSharedModeEnginePeriod(
        nint pFormat,
        out uint pDefaultPeriodInFrames,
        out uint pFundamentalPeriodInFrames,
        out uint pMinPeriodInFrames,
        out uint pMaxPeriodInFrames);

    [PreserveSig]
    int GetCurrentSharedModeEnginePeriod(out nint ppFormat, out uint pCurrentPeriodInFrames);

    [PreserveSig]
    int InitializeSharedAudioStream(
        uint StreamFlags,
        uint PeriodInFrames,
        nint pFormat,
        in Guid AudioSessionGuid);
}

#endregion

internal static class WasapiGuids
{
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioClient3 = new("7ED4FD07-DC52-4734-B0C5-E19E970C6332");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");

    public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
}

internal static class WasapiConstants
{
    public const uint DEVICE_STATE_ACTIVE = 0x00000001;
    public const uint STGM_READ = 0x00000000;

    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_BUFFER_SIZE_ERROR = unchecked((int)0x88890016);
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    public const uint SPEAKER_FRONT_CENTER = 0x00000004;
    public const uint SPEAKER_FRONT_LEFT = 0x00000001;
    public const ushort VT_LPWSTR = 31;
}
