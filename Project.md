# Real-Time Voice Changer — Engineering Specification

> **Audience:** coding agents working on this repository.Read this file fully before writing code. It encodes decisions already made and traps already identified. Do not re-litigate the architecture; if you believe a decision is wrong, say so explicitly and wait rather than silently doing something else.

---

## 1. What this is

A Windows desktop application that captures microphone input, transforms the voice in real time, and routes the result into a virtual audio device so that other applications (Discord, games) receive the transformed voice as if it were a normal microphone.

Two processing tiers:
- DSP tier — pitch and formant shifting via phase vocoder. Target latency budget: < 30 ms round trip. Runs on CPU. This is the primary tier and must work standalone.
- Neural tier — RVC-style voice conversion via ONNX Runtime. Target: < 300 ms. Optional, loaded on demand, must be swappable behind the same interface as the DSP tier.

The project has two goals with different priorities. It must be usable (the author will actually use it while gaming) and it must be demonstrable (it is a portfolio piece). Where these conflict, favour code quality and measurability — a slightly less convenient feature that is well-engineered and instrumented is preferred over a hack that works.

---

## 2. Target environment

| | |
|---|---|
| OS | Windows 11 Pro |
| Runtime | .NET 10 (LTS) |
| CPU | Intel Core Ultra 7 155H (Meteor Lake — P-cores, E-cores, LP E-cores, plus an NPU) |
| GPU | NVIDIA RTX 4060 Laptop + Intel Arc iGPU |
| RAM | 32 GB DDR5 |
| Audio | Wired headset mic in, VB-CABLE out |

Windows-only is an accepted constraint. Use platform APIs freely. Do not add abstraction layers for hypothetical cross-platform support.

On .NET 10 specifically: the baseline requirements below (Span<T>, System.Runtime.Intrinsics, LibraryImport, GeneratedComInterface, source-generated JSON) all predate .NET 10 and are safe to rely on. If you want to use an API you believe is new in .NET 9 or 10, verify it against current documentation before using it and note the verification in your commit message. Do not assume a feature exists because it sounds like something that should.

---

## 3. Hard invariants

Violating any of these is a bug regardless of whether the code appears to work.

1. Zero heap allocation inside the audio callback path. No new, no LINQ, no closures that capture, no boxing, no string formatting, no logging that allocates. Every buffer is pre-allocated at startup. If you need scratch space, use stackalloc with a compile-time-bounded size, or a pre-allocated pool.
2. No inference, file I/O, locking, or unbounded work on the audio thread. The capture and render callbacks touch ring buffers and nothing else. All processing happens on a dedicated worker thread.
3. VoiceChanger.Core has no dependency on NAudio, WASAPI, ONNX, or any UI framework. It is pure managed DSP over spans. This is what makes it unit-testable without hardware. Enforce with a test that asserts the assembly's referenced assemblies.
4. No copyrighted or third-party-trained voice models are committed to this repository, and none are referenced by URL in a way that constitutes distribution. The README documents how a user imports their own model. Sample/demo material uses either self-recorded audio or a permissively licensed dataset (VCTK — CC BY 4.0, LibriTTS, Common Voice — CC0).
5. Latency is reported as a distribution, never as a mean. Every benchmark reports p50, p95, p99, and max. A pipeline with an 80 ms mean and a 400 ms p99 is a broken pipeline; a mean alone hides that.
6. Every DSP component is testable with synthetic input. No component may require a live audio device to verify its correctness.

---

## 4. Solution layout

```
VoiceChanger.Core/       DSP, ring buffer, IAudioProcessor. No I/O, no external deps.
VoiceChanger.Audio/      WASAPI capture/render, device enumeration, MMCSS, clock drift.
VoiceChanger.Neural/     ONNX Runtime pipeline. Referenced lazily; app must run without it.
VoiceChanger.App/        UI, tray icon, hotkeys, presets, config.
VoiceChanger.Tests/      Unit tests. Must not require an audio device.
VoiceChanger.Bench/      BenchmarkDotNet harness + latency histogram tooling.
```

The central abstraction:

