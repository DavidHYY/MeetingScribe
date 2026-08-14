# macOS Port — Handoff Brief

**Status: implemented and merged (PR #1, `feat/macos-audio-capture`).** This
brief was written before anyone knew the work was already done — it
originally opened with "macOS does not run yet." That is no longer true.
Real audio capture, verified on real hardware (macOS 26.5, Apple M2, .NET
10.0.302), landed via PR #1 and is now on `main`. The authoritative,
currently-accurate description of what is implemented and what was measured
is the root [`README.md`](../../README.md)'s "Cross-platform status" and
"Whisper on macOS" sections — read those first. This document is kept as
architecture/seam background (still accurate) plus the pre-port checklist,
annotated with what was actually done differently or left open.

See [`STATUS.md`](STATUS.md) for the filled-in progress table and measured
numbers (backfilled from the PR's own commits and README changes — no
`STATUS.md` was kept live during the actual port, since the PR branch was
started before this `docs/mac-port/` directory existed on `main`).

## What the app is

MeetingScribe records a meeting (microphone + system audio as separate
tracks), shows a rough live transcript while the meeting runs, re-transcribes
accurately afterward with a larger Whisper model, then generates meeting
minutes via a pluggable backend (Claude, Codex, or local Ollama). Recording
and transcription run entirely locally — no network, no API key needed for
those two stages. See the repo root [`README.md`](../../README.md) for the
full feature writeup; this doc is only the macOS-port seam.

## The exact seam

- `MeetingScribe.Audio.Abstractions` (`MeetingScribe.Audio.Abstractions.csproj`,
  plain `net10.0`, no platform code) defines the contract:
  `IAudioPlatform` (device enumeration + recorder factory) and
  `IMeetingRecorder` (`Start()` / `StopAsync()`, `MicWavPath` /
  `SystemWavPath` / `MixedWavPath`, `LevelUpdated` / `CaptureError` /
  `SamplesAvailable` events, `Errors`). App code only ever touches this
  interface, never a concrete recorder, never NAudio. **This held**: the Mac
  implementation plugs into the same interface with no changes to it.
- `MeetingScribe.Audio.Windows` (`net10.0-windows`) is the **working
  reference implementation** — NAudio/WASAPI, `MeetingRecorder.cs`,
  `AudioDeviceEnumerator.cs`, `WindowsAudioPlatform.cs`. This is still what
  ships on Windows, unchanged in logic; only its `.csproj` gained an
  `$(OS)`-conditional `NAudio.WinForms` reference so the solution can build
  on a macOS host at all (see "Build fix" below).
- `MeetingScribe.Audio.Mac` (plain `net10.0`) — **no longer a stub.** Real
  implementation: `MacAudioPlatform` + `MacMeetingRecorder` +
  `Internal/Mac{Mic,System,Source}Pipeline.cs`, `NativeMethods.cs`,
  `PcmWavWriter.cs`, `MacDeviceEnumerator.cs`, `MacDeviceResolver.cs`.
  Microphone via AVFoundation `AVCaptureSession`; system audio via
  **ScreenCaptureKit** (`SCStream`) — picked over a virtual loopback device
  like BlackHole specifically so the user does not need to install anything.
  Both paths resample to 16 kHz mono 16-bit PCM via `AVAudioConverter`, not
  naive decimation. The `SCStreamOutput` protocol is impractical to
  hand-roll from pure C#, so it sits behind a small Objective-C helper dylib
  (`MeetingScribe.Audio.Mac/native/meetingscribe_mac_audio.m`), built by an
  MSBuild target (`BuildMacNativeAudioHelper`) that runs `clang` on macOS
  hosts only, and P/Invoked from C# via `[UnmanagedCallersOnly]` callbacks.

## The App already compiles the Mac leg

`MeetingScribe.App.csproj` still multi-targets — now conditionally:
`net10.0-windows;net10.0` on a Windows build host, plain `net10.0` on a
non-Windows host (a straight unconditional multi-target was not buildable on
macOS at all: NAudio.WinForms's `FrameworkReference` doesn't exist there).
The conditional `ProjectReference` per TFM described in the original version
of this doc is unchanged in spirit:

```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
  <ProjectReference Include="..\MeetingScribe.Audio.Windows\MeetingScribe.Audio.Windows.csproj" />
</ItemGroup>
<ItemGroup Condition="'$(TargetFramework)' != 'net10.0-windows'">
  <ProjectReference Include="..\MeetingScribe.Audio.Mac\MeetingScribe.Audio.Mac.csproj" />
</ItemGroup>
```

`Bootstrap/AudioPlatformProvider.Windows.cs` and
`Bootstrap/AudioPlatformProvider.Other.cs` still mirror this with a
`<Compile Remove>` condition so exactly one is compiled in. On macOS:
`dotnet build MeetingScribeCS.sln -c Release` — **0 errors, 0 warnings, 7
projects.** Running the app for real on macOS is
`dotnet run --project MeetingScribe.App -c Release -f net10.0`.

## Whisper.net.Runtime.Vulkan is Windows-specific — resolved

The original version of this doc flagged this as unsolved; it is now fixed.
`MeetingScribe.Whisper.csproj` conditions the native runtime package on host
OS: `Whisper.net.Runtime.Vulkan` stays exactly as-is on Windows (unchanged
behaviour), and non-Windows hosts get plain `Whisper.net.Runtime` 1.9.1,
which already bundles `libggml-metal-whisper.dylib` for `macos-arm64` — no
extra package needed for Metal acceleration. `Whisper.net.Runtime.CoreML`
was deliberately **not** used: it needs a separately generated
`ggml-<model>-encoder.mlmodelc` bundle (Python + coremltools + Xcode), which
this build does not produce. `Whisper.net`'s `RuntimeLibrary` enum has no
Metal case (Metal is compiled into the same native lib it otherwise reports
as `Cpu` on macOS), so `BackendInfo`/`WhisperTranscriber` now infer Metal
from the native log (`ggml_metal_device_init`) instead of trusting the enum.
Measured on Apple M2, medium model, 68.96 s multilingual clip:
`LoadedLibrary=Metal`, `IsGpuBackend=True`, **9.57x realtime**. A second run
(69 s clip) also measured 9.57x; a 33 s clip measured 6.40x.

## Behaviour matched

- Mic and system audio captured as **separate tracks** (`mic.wav`,
  `system.wav`) plus a **mixed** track (`raw.wav`) — confirmed working on
  macOS after fixing a real mixer bug (see `STATUS.md` open-issue log: the
  mixer originally dropped ~44% of one 15 s take by truncating to
  `Math.Min(micChunk, systemChunk)` per read instead of buffering the
  remainder — fixed by sample-level buffering).
- All three: **16 kHz mono, 16-bit PCM** — confirmed via independent
  read-back (`afinfo` + this repo's own `WavAnalyzer`).
- Written and flushed continuously: `PcmWavWriter` patches the RIFF/data
  chunk sizes on every flush, verified crash-resilient (`kill -9` mid-record
  leaves a correctly-sized, cleanly-parseable file, confirmed via `afinfo`).

## macOS permissions (TCC) — partially verified

Screen-recording access (`CGPreflightScreenCaptureAccess`) was confirmed
already granted on the verification machine, and the system-audio path
(ScreenCaptureKit) was fully exercised. **Microphone TCC consent was not
exercised end to end** — the verification machine had no interactive
session available to click "Allow" on the consent sheet, so the
permission-request code path and its bounded-wait/remediation-message
behavior were verified (fails cleanly with a specific instruction to open
System Settings), but actual granted-mic audio capture was not. This is the
single largest open item — see `STATUS.md`.

## UTF-8 on every file read/write

Unchanged guidance: transcripts mix Japanese, Chinese, Polish, and French
(see root `README.md`'s multi-language section). Check every file-I/O call
in the Mac audio path for an explicit `Encoding.UTF8` (WAV headers aside —
those are binary). Not specifically re-audited as part of this port; no
mojibake was reported during verification, but that is not the same as a
deliberate check.

## Acceptance bar — what was and wasn't met

The original bar: record ~15 s with both mic and system audio while
something plays through the speakers, read the WAV files back
independently, report real RMS/peak per track, not "it worked."

- **Met, system audio:** `system.wav`/`raw.wav`, 16000 Hz/1ch/16-bit,
  rms=0.02541 peak=0.22971 over 15.08 s — non-silent, independently
  confirmed via a Python parser and `afinfo`.
- **Not met, mic-vs-system independence:** the acceptance bar also asked for
  mic and system tracks to be confirmed as genuinely different content, not
  just each non-silent. `MeetingScribe.Audio.TestHarness` gained a
  mic-vs-system independence check (byte-identity + Pearson correlation) as
  part of this port, but a full live run pairing real mic input against
  real system playback was blocked by the same no-interactive-session
  limitation as microphone TCC consent above — not verified on macOS.
- **Not met:** a full Start → live transcript → Stop → accurate pass →
  Generate Minutes cycle has not been run on macOS.

## Process

Superseded — the PR is already open and merged. Left here for the next
platform port to reuse the pattern: open a PR, do not push straight to
`main`; record progress, blockers, environment, and measured numbers in
`STATUS.md` as you go, not only in commit messages or chat.
