using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using VoiceChanger.Audio.Interop;
using VoiceChanger.Audio.Pipeline;

namespace VoiceChanger.Audio.Streams;

/// <summary>
/// WASAPI event-driven capture stream in shared mode, configured for 48 kHz mono float32.
/// MMCSS "Pro Audio" applied to the dedicated capture thread.
/// </summary>
public sealed class WasapiCaptureStream : IDisposable
{
    private readonly string? _deviceId;
    private readonly ProcessingPipeline _pipeline;
    private Thread? _thread;
    private nint _hEvent;
    private volatile bool _isStopping;
    private bool _disposed;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;

    /// <summary>
    /// Event fired when the audio device is disconnected or invalidated by Windows.
    /// </summary>
    public event Action? DeviceInvalidated;

    /// <summary>
    /// Latency in frames reported by the audio client.
    /// </summary>
    public int LatencyFrames { get; private set; }

    /// <summary>
    /// Stream latency in milliseconds.
    /// </summary>
    public double LatencyMs { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="WasapiCaptureStream"/>.
    /// </summary>
    /// <param name="deviceId">Device ID to capture from, or null for default communications microphone.</param>
    /// <param name="pipeline">Processing pipeline receiving audio samples.</param>
    public WasapiCaptureStream(string? deviceId, ProcessingPipeline pipeline)
    {
        _deviceId = deviceId;
        _pipeline = pipeline;
    }

    /// <summary>
    /// Initializes WASAPI COM objects, audio client, and creates event handle.
    /// </summary>
    public void Initialize()
    {
        IMMDevice? device = GetCaptureDevice(_deviceId);
        if (device == null)
        {
            throw new InvalidOperationException("Failed to locate WASAPI capture endpoint.");
        }

        Guid iidAudioClient = WasapiGuids.IID_IAudioClient;
        int hr = device.Activate(in iidAudioClient, Ole32.CLSCTX_ALL, 0, out nint pAudioClient);
        if (hr != WasapiConstants.S_OK || pAudioClient == 0)
        {
            throw new COMException("Failed to activate IAudioClient on capture device.", hr);
        }

        _audioClient = ComHelper.GetOrCreateObject<IAudioClient>(pAudioClient);

        _hEvent = Kernel32.CreateEventW(0, false, false, null);
        if (_hEvent == 0)
        {
            throw new InvalidOperationException("Failed to create Win32 event for capture stream.");
        }

        // 48 kHz, 1 channel, 32-bit IEEE Float format
        WAVEFORMATEXTENSIBLE wfe = new();
        wfe.Format.wFormatTag = WasapiConstants.WAVE_FORMAT_EXTENSIBLE;
        wfe.Format.nChannels = 1;
        wfe.Format.nSamplesPerSec = 48000;
        wfe.Format.wBitsPerSample = 32;
        wfe.Format.nBlockAlign = 4;
        wfe.Format.nAvgBytesPerSec = 192000;
        wfe.Format.cbSize = 22;
        wfe.wValidBitsPerSample = 32;
        wfe.dwChannelMask = WasapiConstants.SPEAKER_FRONT_CENTER;
        wfe.SubFormat = WasapiGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

        unsafe
        {
            uint streamFlags = WasapiConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                               WasapiConstants.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                               WasapiConstants.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            // 10 ms requested buffer duration (100,000 hns)
            long hnsBufferDuration = 100_000;

            hr = _audioClient.Initialize(
                (int)AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                streamFlags,
                hnsBufferDuration,
                0,
                (nint)(&wfe),
                Guid.Empty);

            if (hr != WasapiConstants.S_OK)
            {
                // Fallback: try mix format with auto-convert
                hr = _audioClient.GetMixFormat(out nint pMixFormat);
                if (hr == WasapiConstants.S_OK && pMixFormat != 0)
                {
                    try
                    {
                        hr = _audioClient.Initialize(
                            (int)AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                            streamFlags,
                            hnsBufferDuration,
                            0,
                            pMixFormat,
                            Guid.Empty);
                    }
                    finally
                    {
                        Ole32.CoTaskMemFree(pMixFormat);
                    }
                }

                if (hr != WasapiConstants.S_OK)
                {
                    string detail = hr == unchecked((int)0x8889000A)
                        ? "Device is in exclusive use by another app or requires a system reboot after driver installation."
                        : $"HRESULT 0x{hr:X8}";
                    throw new COMException($"Failed to initialize WASAPI capture stream: {detail}", hr);
                }
            }
        }

        hr = _audioClient.SetEventHandle(_hEvent);
        if (hr != WasapiConstants.S_OK)
        {
            throw new COMException("Failed to assign event handle to capture audio client.", hr);
        }

        Guid iidCaptureClient = WasapiGuids.IID_IAudioCaptureClient;
        hr = _audioClient.GetService(in iidCaptureClient, out nint pCaptureClient);
        if (hr != WasapiConstants.S_OK || pCaptureClient == 0)
        {
            throw new COMException("Failed to obtain IAudioCaptureClient service.", hr);
        }

        _captureClient = ComHelper.GetOrCreateObject<IAudioCaptureClient>(pCaptureClient);

        int hrLatency = _audioClient.GetStreamLatency(out long phnsLatency);
        int hrBuffer = _audioClient.GetBufferSize(out uint bufferFrames);
        int hrPeriod = _audioClient.GetDevicePeriod(out long defPeriod, out long minPeriod);

        // For event-driven capture in shared mode, WASAPI delivers packets every engine period.
        // GetStreamLatency is not supported for capture streams (E_NOTIMPL / returns 0).
        // The actual capture latency is the engine period (defPeriod), NOT the entire hardware circular buffer (bufferFrames).
        if (hrLatency == WasapiConstants.S_OK && phnsLatency > 0)
        {
            LatencyMs = phnsLatency / 10000.0;
            LatencyFrames = (int)(LatencyMs * 48);
        }
        else if (hrPeriod == WasapiConstants.S_OK && defPeriod > 0)
        {
            LatencyMs = defPeriod / 10000.0;
            LatencyFrames = (int)(LatencyMs * 48);
        }
        else if (hrBuffer == WasapiConstants.S_OK && bufferFrames > 0)
        {
            LatencyFrames = (int)bufferFrames;
            LatencyMs = LatencyFrames / 48.0;
        }
        else
        {
            LatencyMs = 10.0;
            LatencyFrames = 480;
        }
    }

    /// <summary>
    /// Starts the capture stream and worker thread.
    /// </summary>
    public void Start()
    {
        if (_audioClient == null)
        {
            throw new InvalidOperationException("Stream is not initialized.");
        }

        _isStopping = false;
        int hr = _audioClient.Start();
        if (hr != WasapiConstants.S_OK)
        {
            throw new COMException("Failed to start WASAPI capture stream.", hr);
        }

        _thread = new Thread(CaptureLoop)
        {
            Name = "VoiceChanger.WasapiCapture",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    /// <summary>
    /// Stops the capture stream and awaits thread exit.
    /// </summary>
    public void Stop()
    {
        _isStopping = true;
        if (_hEvent != 0)
        {
            Kernel32.SetEvent(_hEvent);
        }

        _thread?.Join(1000);
        _thread = null;

        try
        {
            _audioClient?.Stop();
        }
        catch
        {
            // Ignore shutdown stop exceptions
        }
    }

    private void CaptureLoop()
    {
        Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);

        // Apply MMCSS "Pro Audio" per Project.md Phase 0 requirement
        uint taskIndex = 0;
        nint avrtHandle = Avrt.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);

        try
        {
            while (!_isStopping)
            {
                uint waitResult = Kernel32.WaitForSingleObject(_hEvent, 100);
                if (_isStopping)
                {
                    break;
                }

                if (waitResult == Kernel32.WAIT_OBJECT_0)
                {
                    ProcessPackets();
                }
            }
        }
        finally
        {
            if (avrtHandle != 0)
            {
                Avrt.AvRevertMmThreadCharacteristics(avrtHandle);
            }

            Ole32.CoUninitialize();
        }
    }

    private unsafe void ProcessPackets()
    {
        if (_captureClient == null)
        {
            return;
        }

        float* silenceBuffer = stackalloc float[512];
        new Span<float>(silenceBuffer, 512).Clear();

        while (!_isStopping)
        {
            int hr = _captureClient.GetNextPacketSize(out uint packetLength);
            if (hr == WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED)
            {
                _isStopping = true;
                DeviceInvalidated?.Invoke();
                return;
            }

            if (hr != WasapiConstants.S_OK || packetLength == 0)
            {
                break;
            }

            hr = _captureClient.GetBuffer(
                out nint pData,
                out uint numFramesToRead,
                out uint flags,
                out _,
                out _);

            if (hr == WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED)
            {
                _isStopping = true;
                DeviceInvalidated?.Invoke();
                return;
            }

            if (hr == WasapiConstants.S_OK && pData != 0 && numFramesToRead > 0)
            {
                if ((flags & WasapiConstants.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                {
                    // Silent buffer: fill zeroes
                    int remaining = (int)numFramesToRead;
                    while (remaining > 0)
                    {
                        int chunk = Math.Min(remaining, 512);
                        var slice = new ReadOnlySpan<float>(silenceBuffer, chunk);
                        if (!_pipeline.CaptureRing.Write(slice))
                        {
                            _pipeline.RecordOverrun(chunk);
                        }
                        remaining -= chunk;
                    }
                }
                else
                {
                    var span = new ReadOnlySpan<float>((void*)pData, (int)numFramesToRead);
                    if (!_pipeline.CaptureRing.Write(span))
                    {
                        _pipeline.RecordOverrun(span.Length);
                    }
                }

                _captureClient.ReleaseBuffer(numFramesToRead);
            }
            else
            {
                break;
            }
        }

        _pipeline.NotifyCaptureDataAvailable();
    }

    private static IMMDevice? GetCaptureDevice(string? deviceId)
    {
        Guid clsid = WasapiGuids.CLSID_MMDeviceEnumerator;
        Guid iid = WasapiGuids.IID_IMMDeviceEnumerator;

        int hr = Ole32.CoCreateInstance(in clsid, 0, Ole32.CLSCTX_ALL, in iid, out nint pEnum);
        if (hr != WasapiConstants.S_OK || pEnum == 0)
        {
            return null;
        }

        var enumerator = ComHelper.GetOrCreateObject<IMMDeviceEnumerator>(pEnum);

        if (!string.IsNullOrEmpty(deviceId))
        {
            hr = enumerator.GetDevice(deviceId, out IMMDevice specificDevice);
            if (hr == WasapiConstants.S_OK && specificDevice != null)
            {
                return specificDevice;
            }
        }

        hr = enumerator.GetDefaultAudioEndpoint(
            (int)EDataFlow.eCapture, (int)ERole.eCommunications, out IMMDevice defaultDevice);

        return hr == WasapiConstants.S_OK ? defaultDevice : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        if (_hEvent != 0)
        {
            Kernel32.CloseHandle(_hEvent);
            _hEvent = 0;
        }

        _captureClient = null;
        _audioClient = null;
    }
}
