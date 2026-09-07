# Implementation.md — VoiceChanger Agent Implementation Guide

> **Version:** 1.0 · **Created:** 2026-09-06 · **Maintainer:** every agent that completes a step must keep this file current (see §0)
>
> **Audience:** AI coding agents (Antigravity, GitHub Copilot, Claude Code, Cursor, Codex, opencode, …) and human developers implementing this repository.
>
> **Derived from:** [`Project.md`](Project.md) (authoritative engineering specification), [`AGENTS.md`](AGENTS.md) (governance, skills, mandatory logging), [`Summary.md`](Summary.md) (chronological execution log).
>
> **If you read only one file, read [`Project.md`](Project.md).** This document is its operational expansion: it turns the spec into an ordered work program with file-level defaults, acceptance mapping, and verification steps.

---

## 0. Authority and usage rules

| Rule | Detail |
|---|---|
| **Hierarchy** | `Project.md` > this document > personal preference. If anything here conflicts with `Project.md`, `Project.md` wins. Log the conflict in `Summary.md` and patch this file. |
| **Defaults, not decisions** | File names, class names, numeric defaults, and step ordering that go beyond the sources are **operational defaults**. Adjust them freely within the hard invariants; record any deviation in your `Summary.md` entry. |
| **No re-litigating** | The tech stack (.NET 10, Windows 11, WinUI 3, WASAPI, ONNX Runtime/DirectML), phase order, and hard invariants are decided. Objections go to `Summary.md` + the owner — never silently into code. |
| **Keep it current** | When a step or phase completes, update the tracker (§2.4) and append to `Summary.md`. Both are mandatory, on success **and** failure. |
| **Verify new APIs** | Any API you believe is new in .NET 9/.NET 10 must be verified against current documentation before use (`microsoft-docs` skill); note the verification in your `Summary.md` entry. Do not assume a feature exists because it sounds like it should. |

---

## 1. Project at a glance

| | |
|---|---|
| **Product** | Windows desktop app: capture mic → transform voice in real time → route into a virtual audio device (VB-CABLE) so Discord/games receive the transformed voice as a normal microphone. |
| **DSP tier** | Pitch + formant shifting via phase vocoder. CPU. Budget: **< 30 ms** round trip. Primary tier; must work standalone. |
| **Neural tier** | RVC-style voice conversion via ONNX Runtime. Budget: **< 300 ms**. Optional, loaded on demand, swappable behind the same `IAudioProcessor` interface. |
| **Environment** | Windows 11 Pro · .NET 10 (LTS) · Intel Core Ultra 7 155H (P/E/LP-E cores + NPU) · RTX 4060 Laptop + Arc iGPU · 32 GB · headset mic in / VB-CABLE out. Windows-only is accepted; use platform APIs freely; no cross-platform abstraction layers. |
| **UI** | WinUI 3 (`VoiceChanger.App`). |
| **Priorities** | Usable (the author games with it) **and** demonstrable (portfolio piece). When they conflict, favour code quality and measurability. |

### Hard invariants (violations are bugs even if the code appears to work)

1. **Zero heap allocation** in the audio callback path (`Process` and everything reachable from capture/render callbacks). No `new`, no LINQ, no capturing closures, no boxing, no string interpolation, no allocating logging.
2. **No inference, file I/O, locks, or unbounded work** on the audio thread. Callbacks touch ring buffers and nothing else; all processing happens on a dedicated worker thread.
3. **`VoiceChanger.Core` has zero external dependencies** — no NAudio, no WASAPI, no ONNX, no UI frameworks. Pure managed DSP over spans. Enforced by a test that asserts the assembly's referenced assemblies.
4. **No copyrighted or third-party-trained voice models** committed or distributed. Model import is a documented user workflow.
5. **Latency is always reported as a distribution** (p50, p95, p99, max) — never a mean alone.
6. **Every DSP component is testable with synthetic input** — no live audio device required for verification.

---

## 2. Current repository state (snapshot 2026-09-06)

### 2.1 What exists

- **Six-project solution**: `VoiceChanger.sln` / `VoiceChanger.slnx` with `VoiceChanger.Core`, `VoiceChanger.Audio`, `VoiceChanger.Neural`, `VoiceChanger.App`, `VoiceChanger.Tests`, `VoiceChanger.Bench`.
- **Governance**: `AGENTS.md`, `Project.md`, `Summary.md`, 24 skills under `.agents/skills/`.
- `.gitignore` present. The directory is not yet a git repository.

### 2.2 What does not exist yet

Any DSP or interop code (Phase 0+) · CI · README.

### 2.3 Naming reconciliation — decision D-1 (resolved)

| Item | Resolution |
|---|---|
| Solution + project names | `VoiceChanger.*` (`VoiceChanger.sln`, `VoiceChanger.Core`, `VoiceChanger.Audio`, `VoiceChanger.Neural`, `VoiceChanger.App`, `VoiceChanger.Tests`, `VoiceChanger.Bench`). |
| App assembly/namespace identity | **Resolved by owner:** renamed to `VoiceChanger.App` namespace and `VoiceChanger` assembly name everywhere to avoid copyright issues. |
| Repo / display name | VoiceChanger. |

### 2.4 Phase tracker (keep in sync with `Summary.md`)

