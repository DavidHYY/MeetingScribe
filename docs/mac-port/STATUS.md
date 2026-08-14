# macOS Port — Status

**Backfilled note:** this file's template asked for live updates "as you
go," but the PR branch (`feat/macos-audio-capture`) was started before
`docs/mac-port/` existed on `main`, so nothing was written here during the
actual port — the real record lives in the PR's commit messages and the
root `README.md` changes it made. This file was filled in afterward,
reconciling this doc set with that PR, using only facts stated in those
commits/README — nothing here is a new measurement.

---

## Current state

**Done**, with two open items: microphone capture and the full end-to-end
cycle are implemented but not verified on real hardware (no interactive
session was available on the verification machine to grant mic TCC
consent). System-audio capture, build, UI render, and Whisper/Metal are all
verified. Merged via PR #1 into `main`.

## Last updated

`2026-08-12`, backfilled from PR #1 (`feat/macos-audio-capture`, 7 commits,
merged into `main`).

## Environment

| Field | Value |
|---|---|
| macOS version | 26.5 |
| Chip | Apple Silicon (M2) |
| .NET SDK version | 10.0.302 |

## Progress

| Item | Status | Notes |
|---|---|---|
| Repo clones and builds for `net10.0` on macOS (`dotnet build MeetingScribeCS.sln -c Release`) | done | 0 errors, 0 warnings, 7 projects (macOS build only builds 7 of the 8 solution entries — `net10.0-windows` doesn't apply on macOS). Required fixing `NETSDK1073` (NAudio.WinForms `FrameworkReference` doesn't exist on macOS SDKs) by conditioning `MeetingScribe.Audio.Windows.csproj` and `MeetingScribe.App.csproj` on `$(OS)`. |
| Whisper runtime swapped Vulkan → CoreML (or CPU) and transcription verified | done | Went with plain `Whisper.net.Runtime` (bundled Metal, `libggml-metal-whisper.dylib`), not CoreML — CoreML needs a separately generated `.mlmodelc` bundle this build doesn't produce. Metal confirmed via native log (`ggml_metal_device_init`), since `Whisper.net`'s `RuntimeLibrary` enum has no Metal case. |
| Microphone capture (CoreAudio / AVFoundation) | blocked | Code path implemented (`AVCaptureSession`/`AVCaptureAudioDataOutput`) and the permission-denied path verified (correct remediation message). Actual granted-mic capture **not verified**: no interactive session on the verification machine to click "Allow" on the TCC consent sheet. |
| System-audio capture (ScreenCaptureKit, or BlackHole fallback) | done | ScreenCaptureKit (`SCStream`), not BlackHole — needs nothing installed by the user. 15.08 s capture, rms=0.02541 peak=0.22971, non-silent, confirmed via independent Python parser + `afinfo`. |
| Separate `mic.wav` / `system.wav` / mixed `raw.wav`, 16 kHz mono | done | Fixed a real mixer bug first: `MixerLoop` truncated to `Math.Min(micChunk, systemChunk)` per read and discarded the remainder, losing ~44% of a 15 s take (8.63 s of a 15.47/15.10 s take) — mic and system push independently-sized native callback blocks on macOS (~171 vs ~320 samples/callback measured), unlike Windows' single shared pull loop. Fixed with sample-level buffering carrying remainders forward; verified over 5 live runs, `raw.wav` matched `max(mic, system)` duration every time after the fix. |
| Avalonia UI renders and is usable | done | Confirmed by screenshot: device pickers, Start button, elapsed timer, Live Transcript / Minutes / Settings tabs all render. |
| Full cycle: record → live transcript → stop → accurate pass → minutes | not started | Not attempted on macOS — same no-interactive-session limitation blocking mic verification. |

## Measured numbers

| Metric | Value |
|---|---|
| Whisper backend loaded (`BackendInfo.LoadedLibrary`) | `Metal` (`IsGpuBackend=True`) |
| Realtime factor (model size, file length) | Medium model: **9.57x** (68.96 s / 69 s multilingual clip, two runs), **6.40x** (33 s clip) |
| Comparison note | Windows/Vulkan on Intel iGPU measures 1.94x–2.80x realtime on the medium model (see repo root `README.md`). Apple Silicon + Metal measured faster, as expected — 9.57x vs 2.80x best-case Windows figure. Not a like-for-like benchmark (different hardware class, different clip), but directionally as anticipated. |
| System-audio capture (post mixer fix) | 16000 Hz, 1 ch, 16-bit; rms=0.02541 peak=0.22971 over 15.08 s, non-silent (independent Python parser + `afinfo`) |
| `raw.wav` mixer, post-fix | 5 live runs, `raw.wav` duration matches `max(mic, system)` every time (e.g. 15.44 s/15.44 s/15.16 s) |
| `StopAsync()` latency, post-fix | Under 1 s (was: every call burned the full 10 s timeout before the fix — mixer channel was only completed in `Dispose()`, which ran after the very join it was blocking) |

## Open issues

1. **Microphone capture not verified end to end.**
   - Expected: grant mic TCC consent interactively, confirm real
     non-silent, room-noise `mic.wav`.
   - Actual: verification machine has no interactive session (display
     asleep / no WindowServer able to show the consent sheet). Code path
     and the permission-denied remediation message are verified; actual
     captured audio is not.
   - Blocks: no (system-audio path and build are independently verified;
     this narrows the release-readiness claim rather than blocking the
     merge).

2. **Full Start → live transcript → Stop → accurate pass → Minutes cycle
   not run on macOS.**
   - Expected: one real end-to-end pass exercising every stage.
   - Actual: not attempted, same interactive-session limitation as #1.
   - Blocks: no, but should be the first thing verified on real
     interactive macOS hardware.

3. **Display-sleep kills ScreenCaptureKit capture — mitigated, not
   eliminated.** `msc_system_start` holds an
   `IOPMAssertionCreateWithName`/`kIOPMAssertionTypePreventUserIdleDisplaySleep`
   assertion and retries once after waking an already-sleeping display, but
   if the display is forced to sleep anyway, `SCStream` still stops
   (`didStopWithError`) — surfaced as an error via the existing
   `CaptureError` event, not silently, but the recording still ends early.
   - Blocks: no — this is documented, tested behavior (verified via
     `pmset displaysleepnow` mid-capture: stream halted cleanly, error
     reached the app, 1.78 s of real audio captured before the stop).

4. **ScreenCaptureKit has no per-device loopback selection.** Always
   captures the full system mix; `MeetingRecorderOptions.SystemAudioDeviceId`
   is validated but not honored on macOS (documented in
   `MacDeviceResolver.ValidateRender`). Not fixable within ScreenCaptureKit's
   API surface.
   - Blocks: no — same behavior gap exists conceptually with any
     post-mix-capture approach; documented rather than hidden.

## Questions for David

1. Is verifying microphone capture and the full end-to-end cycle on real
   interactive macOS hardware (open item #1/#2 above) worth a follow-up
   task, or is system-audio + build + UI verification sufficient to call
   the macOS port shippable for now?
