using System.Runtime.InteropServices;

namespace VoiceChanger.Audio.Interop;

/// <summary>
/// OLE32 COM base P/Invoke bindings.
/// </summary>
internal static partial class Ole32
{
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint CLSCTX_ALL = 0x17; // 1 | 2 | 4 | 16

    [LibraryImport("ole32.dll", SetLastError = true)]
    public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out nint ppv);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PROPVARIANT pvar);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);
}
