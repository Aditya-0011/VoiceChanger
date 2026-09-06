using VoiceChanger.Audio.Devices;
using VoiceChanger.Audio.Interop;
using VoiceChanger.Audio.Pipeline;
using VoiceChanger.Audio.Streams;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class AudioInteropSmokeTests
{
    [Fact]
    public void PassthroughBridge_WriteAndRead_TransfersDataAccurately()
    {
        var bridge = new PassthroughBridge(capacity: 1024);

        float[] writeData = new float[256];
        for (int i = 0; i < writeData.Length; i++)
        {
            writeData[i] = i * 0.1f;
        }

        bridge.Write(writeData);
        Assert.Equal(256, bridge.BufferedFrames);

        float[] readData = new float[256];
        int readCount = bridge.Read(readData);

        Assert.Equal(256, readCount);
        Assert.Equal(writeData, readData);
        Assert.Equal(0, bridge.BufferedFrames);
    }

    [Fact]
    public void PassthroughBridge_Underrun_ZeroFillsOutput()
    {
        var bridge = new PassthroughBridge(capacity: 512);

        float[] writeData = [1.0f, 2.0f, 3.0f];
        bridge.Write(writeData);

        float[] readData = new float[5];
        Array.Fill(readData, -999.0f);

        int readCount = bridge.Read(readData);

        Assert.Equal(3, readCount);
        Assert.Equal(1.0f, readData[0]);
        Assert.Equal(2.0f, readData[1]);
        Assert.Equal(3.0f, readData[2]);
        Assert.Equal(0.0f, readData[3]); // padded with silence
        Assert.Equal(0.0f, readData[4]); // padded with silence
        Assert.True(bridge.UnderrunFrames > 0);
    }

    [Fact]
    public void PassthroughBridge_Wraparound_MaintainsOrder()
    {
        var bridge = new PassthroughBridge(capacity: 64);

        float[] block = new float[40];
        for (int i = 0; i < block.Length; i++) block[i] = i;

        // First write: fill 40
        bridge.Write(block);

        // Read 30 (read pointer at 30, write pointer at 40)
        float[] out30 = new float[30];
        bridge.Read(out30);

        // Second write: write 40 (will wrap around 64: 40 + 40 = 80 > 64)
        for (int i = 0; i < block.Length; i++) block[i] = 100 + i;
        bridge.Write(block);

        // Read remaining 50 frames (10 old + 40 new)
        float[] out50 = new float[50];
        int read = bridge.Read(out50);

        Assert.Equal(50, read);
        // Check old frames (indices 30..39 of first block)
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(30 + i, out50[i]);
        }
        // Check new frames (indices 0..39 of second block)
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(100 + i, out50[10 + i]);
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public AudioInteropSmokeTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DeviceList_DoesNotThrow()
    {
        var captureDevices = AudioDeviceList.GetCaptureDevices();
        var renderDevices = AudioDeviceList.GetRenderDevices();

        _output.WriteLine($"Found {captureDevices.Count} capture devices:");
        foreach (var dev in captureDevices)
        {
            _output.WriteLine($"  - [Capture] {dev.Name} (Default: {dev.IsDefault}, ID: {dev.Id})");
        }

        _output.WriteLine($"Found {renderDevices.Count} render devices:");
        foreach (var dev in renderDevices)
        {
            _output.WriteLine($"  - [Render] {dev.Name} (Default: {dev.IsDefault}, ID: {dev.Id})");
        }

        Assert.NotNull(captureDevices);
        Assert.NotNull(renderDevices);
        Assert.NotEmpty(captureDevices);
        Assert.NotEmpty(renderDevices);
    }

    [Fact]
    public void WasapiStreams_HardwareSmokeTest()
    {
        var captures = AudioDeviceList.GetCaptureDevices();
        var renders = AudioDeviceList.GetRenderDevices();

        if (captures.Count == 0 || renders.Count == 0)
        {
            return;
        }

        using var pipeline = new ProcessingPipeline(new Core.PassthroughProcessor(), ringCapacity: 1024);
        using var capture = new WasapiCaptureStream(null, pipeline);
        capture.Initialize();

        using var render = new WasapiRenderStream(null, pipeline);
        render.Initialize();

        _output.WriteLine($"Capture LatencyMs: {capture.LatencyMs}, Frames: {capture.LatencyFrames}");
        _output.WriteLine($"Render LatencyMs: {render.LatencyMs}, Frames: {render.LatencyFrames}");

        Assert.True(capture.LatencyMs > 0);
        Assert.True(render.LatencyMs > 0);
    }

    [Fact]
    public void TestAudioClient_Parameters()
    {
        var captures = AudioDeviceList.GetCaptureDevices();
        var renders = AudioDeviceList.GetRenderDevices();

        if (captures.Count == 0 || renders.Count == 0)
        {
            return;
        }

        _output.WriteLine("=== Testing IAudioClient on Capture Endpoints ===");
        foreach (var cap in captures)
        {
            TestDeviceClient(cap.Id, cap.Name, isCapture: true);
        }

        _output.WriteLine("=== Testing IAudioClient on Render Endpoints ===");
        foreach (var ren in renders)
        {
            TestDeviceClient(ren.Id, ren.Name, isCapture: false);
        }
    }

    private unsafe void TestDeviceClient(string id, string name, bool isCapture)
    {
        Guid clsid = WasapiGuids.CLSID_MMDeviceEnumerator;
        Guid iidEnum = WasapiGuids.IID_IMMDeviceEnumerator;
        Ole32.CoCreateInstance(in clsid, 0, Ole32.CLSCTX_ALL, in iidEnum, out nint pEnum);
        var enumerator = ComHelper.GetOrCreateObject<IMMDeviceEnumerator>(pEnum);
        enumerator.GetDevice(id, out IMMDevice device);

        Guid iidClient = WasapiGuids.IID_IAudioClient;
        int hr = device.Activate(in iidClient, Ole32.CLSCTX_ALL, 0, out nint pClient);
        if (hr != WasapiConstants.S_OK || pClient == 0) return;

        var client = ComHelper.GetOrCreateObject<IAudioClient>(pClient);

        int hrPeriod = client.GetDevicePeriod(out long defPeriod, out long minPeriod);

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

        uint streamFlags = WasapiConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                           WasapiConstants.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                           WasapiConstants.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        int hrInit = client.Initialize(
            (int)AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
            streamFlags,
            0,
            0,
            (nint)(&wfe),
            Guid.Empty);

        client.GetBufferSize(out uint bufFrames);
        client.GetStreamLatency(out long latencyHns);

        _output.WriteLine($"[{name}]");
        _output.WriteLine($"  DevicePeriod: def={defPeriod / 10000.0}ms ({defPeriod * 48 / 10000} frames), min={minPeriod / 10000.0}ms ({minPeriod * 48 / 10000} frames)");
        _output.WriteLine($"  Init: HR=0x{hrInit:X8}, BufferFrames={bufFrames} ({bufFrames / 48.0:F2}ms), StreamLatency={latencyHns / 10000.0}ms ({latencyHns * 48 / 10000} frames)");
    }
}
