using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using VoiceChanger.Audio.Interop;
using VoiceChanger.Audio.Pipeline;

namespace VoiceChanger.Audio.Streams;

/// <summary>
/// WASAPI event-driven render stream in shared mode, writing to user-selected endpoint (e.g. VB-CABLE).
/// MMCSS "Pro Audio" applied to the dedicated render thread.
/// </summary>
public sealed class WasapiRenderStream : IDisposable
{
    private readonly string? _deviceId;
    private readonly ProcessingPipeline _pipeline;
    private Thread? _thread;
    private nint _hEvent;
    private volatile bool _isStopping;
    private bool _disposed;
    private uint _bufferFrameCount;

    private IAudioClient? _audioClient;
    private IAudioRenderClient? _renderClient;

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
    /// Initializes a new instance of <see cref="WasapiRenderStream"/>.
    /// </summary>
    /// <param name="deviceId">Target render device ID, or null for default multimedia output.</param>
    /// <param name="pipeline">Processing pipeline providing audio samples.</param>
    public WasapiRenderStream(string? deviceId, ProcessingPipeline pipeline)
    {
        _deviceId = deviceId;
        _pipeline = pipeline;
    }

    /// <summary>
    /// Initializes WASAPI COM objects, audio client, and creates event handle.
    /// </summary>
    public void Initialize()
    {
        IMMDevice? device = GetRenderDevice(_deviceId);
        if (device == null)
        {
            throw new InvalidOperationException("Failed to locate WASAPI render endpoint.");
        }

        Guid iidAudioClient = WasapiGuids.IID_IAudioClient;
        int hr = device.Activate(in iidAudioClient, Ole32.CLSCTX_ALL, 0, out nint pAudioClient);
        if (hr != WasapiConstants.S_OK || pAudioClient == 0)
        {
            throw new COMException("Failed to activate IAudioClient on render device.", hr);
        }

        _audioClient = ComHelper.GetOrCreateObject<IAudioClient>(pAudioClient);

        _hEvent = Kernel32.CreateEventW(0, false, false, null);
        if (_hEvent == 0)
        {
            throw new InvalidOperationException("Failed to create Win32 event for render stream.");
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
                // Fallback: mix format with auto-convert
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
                    throw new COMException($"Failed to initialize WASAPI render stream: {detail}", hr);
                }
            }
        }

        hr = _audioClient.SetEventHandle(_hEvent);
        if (hr != WasapiConstants.S_OK)
        {
            throw new COMException("Failed to assign event handle to render audio client.", hr);
        }

        Guid iidRenderClient = WasapiGuids.IID_IAudioRenderClient;
        hr = _audioClient.GetService(in iidRenderClient, out nint pRenderClient);
        if (hr != WasapiConstants.S_OK || pRenderClient == 0)
        {
            throw new COMException("Failed to obtain IAudioRenderClient service.", hr);
        }

        _renderClient = ComHelper.GetOrCreateObject<IAudioRenderClient>(pRenderClient);

        hr = _audioClient.GetBufferSize(out _bufferFrameCount);
        if (hr != WasapiConstants.S_OK)
        {
            _bufferFrameCount = 480; // default 10 ms
        }

        int hrLatency = _audioClient.GetStreamLatency(out long phnsLatency);
        int hrPeriod = _audioClient.GetDevicePeriod(out long defPeriod, out _);

        // For event-driven render in shared mode, Microsoft WASAPI documentation specifies stream latency
        // as the sum of the engine period and the driver processing pass (2x engine period = 20ms).
        // GetStreamLatency returns 0 in shared mode.
        if (hrLatency == WasapiConstants.S_OK && phnsLatency > 0)
        {
            LatencyMs = phnsLatency / 10000.0;
            LatencyFrames = (int)(LatencyMs * 48);
        }
        else if (hrPeriod == WasapiConstants.S_OK && defPeriod > 0)
        {
            LatencyMs = (defPeriod * 2) / 10000.0;
            LatencyFrames = (int)(LatencyMs * 48);
        }
        else if (_bufferFrameCount > 0)
        {
            LatencyFrames = (int)_bufferFrameCount;
            LatencyMs = LatencyFrames / 48.0;
        }
        else
        {
            LatencyMs = 20.0;
            LatencyFrames = 960;
        }

        // Pre-roll the render buffer with silence so the engine starts cleanly
        PreRollBuffer();
    }

    private unsafe void PreRollBuffer()
    {
        if (_renderClient == null || _bufferFrameCount == 0)
        {
            return;
        }

        int hr = _renderClient.GetBuffer(_bufferFrameCount, out nint pData);
        if (hr == WasapiConstants.S_OK && pData != 0)
        {
            new Span<float>((void*)pData, (int)_bufferFrameCount).Clear();
            _renderClient.ReleaseBuffer(_bufferFrameCount, WasapiConstants.AUDCLNT_BUFFERFLAGS_SILENT);
        }
    }

    /// <summary>
    /// Starts the render stream and worker thread.
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
            throw new COMException("Failed to start WASAPI render stream.", hr);
        }

        _thread = new Thread(RenderLoop)
        {
            Name = "VoiceChanger.WasapiRender",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    /// <summary>
    /// Stops the render stream and awaits thread exit.
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

    private void RenderLoop()
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
                    FeedRenderBuffer();
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

    private unsafe void FeedRenderBuffer()
    {
        if (_audioClient == null || _renderClient == null)
        {
            return;
        }

        int hr = _audioClient.GetCurrentPadding(out uint paddingFrames);
        if (hr == WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED)
        {
            _isStopping = true;
            DeviceInvalidated?.Invoke();
            return;
        }

        if (hr != WasapiConstants.S_OK)
        {
            return;
        }

        uint framesNeeded = _bufferFrameCount > paddingFrames ? _bufferFrameCount - paddingFrames : 0;
        if (framesNeeded == 0)
        {
            return;
        }

        hr = _renderClient.GetBuffer(framesNeeded, out nint pData);
        if (hr == WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED)
        {
            _isStopping = true;
            DeviceInvalidated?.Invoke();
            return;
        }

        if (hr == WasapiConstants.S_OK && pData != 0)
        {
            var outputSpan = new Span<float>((void*)pData, (int)framesNeeded);
            int read = _pipeline.RenderRing.Read(outputSpan);

            if (read < (int)framesNeeded)
            {
                // Underflow: pad remainder with silence and record metric
                outputSpan.Slice(read).Clear();
                _pipeline.RecordUnderrun((int)framesNeeded - read);
            }

            uint flags = read == 0 ? WasapiConstants.AUDCLNT_BUFFERFLAGS_SILENT : 0;
            _renderClient.ReleaseBuffer(framesNeeded, flags);
        }
    }

    private static IMMDevice? GetRenderDevice(string? deviceId)
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
            (int)EDataFlow.eRender, (int)ERole.eMultimedia, out IMMDevice defaultDevice);

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

        _renderClient = null;
        _audioClient = null;
    }
}