| Step | Scope | Status | Latest `Summary.md` entry |
|---|---|---|---|
| S0 | Solution scaffolding | Complete (acceptance met) | [2026-09-06 16:25] |
| Phase 0 | WASAPI passthrough | Complete (acceptance met) | [2026-09-06 16:53] |
| Phase 1 | SPSC ring buffer + worker + clock drift | Complete (acceptance met) | [2026-09-06 19:05] |
| Phase 2 | Phase vocoder DSP tier | Complete (acceptance met) | [2026-09-06 20:50] |
| Phase 3 | Formant control + usability | Not started | — |
| Phase 4 | Neural tier (ONNX/DirectML) | Not started | — |
| Phase 5 | Optimization + measurement | Not started | — |

Statuses: **Not started · In progress · Complete (acceptance met) · Blocked (see Summary.md)**. A phase may only enter "Complete" when its acceptance criteria (Appendix C) are verified and logged.

---

## 3. Target solution architecture

### 3.1 Layout

```
VoiceChanger.sln
├── VoiceChanger.Core/     net10.0                        DSP, ring buffer, IAudioProcessor. Zero external deps, no I/O, spans only.
├── VoiceChanger.Audio/    net10.0-windows10.0.26100.0    WASAPI capture/render, device enumeration, MMCSS, clock drift, pipeline threads.
├── VoiceChanger.Neural/   net10.0-windows10.0.26100.0    ONNX Runtime RVC pipeline (DirectML default). Loaded on demand.
├── VoiceChanger.App/      net10.0-windows10.0.26100.0    WinUI 3 UI, tray, hotkeys, presets, config. (Current root template moves here.)
├── VoiceChanger.Tests/    net10.0-windows10.0.26100.0    xUnit. Must run in CI with no audio hardware.
└── VoiceChanger.Bench/    net10.0-windows10.0.26100.0    BenchmarkDotNet + latency histogram tooling.
```

### 3.2 Central abstraction (verbatim from `Project.md` §4)

```csharp
public interface IAudioProcessor
{
    int LatencyFrames { get; }        // algorithmic delay this processor introduces
    void Prepare(int sampleRate, int maxBlockSize);
    void Process(ReadOnlySpan<float> input, Span<float> output);
    void Reset();
}
```

`PassthroughProcessor`, `PhaseVocoderProcessor`, and `RvcProcessor` all implement this. The active processor must be hot-swappable without restarting the audio stream. `Process` may be called with a block size different from the audio callback's — the worker thread owns its own chunking.

### 3.3 Reference and dependency rules

| Project | May reference | NuGet allowed |
|---|---|---|
| `VoiceChanger.Core` | nothing | **none** — BCL only (`System.Text.Json` source generation is inbox in .NET 10, so presets serialization stays in Core without a package) |
| `VoiceChanger.Audio` | Core | none — raw interop only (`[LibraryImport]` + `[GeneratedComInterface]`) |
| `VoiceChanger.Neural` | Core | `Microsoft.ML.OnnxRuntime.DirectML` (Phase 4+); OpenVINO EP package only for Phase 5 experiments (verify current package via `microsoft-docs`) |
| `VoiceChanger.App` | Core, Audio, Neural | Windows App SDK + WinApp build tools (already in the template) |
| `VoiceChanger.Tests` | Core, Audio (+ Neural, optional) | xunit |
| `VoiceChanger.Bench` | Core, Neural | BenchmarkDotNet |

- App touches Neural types **only through a feature gate**: assemblies load on first use; init failure must degrade gracefully to DSP-only. The app must run with the neural tier disabled or absent.
- Core purity is enforced by a test (§4.3), not by trust.

### 3.4 Threading and data-flow model (target state after Phase 1)

```
mic ─▶ WASAPI capture callback ─▶ [capture SPSC ring] ─▶ worker thread ─▶ [render SPSC ring] ─▶ WASAPI render callback ─▶ VB-CABLE
                                     └── fill level ──▶ drift controller + diagnostics (monitor thread)
```

- **Capture/render callback threads** (WASAPI event-driven): touch ring buffers and nothing else. No allocation, no locks, no logging.
- **Worker thread**: fixed-size chunking, calls `IAudioProcessor.Process`, applies drift correction.
- **MMCSS "Pro Audio"** on every real-time thread (capture, render, worker) via `AvSetMmThreadCharacteristicsW` (avrt.dll); store the returned handle; call `AvRevertMmThreadCharacteristics` on shutdown. On the Core Ultra 7 155H, Windows otherwise parks the audio thread on an efficiency core → intermittent crackle. Non-optional per `Project.md`.
- **UI thread**: allocates freely; never shares a data path with the audio threads. Parameters flow UI → audio via an atomically published immutable snapshot object (§9, P3-0).
- **Diagnostics**: pre-allocated counters/fields sampled by a monitor thread. Never log on real-time threads.
- **Phase 0 interim**: before the SPSC rings exist, a minimal pre-allocated FIFO with the same discipline bridges capture → render; Phase 1 replaces it with `SpscRingBuffer` on both sides.

---

## 4. Cross-cutting engineering standards (apply in every phase)

### 4.1 Audio-path allocation rules (invariant #1)

Applies to all code reachable from capture/render callbacks and from `Process`.

