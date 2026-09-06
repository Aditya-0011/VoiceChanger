using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using VoiceChanger.Audio.Interop;

namespace VoiceChanger.Audio.Devices;

/// <summary>
/// Enumerates WASAPI audio capture and render endpoints.
/// </summary>
public static class AudioDeviceList
{
    /// <summary>
    /// Enumerates active audio input (microphone) devices.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() =>
        EnumerateEndpoints(EDataFlow.eCapture);

    /// <summary>
    /// Enumerates active audio output (playback / cable) devices.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices() =>
        EnumerateEndpoints(EDataFlow.eRender);

    /// <summary>
    /// Gets the default input device ID, or null if none available.
    /// </summary>
    public static string? GetDefaultCaptureDeviceId() =>
        GetDefaultEndpointId(EDataFlow.eCapture, ERole.eCommunications);

    /// <summary>
    /// Gets the default output device ID, or null if none available.
    /// </summary>
    public static string? GetDefaultRenderDeviceId() =>
        GetDefaultEndpointId(EDataFlow.eRender, ERole.eMultimedia);

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        Guid clsid = WasapiGuids.CLSID_MMDeviceEnumerator;
        Guid iid = WasapiGuids.IID_IMMDeviceEnumerator;

        int hr = Ole32.CoCreateInstance(in clsid, 0, Ole32.CLSCTX_ALL, in iid, out nint pEnum);
        if (hr != WasapiConstants.S_OK || pEnum == 0)
        {
            return null;
        }

        return ComHelper.GetOrCreateObject<IMMDeviceEnumerator>(pEnum);
    }

    private static string? GetDefaultEndpointId(EDataFlow flow, ERole role)
    {
        try
        {
            IMMDeviceEnumerator? enumerator = CreateEnumerator();
            if (enumerator == null)
            {
                return null;
            }

            int hr = enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out IMMDevice defaultDevice);
            if (hr != WasapiConstants.S_OK || defaultDevice == null)
            {
                return null;
            }

            return GetDeviceId(defaultDevice);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<AudioDeviceInfo> EnumerateEndpoints(EDataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            IMMDeviceEnumerator? enumerator = CreateEnumerator();
            if (enumerator == null)
            {
                return devices;
            }

            string? defaultId = GetDefaultEndpointId(flow, ERole.eMultimedia);

            int hr = enumerator.EnumAudioEndpoints((int)flow, WasapiConstants.DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection);
            if (hr != WasapiConstants.S_OK || collection == null)
            {
                return devices;
            }

            hr = collection.GetCount(out uint count);
            if (hr != WasapiConstants.S_OK)
            {
                return devices;
            }

            for (uint i = 0; i < count; i++)
            {
                hr = collection.Item(i, out IMMDevice device);
                if (hr != WasapiConstants.S_OK || device == null)
                {
                    continue;
                }

                string? id = GetDeviceId(device);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                string name = GetDeviceFriendlyName(device) ?? $"Audio Device ({id[..Math.Min(8, id.Length)]})";
                bool isDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);

                devices.Add(new AudioDeviceInfo(id, name, isDefault));
            }
        }
        catch
        {
            // Graceful fallback on machines without active audio devices
        }

        return devices;
    }

    private static string? GetDeviceId(IMMDevice device)
    {
        int hr = device.GetId(out nint pStr);
        if (hr != WasapiConstants.S_OK || pStr == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pStr);
        }
        finally
        {
            Ole32.CoTaskMemFree(pStr);
        }
    }

    private static string? GetDeviceFriendlyName(IMMDevice device)
    {
        int hr = device.OpenPropertyStore(WasapiConstants.STGM_READ, out IPropertyStore store);
        if (hr != WasapiConstants.S_OK || store == null)
        {
            return null;
        }

        PROPERTYKEY key = WasapiGuids.PKEY_Device_FriendlyName;
        hr = store.GetValue(in key, out PROPVARIANT pv);
        if (hr != WasapiConstants.S_OK)
        {
            return null;
        }

        try
        {
            if (pv.vt == WasapiConstants.VT_LPWSTR && pv.pwszVal != 0)
            {
                return Marshal.PtrToStringUni(pv.pwszVal);
            }
        }
        finally
        {
            Ole32.PropVariantClear(ref pv);
        }

        return null;
    }
}