```c#
public interface IAudioProcessor
{
    int LatencyFrames { get; }        // algorithmic delay this processor introduces
    void Prepare(int sampleRate, int maxBlockSize);
    void Process(ReadOnlySpan<float> input, Span<float> output);
    void Reset();
}
```

PassthroughProcessor, PhaseVocoderProcessor, and RvcProcessor all implement this. The app should be able to hot-swap the active processor without restarting the audio stream. Note that Process may be called with a different block size than the audio callback's — the worker thread decides its own chunking.

---

## 5. Build order

Do not skip ahead. Each phase has acceptance criteria that must pass before the next begins.

### Phase 0 — Passthrough

Capture from the default mic, write unmodified to the selected output device.
- WASAPI shared mode, event-driven (not polling), 48 kHz, mono, float32.
- Enumerate devices via MMDeviceEnumerator; the output target must be user-selectable so VB-CABLE can be chosen.
- Call AvSetMmThreadCharacteristics("Pro Audio", ...) on the audio thread. This is not optional — on the 155H, Windows will otherwise park the audio thread on an efficiency core and produce intermittent crackle. Store the returned handle and call AvRevertMmThreadCharacteristics on shutdown.
- Use [LibraryImport] (source-generated) rather than [DllImport] for new P/Invoke.

Acceptance: a second person hears you on Discord with VB-CABLE selected as their input. Measured round-trip latency is logged at startup. No dropouts over 10 continuous minutes.

This phase is deceptively hard and is where most of the platform pain lives. Do not treat it as boilerplate.

### Phase 1 — Ring buffer and thread separation

Insert a lock-free SPSC ring buffer on each side of a dedicated processing thread.
- Backing store is a pre-allocated float[]. Reads and writes go through Span<float> slices. Handle wraparound by slicing twice, not by per-sample index arithmetic.
- Single producer, single consumer. Use Volatile.Read/Volatile.Write on the position fields; do not use locks.
- Capacity is a power of two so the modulo becomes a mask.
- Expose a fill-level property for the drift controller and for diagnostics.

Clock drift is a real problem and must be addressed here, not deferred. The mic and VB-CABLE are separate devices with independent clocks. One may run at 48000.02 Hz and the other at 47999.97 Hz. Over minutes, the buffer monotonically fills or drains until it glitches.

Implement in this order:
1. Instrument first — log buffer fill level over time and confirm the drift direction and rate before writing any correction.
2. Simplest correction: drop or duplicate a single sample when fill level crosses a threshold.
3. Better, if time permits: an adaptive resampler with a slow control loop on fill level. This is genuinely good engineering-portfolio material.

Acceptance: 60 minutes of continuous passthrough with no dropout and no monotonic buffer drift. Fill-level graph is flat.

### Phase 2 — Phase vocoder

The core DSP. Pitch shift without changing speech rate.

Algorithm: time-stretch by factor r, then resample by 1/r. Naive resampling alone shifts pitch but also changes tempo, which desyncs your speech — that is not acceptable.
- STFT with Hann window. Start at 1024-point frames, 4× overlap (hop 256). Make both configurable.
- Analyse instantaneous frequency by unwrapping the phase difference between consecutive frames.
- Advance the synthesis phase by the stretched hop.
- ISTFT with overlap-add and correct window normalisation (use the COLA condition — verify your window/hop pair satisfies it).
- FFT: NAudio.Dsp.FastFourierTransform is acceptable, but a hand-written iterative radix-2 Cooley–Tukey in Core is preferred — it removes the dependency from Core and is worth having written.

Acceptance (all automated, no hardware):
- Feed a 440 Hz sine, request +12 semitones, assert the dominant output bin is 880 Hz ± 1%.
- Feed a sine sweep, assert monotonic frequency tracking.
- Assert output length equals input length for a range of shift factors (i.e. tempo is genuinely unchanged).
- Assert zero allocations in Process — use BenchmarkDotNet's [MemoryDiagnoser] and assert Allocated == 0.

### Phase 3 — Formant control and usability