**Forbidden:** `new` of any reference type · array/list/lambda-closure creation · boxing (including enum→string) · LINQ · string interpolation/formatting · `Console`/`Debug`/logger calls · `async`/`await` · `lock`/`Monitor` · `params` arrays · `yield` iterators · delegate allocation.

**Allowed:** `stackalloc` with compile-time-bounded size · buffers pre-allocated in `Prepare`/startup · `Span<T>`/`ReadOnlySpan<T>` slicing · `MemoryMarshal` · `Vector<T>` and intrinsics · static pre-computed tables.

### 4.2 Audio-thread restrictions (invariant #2)

No inference, file I/O, locks, unbounded loops, or unbounded waits on real-time threads. Bounded `SpinWait`/`Thread.Yield` backoff on the worker when rings are empty/full is acceptable. All buffers pre-allocated at startup.

### 4.3 Core purity (invariant #3)

- `VoiceChanger.Core` references only BCL assemblies.
- Enforced by `VoiceChanger.Tests/CorePurityTests`: assert that `typeof(VoiceChanger.Core.IAudioProcessor).Assembly.GetReferencedAssemblies()` contains only expected BCL assembly names; the test fails on anything else.
- Consequence worth noting: the `NAudio.Dsp.FastFourierTransform` option in `Project.md` Phase 2 is **excluded in practice** by this invariant — write the hand-written radix-2 FFT, which `Project.md` prefers anyway.

### 4.4 GC configuration and startup warm-up (`Project.md` §6)

Apply in the App (and any process hosting the pipeline):

```xml
<ServerGarbageCollection>false</ServerGarbageCollection>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<TieredPGO>true</TieredPGO>
```

```csharp
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
```

Before opening the stream, run a few hundred iterations of `Process` over a silence buffer (promotes past tier-0 JIT; eliminates startup crackle). Neural tier: 5–10 silent inferences before the mic opens (Phase 4). NativeAOT is out of scope for v1 — do not propose it.

### 4.5 Latency reporting standard (invariant #5)

Every latency number, anywhere — logs, README, committed results — is a distribution in this shape:

```
p50    p95    p99    max    samples   duration   configuration
```

Never a mean alone. A pipeline with an 80 ms mean and a 400 ms p99 is a broken pipeline. The latency harness (Phase 5) is a first-class deliverable, runnable by anyone cloning the repo, and emits a histogram.

### 4.6 Testing standards (`Project.md` §8)

- `VoiceChanger.Tests` runs in CI with **no audio hardware present**.
- Every DSP component gets synthetic-input tests: sine, sine sweep, impulse, white noise, silence.
- Zero-allocation assertions on every `IAudioProcessor.Process` implementation (see P2-6).
- Ring buffer: producer + consumer threads, random block sizes, millions of samples, assert no loss or corruption.
- Device-dependent interop: hardware-guarded smoke tests that skip in CI; hardware validation is a manual soak, documented in `Summary.md`.

### 4.7 Code and interop style

- Nullable + implicit usings on; file-scoped namespaces; `sealed` by default; XML docs on public APIs (`csharp-docs` skill); follow `.agents/skills/dotnet-best-practices`.
- Hot loops in the canonical `for (int i = 0; i < span.Length; i++)` shape so the JIT elides bounds checks.
- **All new P/Invoke via `[LibraryImport]`** (source-generated), never `[DllImport]`. COM via `[GeneratedComInterface]`. For string parameters use `StringMarshalling = StringMarshalling.Utf16` (e.g., `AvSetMmThreadCharacteristicsW`, `RegisterHotKeyW`).

---

## 5. Step S0 — Solution scaffolding (mandatory pre-step)

**Objective:** turn the bare WinUI template at the root into the six-project solution of §3.
**Entry criteria:** none — this is the first actionable step.
**Skills:** `winui-dev-workflow`, `dotnet-best-practices` (toolchain problems → `winui-setup`).

| ID | Task |
|---|---|
| S0-1 | `dotnet new sln -n VoiceChanger` at repo root. |
| S0-2 | Create `VoiceChanger.App/`. Move `VoiceMod.csproj` → `VoiceChanger.App/VoiceChanger.App.csproj` and move `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `MainPage.xaml(.cs)`, `Package.appxmanifest`, `app.manifest`, `Assets/`, `Properties/` into it. **Keep `<RootNamespace>VoiceMod</RootNamespace>` and `<AssemblyName>VoiceMod</AssemblyName>`** (XAML `x:Class` and MSIX identity unchanged — D-1). Delete stale root `bin/` and `obj/` after the move. |
| S0-3 | Create Core: `dotnet new classlib -n VoiceChanger.Core -o VoiceChanger.Core -f net10.0`; delete `Class1.cs`. |
| S0-4 | Create Audio and Neural classlibs targeting `net10.0-windows10.0.26100.0` (if the CLI rejects the windows TFM for the template, create with the default TFM and edit the `.csproj`). Neural is an **empty placeholder** until Phase 4 — no ONNX package yet. |
| S0-5 | Create Tests (`dotnet new xunit`) and Bench (`dotnet new console`), both targeting `net10.0-windows10.0.26100.0` (Tests must match Audio's TFM to reference it). Add the BenchmarkDotNet package to Bench. |
| S0-6 | `dotnet sln add` all six projects; wire project references per §3.3. |
| S0-7 | Add root `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, `TreatWarningsAsErrors=true` (relax the last one if template/XAML warnings block the build — record the decision). |
| S0-8 | Add `CorePurityTests` (§4.3) — it passes trivially on an empty Core and becomes the permanent invariant guard. |
| S0-9 | Version control: the directory is **not yet a git repository**. Ask the owner to confirm, then `git init` + initial commit of governance docs and scaffold. Do not initialize or commit without owner confirmation. |
| S0-10 | CI workflow per §12 (requires a GitHub remote — coordinate with the owner; may land after S0). |

**Verification:** `dotnet build VoiceChanger.sln` with zero errors · `dotnet test` green · `dotnet run --project VoiceChanger.App` launches the empty window (see `winui-dev-workflow` for run/crash diagnosis).
**Exit checklist:** build/test/launch verified · `Summary.md` entry appended · tracker §2.4 updated.

---

## 6. Phase 0 — WASAPI passthrough

**Objective** (`Project.md` §5 Phase 0): capture the default mic, write unmodified to the user-selected output device (VB-CABLE). WASAPI **shared mode**, **event-driven** (not polling), 48 kHz, mono, float32. MMCSS "Pro Audio" on the audio thread. `[LibraryImport]` for all new P/Invoke.
**Entry criteria:** S0 complete.
**Skills:** `winmd-api-search` (WASAPI/MMCSS API shapes), `microsoft-docs` (verify every P/Invoke and COM signature), `dotnet-best-practices`, `csharp-docs`, `winui-app` + `winui-design` (device picker), `winui-dev-workflow` (build/run).
**Warning:** this phase is deceptively hard and is where most of the platform pain lives. Do not treat it as boilerplate.

| ID | Task / File (defaults) | Notes |
|---|---|---|
| P0-1 | Core: `IAudioProcessor.cs`, `PassthroughProcessor.cs` | Interface verbatim (§3.2). Passthrough copies input → output and is the zero-allocation template for all future processors. |
| P0-2 | Audio: `Interop/Avrt.cs` | `[LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]` — `AvSetMmThreadCharacteristicsW("Pro Audio", ref uint) → nint handle`, `AvRevertMmThreadCharacteristics(nint)`. Verify signatures before use. |
| P0-3 | Audio: `Interop/MmDeviceApi.cs`, `Interop/Ole32.cs`, `Interop/Kernel32Events.cs` | COM interfaces via `[GeneratedComInterface]`: `IMMDeviceEnumerator`, `IMMDevice`, `IMMDeviceCollection`, `IMMEndpoint`, `IAudioClient`, `IAudioCaptureClient`, `IAudioRenderClient`. Enumerator via `CoCreateInstance` (ole32.dll) with `CLSID_MMDeviceEnumerator` / `IID_IMMDeviceEnumerator` from `mmdeviceapi.h`. Event handles via `CreateEventW` (kernel32.dll), created once, pre-allocated path only. |
| P0-4 | Audio: `Devices/AudioDeviceList.cs` | Enumerate capture + render endpoints via `IMMDeviceEnumerator`; friendly names via `IPropertyStore` / `PKEY_Device_FriendlyName`. The **output target must be user-selectable** so VB-CABLE can be chosen. |
| P0-5 | Audio: `WasapiCaptureStream.cs` / `WasapiRenderStream.cs` | Shared mode, **event-driven** (`AUDCLNT_STREAMFLAGS_EVENTCALLBACK` + `SetEventHandle`). Initialize with 48 kHz / 1 channel / `WAVE_FORMAT_IEEE_FLOAT`, plus `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY` so the engine converts from the mix format. Handle `AUDCLNT_E_DEVICE_INVALIDATED` with a clean shutdown/restart path. |
| P0-6 | Audio: `PassthroughBridge.cs` (interim) | Minimal pre-allocated FIFO between the two callback threads, same allocation discipline. Phase 1 replaces it with `SpscRingBuffer`. |
| P0-7 | Audio: `AudioEngine.cs` | Owns the threads; applies MMCSS "Pro Audio" to capture and render threads (worker added in Phase 1); stores handles and reverts them on shutdown **including exception paths**; measures and logs round-trip latency at startup (QPC via `Stopwatch.GetTimestamp`; report as a distribution, §4.5; method choice is yours — e.g. frame-marker timestamps plus `IAudioClient` stream latency). |
| P0-8 | App: minimal pipeline page | Input = default mic; output combo (user-selectable); Start/Stop; status + startup latency display. Keep it minimal — the real UI is Phase 3. |
| P0-9 | Tests: `PassthroughProcessorTests` | Copy fidelity on synthetic input (sine, impulse, noise, silence) + zero-allocation check (§4.6). Interop classes get hardware-guarded smoke tests only. |

**Acceptance (`Project.md`):** a second person hears you on Discord with VB-CABLE selected as their input · measured round-trip latency logged at startup · **no dropouts over 10 continuous minutes**. The soak is owner-verified — document the run in `Summary.md`.
**Pitfalls:** polling instead of event-driven · losing/leaking the MMCSS handle · forgetting `AvRevert` on error paths · assuming the shared-mode mix format is already 48 kHz mono float · wrong `ReleaseBuffer` flags (`AUDCLNT_BUFFERFLAGS_SILENT`) · device invalidated on sleep/endpoint change going unhandled · `[DllImport]` instead of `[LibraryImport]`.

---

## 7. Phase 1 — Lock-free SPSC ring buffer and thread separation