- Independent formant shift via spectral envelope warping (cepstral liftering: take the log magnitude spectrum, low-quefrency-lifter to extract the envelope, warp the envelope's frequency axis, reapply). Separating pitch from formants is what gives you distinct "chipmunk" vs "helium" vs "deep" characters rather than one axis.
- Noise gate ahead of the vocoder. Without it, keyboard clicks get pitch-shifted into piercing squeaks. This matters more than it sounds like it does.
- Dry/wet mix.
- Presets serialised as JSON using a source-generated JsonSerializerContext (not reflection-based serialisation).
- Global hotkeys via RegisterHotKey on user32, so presets can be switched while a game has focus.
- Optional self-monitoring: a second render stream to headphones. Must be a toggle. Never enable monitoring through speakers — the mic re-captures the shifted output and you get a pitch-climbing feedback loop.

Acceptance: usable during a real game session. Preset switching works with a fullscreen game focused. No audible artifacts from keystrokes.

### Phase 4 — Neural tier

Only start this once Phases 0–3 are solid and used.

The RVC pipeline is not a single model:
```
audio chunk
  → content encoder (ContentVec / HuBERT)   → 768-dim features per frame
  → F0 extractor (RMVPE)                    → pitch contour
  → [optional] index retrieval (kNN)        → blend toward target speaker
  → generator / decoder                     → waveform
```

Key structural fact: the encoder and F0 extractor are speaker-independent; only the generator is per-voice. Load the shared sessions once at startup and hot-swap only the generator session when the user changes voice. This makes voice switching cheap.

Implementation requirements:
- Chunked streaming with overlap and crossfade. Isolated short chunks produce garbage and click at every boundary. Process overlapping windows (start ~300 ms with ~50 ms overlap, make configurable), crossfade the overlap region. The window size is the latency floor — expose it as the primary latency/quality tradeoff knob.
- IOBinding with pre-allocated OrtValues over pinned memory (CreateTensorValueFromMemory). Do not allocate tensors per inference.
- DirectML as the default execution provider, with the provider and adapter both configurable. CUDA is faster but requires the user to have matching CUDA/cuDNN installed, which is an unacceptable support burden for a distributed tool. DirectML ships in-box on Windows 11.
- Warm up with 5–10 silent inferences before opening the mic. First-call cost includes graph optimisation and kernel compilation and is far above steady state.
- Ship v1 without the faiss index. RVC runs without retrieval at some cost to timbre accuracy. If retrieval is added later, prefer a hand-written SIMD cosine similarity search over a subsampled feature set (Vector<float> or System.Runtime.Intrinsics.X86.Avx) over taking a faiss binding dependency — the query is a single small vector, and P/Invoke overhead per call may dominate faiss's batch-tuned advantage. If a faiss binding is evaluated, check specifically: does it ship prebuilt native binaries via NuGet, or does it require the user to build faiss with CMake?

Model import is a documented user workflow, not a bundled asset. Community models are distributed as PyTorch .pth and must be exported to ONNX by the user. Export can fail on custom ops and may need opset adjustment — document this honestly in the README rather than letting users discover it as a bug.

Acceptance: p99 latency measured and logged. Latency histogram committed to the repo. Voice hot-swap works without an audio dropout.

### Phase 5 — Optimisation and measurement

This phase is where most of the portfolio value is. Do not skip it.
- SIMD the hot loops — windowing, overlap-add, magnitude computation. Prefer portable Vector<float> first; reach for Avx/Avx2 intrinsics only where a profiler justifies it. Benchmark before and after; commit the numbers.
- Write loops in the canonical for (int i = 0; i < span.Length; i++) shape so the JIT elides bounds checks. Only reach for Unsafe.Add(ref MemoryMarshal.GetReference(span), i) if a profiler shows the check is costing you.
- If hand-writing AVX2 over FFT buffers, note that float[] carries no alignment guarantee — allocate with NativeMemory.AlignedAlloc and wrap via new Span<float>(ptr, len).
- Execution-provider comparison. The 155H has three inference targets: the RTX 4060, the Arc iGPU, and the Intel AI Boost NPU. Benchmark the same pipeline across all three and publish the p99 numbers. This is uncommon on a junior portfolio and is the single most distinctive thing available here. 
    - The iGPU path is not just a curiosity — routing inference to the iGPU leaves the discrete GPU entirely to the game, which removes GPU scheduling contention.
    - The NPU via the OpenVINO execution provider is an experiment, not a plan. RVC's op set may not map cleanly and partial CPU fallback could erase the benefit. Measure it; report the result either way. A documented negative result is still a good result.

---

## 6. GC configuration

.NET's GC is manageable here, but only because of invariant #1.

```xml
<ServerGarbageCollection>false</ServerGarbageCollection>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<TieredPGO>true</TieredPGO>
```

```csharp
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
```

The UI thread may allocate freely. It must never share a data path with the audio thread.

Warm the DSP path at startup — run a few hundred iterations of Process over a silence buffer before opening the stream, to promote it past tier-0 JIT. This eliminates startup crackle.

NativeAOT is explicitly out of scope for v1. Note carefully: AOT does not remove the GC — it changes when code is compiled, not how memory is managed, so it is not a solution to allocation concerns. Its actual benefit here (no JIT warmup) is obtainable far more cheaply via the warmup loop above. Its costs are real: WPF and WinForms are unsupported, COM interop requires hand-written [GeneratedComInterface] wrappers, and reflection-based DI/serialisation breaks under trimming. If AOT is revisited later, the correct shape is a headless AOT-compiled engine process with a normal .NET UI communicating over a named pipe — that is a Phase 5+ concern.

---

## 7. Known traps

Agents repeatedly get these wrong. They are listed here so you do not have to rediscover them.
- VB-CABLE is software, not hardware. It is a driver creating a paired virtual speaker and virtual mic. The user's real headset mic remains the input. Only the app's output goes into the cable.
- Riot Vanguard is a kernel-level anti-cheat. Default and document the Discord routing path rather than in-game voice. This is not a code change, but it must be in the README.
- Windows applies its own processing to laptop array mics — beamforming, AEC, noise suppression — before your app sees samples. This is uncontrolled nonlinear preprocessing sitting in front of your DSP and makes output non-reproducible. Either instruct the user to disable audio enhancements, or open the device in WASAPI exclusive mode to get the raw stream (at the cost of exclusive device ownership).
- Bluetooth mics force HFP/HSP, dropping to ~8–16 kHz and adding 100 ms+ before your code runs. Detect and warn.
- Battery power causes latency spikes. Both Windows power management and GPU clocks throttle. Detect AC status and warn on battery.
- A fullscreen exclusive game gets GPU scheduling priority and will preempt your inference workload, producing p99 outliers rather than mean-latency degradation. Recommend borderless-windowed and Hardware-Accelerated GPU Scheduling in the README. The iGPU inference path in Phase 5 is the structural fix.
- RVC converts timbre, not performance. Pitch contour, rhythm, energy, and mannerisms pass through unchanged. This means the DSP tier should feed into the neural tier, not be replaced by it — shift the speaker into the target's register first, then convert timbre. Models trained far outside the user's natural range will be unstable and artifacty because the pipeline is extrapolating.

---

## 8. Testing

- VoiceChanger.Tests must run in CI with no audio hardware present.
- Every DSP component gets synthetic-input tests: sine, sine sweep, impulse, white noise, silence.
- Assert zero allocations on all IAudioProcessor.Process implementations.
- Ring buffer gets a concurrency test: producer and consumer threads, random block sizes, assert no data loss or corruption over millions of samples.
- The latency harness is a first-class deliverable, not a debugging aid. It should be runnable by anyone cloning the repo and should emit a histogram.

---

## 9. When to stop and ask

Ask the repository owner rather than guessing when:
- A .NET 10 API you want to use cannot be confirmed in current documentation.
- An acceptance criterion cannot be met and you want to weaken it.
- You are about to add a dependency to VoiceChanger.Core.
- You are about to add anything to the repository that has an unclear licence.
- A measured result contradicts something asserted in this document. Report the measurement; this document is not authoritative over reality.

---

## 10. README framing

The README is part of the deliverable. It should lead with measured latency numbers, not a feature list. The fact that latency was measured at all — with a distribution, across execution providers — communicates more than any feature does.

Describe the work accurately and specifically. Not "a voice changer," but: a sub-30 ms real-time audio pipeline implementing a phase vocoder with independent pitch and formant control, lock-free thread handoff, clock-drift compensation across independent device clocks, and an optional ONNX streaming inference tier benchmarked across three execution providers.