**Objective:** an SPSC ring on each side of a dedicated processing thread; clock-drift compensation, **instrumented first**.
**Entry criteria:** Phase 0 acceptance met and logged.
**Skills:** `dotnet-design-pattern-review` (SPSC patterns), `csharp-async` (thread safety/cancellation), `dotnet-best-practices`.

| ID | Task / File | Notes |
|---|---|---|
| P1-1 | Core: `Buffers/SpscRingBuffer.cs` | Backing store is a pre-allocated `float[]`; capacity a **power of two** (modulo becomes a mask). Single producer, single consumer. Monotonic `long` write/read positions with `Volatile.Read`/`Volatile.Write`; **no locks**. Wraparound handled by **two span slices**, never per-sample index arithmetic. Expose a fill-level property for the drift controller and diagnostics. API sketch below. |
| P1-2 | Tests: `SpscRingBufferTests` | (a) Single-thread correctness across all wraparound boundaries. (b) Concurrency hammer: producer + consumer threads, seeded-random block sizes, millions of samples, checksum equality — no loss or corruption. |
| P1-3 | Audio: `ProcessingPipeline.cs` | Dedicated worker thread: drain the capture ring in fixed chunks (default 256 frames — adjustable), call `Process`, fill the render ring. Underflow → write silence frames + bump a counter; overflow → drop-oldest + counter. Wire `SpscRingBuffer` both sides and retire `PassthroughBridge`. |
| P1-4 | Audio: drift **instrumentation first** | Monitor thread samples ring fill level (≥ 1 Hz) and logs it. **Confirm drift direction and rate before writing any correction** — this order is specified, not stylistic. |
| P1-5 | Audio: `ClockDriftController.cs` | Simplest correction: drop or duplicate a **single sample** when fill level crosses a threshold. Defaults (tune from P1-4 data): act above 75% / below 25% with hysteresis and a correction-rate limit. Runs on the worker, never on the callbacks. |
| P1-6 | (stretch) Core: adaptive resampler | Slow control loop on fill level. Genuinely good portfolio material — only after P1-5 is verified. |

API sketch (shape, not gospel):

```csharp
public sealed class SpscRingBuffer
{
    public SpscRingBuffer(int capacity);        // throws unless power of two
    public int Capacity { get; }
    public long FillCount { get; }             // for drift controller + diagnostics
    public bool Write(ReadOnlySpan<float> src); // false when full
    public int Read(Span<float> dst);           // frames actually read
    public void Reset();                        // startup/shutdown only, not thread-safe
}
```

**Acceptance (`Project.md`):** 60 minutes of continuous passthrough with **no dropout and no monotonic buffer drift**; the fill-level graph is flat. Concurrency test green in CI.
**Pitfalls:** full/empty ambiguity (use monotonic counters, not wrapped indices) · publishing positions before data is written (order matters with `Volatile`) · per-sample wrap arithmetic instead of two slices · running correction on the callback threads · tuning thresholds without instrumentation data.

---

## 8. Phase 2 — Phase vocoder (DSP tier)

**Objective:** pitch shift without changing speech rate. Algorithm: time-stretch by factor r, then resample by 1/r. (Resampling alone changes both pitch and tempo and desyncs speech — unacceptable.)
**Entry criteria:** Phase 1 acceptance met and logged.
**Skills:** `dotnet-best-practices`, `csharp-scripts` (isolated DSP experiments), `dotnet-design-pattern-review`, `csharp-docs`.

| ID | Task / File | Notes |
|---|---|---|
| P2-1 | Core: `Dsp/Fft.cs` | Hand-written iterative radix-2 Cooley–Tukey, forward + inverse. Bit-reversal and twiddle factors **pre-computed and pre-allocated** in the constructor/`Prepare`. (NAudio's FFT is excluded by invariant #3 — §4.3.) |
| P2-2 | Core: `Dsp/Window.cs` | Hann window. **Verify the COLA condition** for the window/hop pair with an automated test (sum of squared windows at hop 256 is flat within tolerance) — not by eyeballing. |
| P2-3 | Core: `Dsp/Resampler.cs` | Zero-allocation fractional-position reader; linear interpolation to start (upgradeable); all state pre-allocated. |
| P2-4 | Core: `Dsp/PhaseVocoderProcessor.cs` | Defaults: Hann 1024-point frames, hop 256 (4× overlap) — **both configurable**. Pipeline: frame → window → FFT → phase analysis (unwrap consecutive-frame phase differences → instantaneous frequency) → advance synthesis phase by the stretched hop → ISTFT overlap-add with correct COLA normalization → resample by 1/r. Every buffer allocated in `Prepare`. `LatencyFrames` = frame length. |
| P2-5 | Tests | FFT: round-trip identity, impulse response, known sine bins, Parseval. Vocoder: the acceptance list below. Allocation: `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across many `Process` calls. |
| P2-6 | Bench: `PhaseVocoderBench` | BenchmarkDotNet with `[MemoryDiagnoser]`, **assert `Allocated == 0`** (spec-required method); record p50/p95/p99/max `Process` time (§4.5). |

**Acceptance (`Project.md` — all automated, no hardware):**
- Feed a 440 Hz sine, request +12 semitones → assert the dominant output bin is 880 Hz ± 1%.
- Feed a sine sweep → assert monotonic frequency tracking.
- Assert output length equals input length for a range of shift factors (tempo genuinely unchanged).
- Assert zero allocations in `Process` via `[MemoryDiagnoser]` (`Allocated == 0`).

**Pitfalls:** resampling without time-stretch (tempo desync) · wrong overlap-add normalization (analysis + synthesis Hann → normalize by Σ w²) · phase unwrapping not wrapped to (−π, π] · tail/partial-frame handling breaking exact length equality · allocation sneaking into the per-call path · `stackalloc` with runtime-dependent sizes.

---

## 9. Phase 3 — Formant control and usability

**Objective:** independent formant shift (spectral envelope warping), noise gate, dry/wet, presets, global hotkeys, optional self-monitoring.
**Entry criteria:** Phase 2 acceptance met and logged.
**Skills:** `winui-design`, `winui-app`, `anti-ui-slop`, `winui-code-review`, `winmd-api-search` (RegisterHotKey), `microsoft-docs` (source-generated JSON), `csharp-docs`.

**Parameter-passing pattern (P3-0, used everywhere from here on):** the UI thread builds a **new immutable parameters object** and atomically publishes it (`Volatile.Write` of a reference); the worker reads it once per block and never mutates it. Allocation happens on the UI thread only — the audio path stays allocation-free.

| ID | Task / File | Notes |
|---|---|---|
| P3-0 | `Parameters` snapshot pattern | As above. Every processor knob (pitch, formant, gate, mix) flows through it. |
| P3-1 | Core: `Dsp/NoiseGate.cs` | Placed **ahead of the vocoder** — without it, keyboard clicks get pitch-shifted into piercing squeaks. Envelope follower in dB with hysteresis, attack/release, zero-alloc. Synthetic test: a keystroke-like transient is attenuated. |
| P3-2 | Core: formant warp, default inside `PhaseVocoderProcessor`'s spectral stage | Cepstral liftering: log magnitude spectrum → real cepstrum (IFFT) → low-quefrency lifter to extract the envelope → warp the envelope's frequency axis → recombine (divide original spectrum by original envelope, multiply by warped envelope). Independent factor from pitch — this is what separates "chipmunk" vs "helium" vs "deep". Buffers pre-allocated. (Alternative: a separate STFT processor — record any deviation in `Summary.md`; D-5.) |
| P3-3 | Core: `Dsp/DryWetMixer.cs` + `Dsp/ProcessorChain.cs` | Composite `IAudioProcessor` with a single allocation domain. Chain: gate → vocoder (+ formant warp) → dry/wet mix. |
| P3-4 | Core: `Presets/Preset.cs` + `Presets/PresetJsonContext.cs` | Source-generated `JsonSerializerContext` — **not** reflection-based serialization (spec). Inbox in .NET 10, so Core stays dependency-free. |
| P3-5 | App: `PresetManager` + preset UI | Presets at `%APPDATA%\VoiceChanger\presets.json`; save/load/switch; sliders for pitch/formant/gate/mix. Consult `winui-design` + `anti-ui-slop`. |
| P3-6 | App: `Services/HotkeyService.cs` | `RegisterHotKeyW` (user32, `[LibraryImport]`), so presets switch while a game has focus. Deliver `WM_HOTKEY` via `SetWindowSubclass` on the main window HWND (`WinRT.Interop.WindowNative.GetWindowHandle`) or a dedicated message-only window. `UnregisterHotKey` on teardown. Default hotkeys must be user-configurable. |
| P3-7 | Audio + App: self-monitoring toggle | Second render stream to **headphones only**; a toggle, default OFF. **Never enable monitoring through speakers** — the mic recaptures the shifted output and you get a pitch-climbing feedback loop (`Project.md` §7 trap). |
| P3-8 | App: tray icon (optional — confirm scope with owner, D-3) | `Shell_NotifyIcon` interop or equivalent. Part of App scope per `Project.md` §4. |

**Acceptance (`Project.md`):** usable during a real game session · preset switching works with a fullscreen game focused · no audible artifacts from keystrokes. Automated portions: gate transient test, preset JSON round-trip test. The game-session check is manual — document it in `Summary.md`.
**Pitfalls:** reflection-based JSON (must be source-gen) · monitoring through speakers (feedback loop) · hotkey ID/registration leaks · UI reading/writing DSP state directly instead of snapshots.

---

## 10. Phase 4 — Neural tier (RVC via ONNX Runtime)

**Entry criteria (spec):** Phases 0–3 are solid **and in real use**. Do not start earlier.
**Skills:** `microsoft-docs` (verify ONNX Runtime/DirectML APIs), `winmd-api-search`, `csharp-async`, `dotnet-best-practices`, `winui-app`, `winui-code-review`.

Pipeline (`Project.md` §5 Phase 4):

```
audio chunk
  → content encoder (ContentVec / HuBERT)  → 768-dim features per frame
  → F0 extractor (RMVPE)                   → pitch contour
  → [optional, not v1] index retrieval     → blend toward target speaker
  → generator / decoder                    → waveform
```

**Structural fact to exploit:** encoder and F0 extractor are speaker-independent; **only the generator is per-voice**. Load the shared sessions once at startup; hot-swap only the generator session when the user changes voice.

| ID | Task | Notes |
|---|---|---|
| P4-1 | Neural: add `Microsoft.ML.OnnxRuntime.DirectML` | Verify the package and current API surface via `microsoft-docs` first (§0 rule). DirectML is the default EP (ships in-box on Windows 11; CUDA rejected as a support burden — decided, do not revisit). Provider and adapter both configurable. |
| P4-2 | Neural: `RvcModelSet.cs` + `VoiceCatalog.cs` | Shared sessions (encoder, F0) once at startup; generator session per voice, **hot-swappable without audio dropout**. Models live in a user models directory — **never bundled or committed** (invariant #4). |
| P4-3 | Neural: `ChunkedStreamer.cs` | Chunked streaming with **overlap and crossfade** — isolated short chunks produce garbage and click at every boundary. Defaults: ~300 ms windows, ~50 ms overlap, **both configurable**. The window size is the latency floor: expose it as the primary latency/quality knob. |
| P4-4 | Neural: `OrtBufferPool.cs` | IOBinding with **pre-allocated `OrtValue`s over pinned memory** (`CreateTensorValueFromMemory`). Do **not** allocate tensors per inference. |
| P4-5 | Neural: `RvcProcessor.cs : IAudioProcessor` | Same contract as the DSP tier; swappable behind `IAudioProcessor` (hot-swap without stream restart). `LatencyFrames` reflects the chunk window. |
| P4-6 | Warm-up | 5–10 **silent** inferences before the mic opens. First-call cost (graph optimization, kernel compilation) is far above steady state. |
| P4-7 | App: feature gate | Neural is optional: construct neural types only on activation (lazy assembly load); if ONNX/DirectML initialization fails, degrade gracefully to DSP-only. The app must run with the tier disabled or absent. |
| P4-8 | README: model import workflow | Community models are PyTorch `.pth` and must be exported to ONNX by the user. Export can fail on custom ops and may need opset adjustment — document honestly. No URLs that constitute distribution (invariant #4). |

v1 ships **without** the faiss index (decided). If retrieval is ever added: prefer a hand-written SIMD cosine search over a subsampled feature set (`Vector<float>` or `System.Runtime.Intrinsics.X86.Avx`) over a faiss binding; if evaluating a binding, check specifically whether it ships prebuilt native binaries via NuGet or requires the user to build faiss with CMake.

**Acceptance (`Project.md`):** p99 latency measured and logged · latency histogram committed to the repo · voice hot-swap works without an audio dropout.
**Pitfalls:** per-inference tensor allocation · skipping warm-up (first-call spike misread as steady state) · chunk boundaries without crossfade · committing models · extrapolation artifacts when the target voice is far outside the user's natural range (`Project.md` §7 — the DSP tier shifts register first; put README guidance next to model import).

---

## 11. Phase 5 — Optimization and measurement

**Entry criteria:** Phase 4 complete. This phase carries most of the portfolio value — do not skip it.
**Skills:** `dotnet-best-practices` (performance idioms), `microsoft-docs` (intrinsics/EP verification), `create-readme`.

| ID | Task | Notes |
|---|---|---|
| P5-1 | Profile before optimizing | BenchmarkDotNet + a profiler (PerfView/VS). Numbers before and after every change; commit them. |
| P5-2 | SIMD the hot loops | Windowing, overlap-add, magnitude computation. **Portable `Vector<float>` first**; AVX/AVX2 intrinsics only where a profiler justifies them. Canonical `for` loop shape for bounds-check elision; `Unsafe.Add(ref MemoryMarshal.GetReference(span), i)` only with profiler evidence. If hand-writing AVX2 over FFT buffers: `float[]` carries no alignment guarantee — allocate with `NativeMemory.AlignedAlloc` and wrap via `new Span<float>(ptr, len)`. |
| P5-3 | Execution-provider comparison | Same pipeline across all three targets on this machine: **RTX 4060 (DirectML)**, **Arc iGPU (DirectML, other adapter)**, **Intel AI Boost NPU (OpenVINO EP — an experiment, not a plan)**. Publish p50/p95/p99/max for each. A documented negative result is still a good result. The iGPU path leaves the discrete GPU entirely to the game (structural fix for fullscreen preemption — `Project.md` §7). |
| P5-4 | Latency harness as a first-class deliverable | Runnable by anyone cloning the repo; emits a histogram; results committed under `bench/results/`. |
| P5-5 | README | Lead with measured latency numbers (distribution, across execution providers), then the accurate, specific description from `Project.md` §10. Use the `create-readme` skill. |

**Acceptance:** committed benchmark tables and histograms · EP comparison published (RTX 4060 / Arc iGPU / NPU) · README leads with measurements.

---

## 12. Verification and CI

Commands (PowerShell, repo root):

```
dotnet build VoiceChanger.sln -c Release
dotnet test  VoiceChanger.Tests
dotnet run  --project VoiceChanger.App        # WinApp run support is configured in the csproj
dotnet run  --project VoiceChanger.Bench -- --job latency    # once P5-4 exists
```

For WinUI run/crash workflow specifics (project-mode running, crash diagnosis), consult `winui-dev-workflow`.

CI requirements:
- GitHub Actions on a Windows runner, .NET 10 SDK, `dotnet build` + `dotnet test`.
- Tests must pass with **no audio hardware present** (§4.6). Hardware soaks stay manual and are documented in `Summary.md`.
- Author/review workflows with the `authoring-github-workflows`, `github-actions-hardening`, and `github-actions-efficiency` skills.
- Benchmarks are manual or scheduled, never per-PR.
- Requires a GitHub remote — the repo is not yet under git (S0-9, D-2); coordinate with the owner.

---

## 13. Per-task execution protocol

1. **Read** the relevant `Project.md` section, this document's step, and the mapped skills (Appendix A). Read the latest `Summary.md` entries for current state.
2. **Check entry criteria** against the tracker (§2.4). Never start a phase before the previous phase's acceptance is met and logged.
3. **Implement** within the invariants (§4). Prefer the defaults here; record deviations.
4. **Verify:** build, tests, benchmarks; hardware soaks where acceptance demands them; keep the invariant tests green.
5. **Log:** append a `Summary.md` entry (Appendix B) — mandatory on success, failure, and interruption alike.
6. **Update** the tracker (§2.4) and, if defaults or architecture details changed, this document.
7. **Report** concisely: what was done, acceptance status, what is next.

---

## 14. Stop and ask the owner (`Project.md` §9)

- A .NET 10 API you want to use cannot be confirmed in current documentation.
- An acceptance criterion cannot be met and you want to weaken it.
- You are about to add a dependency to `VoiceChanger.Core`.
- You are about to add anything with an unclear licence to the repository.
- A measured result contradicts something asserted in `Project.md`. Report the measurement — this document is not authoritative over reality.

---

## 15. Open questions for the repository owner

| ID | Question | Default until answered |
|---|---|---|
| D-1 | Rename VoiceMod to VoiceChanger everywhere? | **Resolved**: Renamed to VoiceChanger everywhere (namespaces, assembly names, manifests) per owner instruction |
| D-2 | `git init` + GitHub remote to enable CI (S0-9/S0-10)? | Ask before initializing |
| D-3 | Is the tray icon in Phase 3 scope? | Optional P3-8, confirm |
| D-4 | Offer WASAPI exclusive-mode capture as an option to bypass vendor AEC/beamforming (`Project.md` §7 trap)? | Shared mode only; README instructs disabling enhancements |
| D-5 | Formant warp inside the vocoder's spectral stage vs. a separate STFT processor? | Inside the vocoder (P3-2) |

---

## Appendix A — Skills map by work area

| Work | Skills to consult |
|---|---|
| S0 scaffolding | `winui-dev-workflow`, `dotnet-best-practices` (`winui-setup` if the toolchain is broken) |
| Phase 0 | `winmd-api-search`, `microsoft-docs`, `dotnet-best-practices`, `csharp-docs`, `winui-app`, `winui-design`, `winui-dev-workflow` |
| Phase 1 | `dotnet-design-pattern-review`, `csharp-async`, `dotnet-best-practices` |
| Phase 2 | `dotnet-best-practices`, `csharp-scripts`, `dotnet-design-pattern-review`, `csharp-docs` |
| Phase 3 | `winui-design`, `winui-app`, `anti-ui-slop`, `winui-code-review`, `winmd-api-search`, `microsoft-docs`, `csharp-docs` |
| Phase 4 | `microsoft-docs`, `winmd-api-search`, `csharp-async`, `dotnet-best-practices`, `winui-app`, `winui-code-review` |
| Phase 5 | `dotnet-best-practices`, `microsoft-docs`, `create-readme` |
| CI / packaging / release | `authoring-github-workflows`, `github-actions-hardening`, `github-actions-efficiency`, `winui-packaging`, `github-release` |
| Documentation | `csharp-docs`, `create-readme` |

## Appendix B — `Summary.md` entry template

```
## [YYYY-MM-DD HH:mm] - <Task / Objective>

- **Agent / Tool**: <name / model>
- **Objective**: <what was requested and planned>
- **What Was Done**: <actions, files created/modified/deleted, commands executed; cite task IDs (S0-2, P1-5, ...)>
- **Results & Status**: Completed | In Progress | Blocked | Failed
- **Errors & Failures**: <build errors, runtime exceptions, failing tests, roadblocks + troubleshooting attempted>
- **Successes & Verification**: <build status, passing tests, benchmark metrics, soak results>
- **Next Steps**: <explicit recommendations for the next agent or developer turn>
```

## Appendix C — Acceptance criteria quick reference (from `Project.md` §5)

| Phase | Acceptance |
|---|---|
| 0 | Discord hears you via VB-CABLE; round-trip latency logged at startup; no dropouts over 10 continuous minutes |
| 1 | 60 minutes continuous passthrough; no dropout; no monotonic buffer drift; fill-level graph flat |
| 2 | 440 Hz +12 semitones → dominant bin 880 Hz ± 1%; sine sweep tracked monotonically; output length == input length across shift factors; `[MemoryDiagnoser]` Allocated == 0 |
| 3 | Usable during a real game session; preset switching works with a fullscreen game focused; no audible keystroke artifacts |
| 4 | p99 latency measured and logged; latency histogram committed to the repo; voice hot-swap without audio dropout |
| 5 | SIMD and execution-provider benchmarks committed (p50/p95/p99/max across RTX 4060 / Arc iGPU / NPU); README leads with measurements |

---

*End of Implementation.md. Remember: this document operationalizes `Project.md` — when reality and this file disagree, measure it, log it in `Summary.md`, and fix whichever document is wrong.*




