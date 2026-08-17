# MeetingScribe

Records a meeting (microphone + system audio as separate tracks), shows a rough
live transcript while the meeting is running, re-transcribes accurately after
the meeting with a larger Whisper model, then generates meeting minutes via a
pluggable backend (Claude, Codex, or a local Ollama model - see "Minutes
generation backends" below).

**Only minutes generation calls a model.** Recording and transcription
(Whisper, via `MeetingScribe.Whisper`) run entirely locally on this machine
and need no AI subscription, no API key, and no network access. The choice of
minutes backend is where a subscription becomes optional-or-not: pick Ollama
and the whole app runs offline; pick Claude or Codex and only that last step
needs their CLI to be installed and authenticated. The one thing the app
contacts on its own is a startup check for a newer version — no meeting
content in that request, just a version number, and it can be turned off
(see "Automatic updates" below).

## Transcribe a recording you already have

A "Transcribe a recording..." button next to Start opens a file picker
instead of starting a live capture. The file runs through the same accurate
Whisper pass and the same minutes generation a live meeting does — shared
code, not a parallel path.

- **Formats.** WAV works everywhere. MP3, M4A, and audio-only MP4 are decoded
  on Windows via Media Foundation (NAudio's `MediaFoundationReader`), which
  the project already bootstraps for the live-recording path, so nothing
  extra ships for this.
- **Why this doesn't touch the seam this README already documents:**
  `MeetingScribe.Whisper` stays plain `net10.0` with no NAudio or Windows
  dependency. Decoding lives behind `IAudioPlatform` in
  `MeetingScribe.Audio.Windows` and hands Whisper a 16 kHz mono PCM16 WAV, so
  chunking, per-chunk language switching, and the silent-lead-in skip (see
  below) all run unmodified.
- **macOS:** throws `PlatformNotSupportedException` naming the fix (convert
  to WAV, or import on Windows). WAV import still works on macOS.
  AVFoundation decoding was not attempted.
- Imported meetings are marked as such in `meta.json`, and a single-track
  file labels its transcript lines "Recording" rather than misleadingly
  "Mic".
- **Verified:** WAV, M4A, MP3, and audio-only MP4 versions of the same
  18-second clip produced identical text and timings, Vulkan GPU backend
  confirmed. Error paths return real messages for missing, empty, and
  corrupt files.
- **Not verified, said plainly:** live mic Start/Stop was not re-tested
  after this change (no microphone in the test environment), and the macOS
  path was not run (no Mac available). Neither existing code path was
  modified by this feature.

Practical effect: a phone becomes a capture device. Record a voice memo,
import the file, get minutes. No server involved.

## Live English translation

Settings → "Live Translation" turns on an English translation beneath each
non-English line of the live transcript, as the meeting runs.

It uses **Whisper's own `translate` task**, so it is local, offline and free —
no LLM, no API key, no network. Because it is Whisper-native it works on
macOS too, now that macOS audio capture is implemented (see "Cross-platform
status" below) — not independently re-measured on macOS, but nothing about
this feature is platform-specific.

- **English only.** Whisper's translate task can only output English; there is
  no translate-to-Chinese in Whisper. Off or English is the whole feature.
- Chunks already detected as English are **skipped**, so English meetings cost
  nothing extra (measured: 5.43 s off vs 5.34 s on for a 60 s English clip).
- Non-English costs roughly **2x the live pass** — it is a second decode of the
  same audio. Measured on a 300 s Japanese/Mandarin segment with the `base`
  model: 49.6 s off vs 109.3 s on.
- Translation is **strictly secondary**: it runs on its own worker with a
  bounded drop-oldest queue. If it falls behind, translations are dropped —
  never audio, never transcript segments.
- Translations are persisted per line in `live_transcript.json` with a
  `translationStatus` (`Completed` / `NotApplicable` / error).

**Accuracy caveat — read this before relying on it.** The live pass uses the
`base` model, which is fast and rough. The translation step faithfully
translates whatever `base` produced, so rough transcription becomes rough
translation. In a verification run on a Japanese meeting, a short sentence
came out as an unrelated English pleasantry, and a longer sentence collapsed
into a repeated apology bearing no relation to what was said. This is not
subtle degradation — it is confident, fluent, wrong output. (Constructed
illustration of the failure shape, not a transcript excerpt: a phrase meaning
"I'm watching" rendered as "Thank you.")

Treat the live translation as a **rough gist for following along**, not as a
record. The accurate transcript is the post-meeting pass (`small` by default),
and the minutes are generated from that, never from the live pane.

## Minutes generation backends

Settings → "Minutes Generation" lets you pick one of three, and shows live
availability (found/not found, with why) for all three so you don't pick one
that will fail when clicked:

| Backend | Requires | Notes |
|---|---|---|
| **Claude** (`claude -p`) | Claude Code CLI installed and authenticated (subscription) | Original/default behaviour, unchanged. |
| **Codex** (`codex exec`) | Codex CLI installed and authenticated (OpenAI/ChatGPT) | Runs with `-s read-only --skip-git-repo-check`: pure text generation, no shell access, no git repo required. |
| **Ollama** (local HTTP) | Nothing - the no-subscription default | Talks to a local (or LAN) Ollama server over HTTP. Server URL, model, and context window (`num_ctx`) are configurable in Settings; model is picked from the server's actual `/api/tags` list via "Refresh Models", never typed blind. |

All three share the same prompt template (Settings → "Minutes Prompt
Template") and the same output post-processing (a wrapping ```` ``` ```` code
fence around the whole response, if the model adds one, is stripped
uniformly - see `MinutesTextUtils`) - no backend-specific fork of the prompt.

**Ollama honest caveat.** On this development machine, Ollama runs on CPU
only (Intel UHD iGPU, no CUDA GPU). Real meetings for this project mix
Japanese, Mandarin, English, Polish, and French in one recording - a small
local model on CPU will be **noticeably worse** than Claude or Codex at that,
both in transcription-artifact tolerance and multi-language fidelity. This
caveat is shown directly in the Settings UI next to the Ollama picker, not
buried in a doc only. Pick Ollama for "no subscription, runs offline,
good enough"; pick Claude or Codex for "best quality on a hard multilingual
transcript."

**Ollama installed and exercised end-to-end (2026-08-11).** Installed
per-user via `winget install --id Ollama.Ollama` (v0.32.6, no admin/UAC
needed — `%LocalAppData%\Programs\Ollama`). Model: **`qwen3:8b`**
(Q4_K_M, 5.2 GB on disk) — chosen over the originally-suggested `qwen2.5:7b`
because Qwen3 is the current generation in the same size class on Ollama's
registry (checked live the same day) and Qwen3 remains strong on
Japanese/Mandarin.

Run against the real, unmodified `OllamaMinutesProvider` code path (same
harness pattern used for Claude/Codex below), on a real 435-line
mixed-Japanese/Mandarin meeting transcript: **282.4s**, and **the output is
not usable**. It ignored the requested Markdown section headers entirely,
extracted almost none of the transcript's actual content — missing specific
dates, a named option, and a budget figure that the fixed run below
recovered correctly — and instead produced generic English commentary
describing the transcript as "fragmented and low-confidence," quoting only a
handful of phrases from near the very end of the document, plus a stray
emoji and a chatty sign-off — not meeting-minutes register at all.

**Root cause identified, then fixed (2026-08-11).** `ollama ps` during that
run showed `CONTEXT 4096`: `OllamaClient`'s `/api/generate` request never set
`options.num_ctx`, so Ollama silently fell back to its own 4096-token
default regardless of the model's real 40960-token context window. The
transcript alone renders to ~11,200 characters, almost entirely CJK,
comfortably exceeding 4096 tokens — the model was answering from a truncated
tail of the document, which is exactly the phrases it echoed back. Confirmed
directly with a diagnostic-only, hand-rolled HTTP call (not the shipped code
path): resending the identical prompt with `num_ctx=16384` explicit made
`ollama ps` report `CONTEXT 16384`, and produced correctly-formatted minutes
hitting the real facts (the same specific dates, named option, and a stated
multi-day office closure that the truncated run above had missed) — but
took **996.6s (16.6 min)**, ~20x Claude's 50.8s, and still added an
attendee name that looks like transcription noise rather than a real
speaker (a fabricated-sounding Japanese honorific name that doesn't map to
any actual participant — almost certainly a whisper.cpp mis-transcription
of the noisy source audio, echoed back as if it were a person).

**Fix, shipped, not just diagnosed.** `OllamaClient.GenerateAsync` now always
sends `options.num_ctx` (`AppSettings.OllamaNumCtx`, exposed on the Settings
tab as "Context window", default **16384** — the smallest value measured to
keep this project's real transcripts in-window). Two further guards, because
raising one fixed number just moves the truncation point to a longer
meeting rather than removing it:

1. **Clamped to what the model can actually honour.** Before every call,
   `OllamaClient.GetMaxContextLengthAsync` queries `POST /api/show` and reads
   `model_info["<family>.context_length"]` (e.g. `"qwen3.context_length"`,
   keyed off `details.family` — confirmed live against a running server:
   `qwen3:8b` reports `40960`). If the model's own maximum is smaller than
   the configured value, the smaller one is sent, never a value the model
   would silently ignore or error on. This is best-effort: any failure
   (server unreachable, unexpected response shape) falls back to the
   configured value rather than blocking generation.
2. **Overflow is surfaced, not swallowed.** Before sending, the prompt
   (instructions + full transcript) is checked against the effective window
   using a character-count token estimate — adequate for this project's
   CJK-heavy transcripts (CJK runs close to 1 token/character; the estimate
   over-counts English, which only makes it more conservative). If the
   estimate exceeds the window, the returned minutes are prefixed with an
   explicit `> **WARNING...**` block naming the estimated size, the
   configured window, and whether the model's own cap was the limiting
   factor — visible both in the Settings-tab minutes preview and in the
   written `minutes.md` (same string, one write). No silent truncation path
   remains.

**Re-verified end to end post-fix (2026-08-11), same real transcript, real
`OllamaMinutesProvider` code path, live `ollama ps` sampled mid-run:**
`CONTEXT 16384` confirmed (not 4096). **1163.9s (19.4 min)**, no overflow
warning (prompt ≈12,600 estimated tokens, under the 16384 window) — up from
996.6s on the earlier diagnostic run, most likely machine-load variance, not
a regression (same model, same effective `num_ctx`). Minutes correctly
grounded: two specific dates (one in the decisions/action items section, one
in the agenda) and a named option raised earlier in the meeting were all
present and correctly attributed to the right section — the same facts the
truncated run above had missed entirely. Overflow-guard behaviour separately
verified by deliberately configuring a 512-token window against the same
transcript: the run completed and the returned minutes began with the
`WARNING` block as designed, instead of silently producing minutes from a
fraction of the meeting. Attendee-name fabrication is **not** fixed by this
change and was not attempted — out of scope, see "Net" below.

**Net: the `num_ctx` gap is fixed and shipped, with an overflow guard for
whatever the next fixed number doesn't cover.** CPU-only `qwen3:8b` remains
far slower than Claude on this hardware (19.4 min vs 50.8s, ~23x) and still
fabricates attendee names on this transcript — neither of those was in scope
for this fix and neither was attempted. "No subscription, runs offline"
remains the honest trade-off for choosing Ollama, not "as good as Claude for
free"; this fix only makes that trade-off honest about *how much of the
meeting the model actually saw*, instead of silently wrong.

**A note on Windows CLI resolution.** `claude`/`codex` on PATH are not
equally launchable: `claude` is an actual `.exe`, but `codex` (npm-installed)
ships as a `codex.cmd` batch shim with no `.exe` at all. .NET's
`Process.Start` (`UseShellExecute=false`, required for redirected stdio)
does not do the PATHEXT search a shell does for a bare command name, so a
naive `Process.Start("codex", ...)` fails with "not found" even though
`codex --version` works fine from a terminal - `CliExecutableResolver`
resolves the real path (trying PATHEXT extensions before a bare match, since
an extension-less file on Windows is not necessarily launchable at all) so
this doesn't surface as a false "Codex not available" in Settings. Relatedly,
`CodexMinutesProvider` sends the entire prompt over stdin rather than as a
command-line argument: `.cmd` execution is transparently routed through
`cmd.exe /c`, whose command-line parser cannot carry an embedded newline
inside an argument, and the real minutes prompt template is multi-line - see
`CodexMinutesProvider`'s doc comment for the reproduction.

## Cross-platform status

**macOS audio capture is implemented.** Measured on macOS 26.5, Apple M2,
.NET 10.0.302 — see below for exactly what was and was not verified.

macOS port work-in-progress lives in [`docs/mac-port/`](docs/mac-port/) —
[`README.md`](docs/mac-port/README.md) is the handoff brief,
[`STATUS.md`](docs/mac-port/STATUS.md) is the living progress/issues log.

- The UI is now **Avalonia**, targeting plain `net10.0` (`MeetingScribe.App`,
  multi-targeted as `net10.0-windows;net10.0` — see below) — cross-platform
  by construction, not WPF. The WPF -> Avalonia port is done: every feature
  (device pickers, level meters, live transcript, Minutes tab with export,
  Settings tab including the allowed-languages list, determinate progress for
  the final pass) is ported, not stubbed. The Avalonia window is confirmed to
  launch and render on macOS (verified by screenshot: device pickers, Start
  button, elapsed timer, Live Transcript / Minutes / Settings tabs).
- Audio capture is behind a platform-neutral interface, **`IAudioPlatform`** /
  **`IMeetingRecorder`**, defined in `MeetingScribe.Audio.Abstractions`
  (plain `net10.0`, no OS-specific code at all). Two implementations exist:
  - `MeetingScribe.Audio.Windows` (`net10.0-windows`) — the real NAudio/WASAPI
    recorder, unchanged in logic from the original WPF build, just moved
    behind the interface. This is what ships and is verified working on
    Windows.
  - `MeetingScribe.Audio.Mac` (plain `net10.0`) — a real implementation:
    `MacAudioPlatform` + `MacMeetingRecorder`. Microphone via AVFoundation
    `AVCaptureSession`; system audio via **ScreenCaptureKit** (`SCStream`).
    Both paths are resampled to 16 kHz mono 16-bit PCM via
    `AVAudioConverter` (not naive decimation). ScreenCaptureKit was picked
    over a virtual device like BlackHole because it needs nothing installed
    by the user; the trade-off is that the `SCStreamOutput` protocol is
    impractical to hand-roll from pure C#, so the implementation sits behind
    a small Objective-C helper dylib (`MeetingScribe.Audio.Mac/native/`),
    built by an MSBuild target that runs `clang` on macOS hosts only, and
    P/Invoked from C# with `[UnmanagedCallersOnly]` callbacks.
- `MeetingScribe.App.csproj` multi-targets `net10.0-windows;net10.0`
  specifically so this seam is *proven*, not just documented: every build of
  this solution compiles the App project against **both** the Windows and
  Mac providers (conditional `ProjectReference`s per TFM), so a break in the
  abstraction boundary fails the build immediately instead of surfacing only
  when someone tries a real Mac build. The App project has **no direct
  reference to NAudio** — only to `MeetingScribe.Audio.Abstractions` (always)
  and whichever platform provider matches the active TFM.
- **Build fix that made this possible:** NAudio pulls in NAudio.WinForms,
  which needs the `Microsoft.WindowsDesktop.App.WindowsForms`
  FrameworkReference and fails on macOS SDKs with `NETSDK1073`.
  `MeetingScribe.Audio.Windows.csproj` and `MeetingScribe.App.csproj` now
  condition that reference on `$(OS)`, so the same solution builds on both
  platforms. Windows behaviour is unchanged. Clean-slate
  `dotnet build MeetingScribeCS.sln -c Release` on macOS: **0 errors, 0
  warnings, 7 projects**.

**Measured on macOS 26.5 / Apple M2 / .NET 10.0.302:** a 15.08 s
system-audio capture, read back off disk with an independent Python parser
and `afinfo` — 16000 Hz, 1 ch, 16-bit, **rms=0.02541, peak=0.22971,
non-silent**.

**Honest caveats — read before relying on this:**

1. ScreenCaptureKit needs an awake, shareable display. The recorder holds an
   `IOPMAssertionCreateWithName` /
   `kIOPMAssertionTypePreventUserIdleDisplaySleep` assertion for the
   duration of a recording and tries once to wake an already-sleeping
   display, but if the display is forced to sleep the `SCStream` stops with
   `didStopWithError` and the error is surfaced rather than writing silence.
2. A **muted Mac produces a completely silent system-audio track** —
   ScreenCaptureKit captures the post-mix stream. The app warns on start when
   the default output device is muted or at zero volume. This is inherent to
   the approach, not a bug.
3. ScreenCaptureKit has **no per-device loopback selection** — it always
   captures the full system mix, unlike WASAPI on Windows, so the
   system-audio device picker is validated but not honoured on macOS.
4. **Microphone capture on macOS is not yet verified end to end.** The code
   path and the permission-denied error are verified, but granting the
   microphone TCC consent needs someone to click Allow in an interactive
   session, which has not happened yet. Do not read this section as proof
   mic capture works — only that the system-audio path and the build do.
5. A full Start → live transcript → Stop → accurate pass → Generate Minutes
   cycle has **not** been run on macOS yet, for the same reason.

## What is verified working (Windows)

- Builds clean on **.NET 10** (`dotnet build MeetingScribeCS.sln -c Release`
  — 0 warnings, 0 errors, all 8 project builds including both App TFMs).
- Avalonia window launches, renders, and binds real data end-to-end
  (device pickers, model-benchmark descriptions, status bar) — verified via
  UI Automation against a running build (device pickers, Start/Stop, tabs,
  status bar all present and bound correctly).
- Per-user install with **no admin rights** (`dist/install.ps1`, not part of
  this repo — see Packaging below): copies to
  `%LocalAppData%\Programs\MeetingScribe`, Start Menu shortcut, HKCU
  uninstall entry. No elevation anywhere.
- Whisper runs on the Intel iGPU via **Vulkan**, measured **2.80x realtime**
  on the medium model on a real 26-minute meeting recording (Intel UHD, flash
  attention on, chunked file transcription — see below).
- Minutes generation verified against a real, mixed Japanese/Mandarin meeting
  transcript for **all three** backends. **Claude**, 50.8s, grounded and
  correctly-attributed (names, dates, decisions matching the transcript, not
  generic filler). **Codex**, 22.0s, likewise grounded — this run also caught
  and fixed the two real bugs documented under "Minutes generation backends"
  above (PATHEXT resolution, stdin-vs-argv prompt delivery for a
  `.cmd`-shimmed CLI on Windows). **Ollama** (`qwen3:8b`, Q4_K_M, CPU-only) —
  originally 282.4s and **not usable** (ignored the requested format, missed
  essentially every real fact) because `OllamaClient` never set
  `options.num_ctx`, silently truncating the transcript at Ollama's
  4096-token default. **Fixed 2026-08-11** (explicit `num_ctx`, clamped to
  the model's reported max, with an overflow warning if a transcript still
  exceeds it — see "Minutes generation backends" above): re-verified against
  the same real transcript through the real `OllamaMinutesProvider` code
  path, **1163.9s (19.4 min)**, `ollama ps` confirmed `CONTEXT 16384` (not
  4096), minutes correctly grounded (the same specific dates and named
  option detailed above). Still ~23x slower than Claude and still
  fabricates attendee names on this transcript — not in scope for this fix.
- Microphone and system audio are captured as **genuinely separate WAV
  tracks** (`mic.wav`, `system.wav`) plus a mixed track (`raw.wav`), not a
  single merged recording — reverified after the port via
  `MeetingScribe.Audio.TestHarness` against the real production
  `MeetingScribe.Audio.Windows` provider: `mic.wav` rms=0.00576 peak=0.03030
  (10.15s, live room noise), `system.wav` rms=0.42453 peak=0.90854 (6.02s, a
  played 440Hz test tone via WASAPI loopback), `raw.wav` (mixed) rms=0.32659
  peak=0.91455 (9.92s) — all three flagged NON-SILENT by the harness's own
  independent read-back analysis.

## Fixed: repetition loop on long recordings

The after-meeting accurate pass (stage 2, the larger-model re-transcription)
used to degenerate into whisper.cpp repeating a single phrase over and over on
long recordings. On a real 26-minute meeting recording, 373 of 513 segments
(72.7%) were one repeated phrase, and language detection locked onto the
bogus code `nn` instead of the recording's actual language.

Root cause: a single long `ProcessAsync()` call over the whole file lets
whisper.cpp's decoder latch onto a wrong phrase on quiet/ambiguous audio, feed
that phrase back into the next decode window as "previous text" context
(`whisper.cpp` per-window context rebuild), and re-seed its own hallucination
indefinitely — the loop does not self-correct within one call.

**Fix, implemented and measured:** chunk the file internally (target/max
chunk length shared with the live-streaming path) and issue one
`ProcessAsync()` call per chunk with `no_context` set, so context resets often
enough that one bad chunk cannot poison the rest of the file. Measured on the
same 26-minute recording: max repeated phrase dropped from 72.7% to **3.8%**
(10/260 segments), language detection correctly locked to **`ja`** (was
`nn`), the meeting's main recurring topic keyword (a Japanese word meaning
"quote/estimate," tracked throughout this document — see "Multi-language
meetings" below) appears 8 times as expected, and the run completed at
**2.80x realtime**.
(This measurement predates per-chunk language switching — see "Multi-language
meetings" below for how language selection actually works today; "locked to
ja" described the whole-file behaviour of that earlier design, not the
current one.)
`dotnet build MeetingScribeCS.sln -c Release` is clean (0 warnings, 0 errors).
See `MeetingScribe.Whisper/WhisperTranscriber.cs` (`TranscribeFileAsync`,
`AudioChunkPlanner`) and `MeetingScribe.Whisper/WhisperTranscriberOptions.cs`
(`NoContext`) for the fix and the full root-cause writeup in the doc comments.

## Fixed (partially): near-silence hallucination

A residual defect remained after the chunking fix above: whisper.cpp
fabricates YouTube-outro-style boilerplate ("おやすみなさい", "ご覧いただき
ありがとうございます") over near-silent audio, because it reports
`no_speech_probability = 0.000` even on these fabricated segments — the
built-in no-speech/entropy/logprob guards never fire, so no decoder flag
fixes it. On the real 26-minute recording, ~19 of 260 segments (~7%) were
this kind of fabrication, concentrated in the near-silent first few minutes
before the meeting starts (measured RMS ~0.02-0.03 there vs. ~0.05-0.10+
during real speech).

**Fix:** `WhisperTranscriberOptions.SkipSilentLeadIn` (default on) skips a
near-silent *lead-in* at the start of the file — never sends it to
whisper.cpp at all — using a threshold relative to the file's own median
chunk RMS (`SilentLeadInRatio`, default 0.65), not a fixed absolute level, so
it self-adjusts to each recording's own gain/mic setup.

This is deliberately a **lead-in-only** skip, not a file-wide silence gate.
Measuring the real recording's chunk energy directly showed real content can
be just as quiet as the pre-meeting silence — a brief "OK" acknowledgement
mid-meeting, and continuous soft-spoken dialogue near the very end (around
25:03, close to the file's actual last segment at 26:14) — both measured
RMS 0.017-0.023, overlapping the 0.021-0.038 measured for the actual
pre-meeting silence. A threshold applied anywhere in the file, at any ratio
that also fully covered the lead-in, would have deleted that real content.
Restricting the skip to a contiguous run from the start of the file makes
that structurally impossible.

Measured effect on the same 26-minute recording: **253 segments** (was 260,
-7 — the entire near-silent lead-in, 0:00-2:20, is now skipped rather than
transcribed). Boilerplate matching the two named phrases dropped from ~19 to
**11** (10x 「おやすみなさい」, 1x 「ご覧いただきありがとうございます」);
every occurrence of 「見てくれてありがとう」 and 「ご視聴ありがとうございま
した」 in the lead-in is gone (2→0 and 1→0). No real content was lost: the
recurring topic keyword still appears 8 times, the last segment still lands
at 26:14-26:16
(a short closing pleasantry), the quiet-but-real dialogue at 25:03 is intact, and language
stayed `ja`. Realtime factor on this run was 2.52x, not the 2.80x baseline —
mechanically that's surprising (skipping ~140s of lead-in is *less* whisper.cpp
work than the baseline run did, so it should be equal or faster), so the drop
looks like machine-load variance on this run rather than a regression from
the change itself, but that is not independently confirmed by a repeat run.

A second, separate cluster of the same boilerplate phrase (「おやすみなさい」
x10, 2:44-3:04) is **not** removed by this fix — it sits inside a chunk with
real acoustic energy (avg RMS ~0.07-0.08, not near-silent; likely people
arriving/greeting before the meeting formally starts), so it is a
decode-quality problem, not a silence problem, and is out of scope for an
energy gate. See `MeetingScribe.Whisper/AudioChunkPlanner.cs`
(`CountLeadingSilentChunks`) for the full reasoning and the measured numbers
behind the default threshold.

## Multi-language meetings: per-chunk switching, not a single lock

Real meetings for this project mix Japanese, English, Mandarin, Polish and
French — sometimes several in one call, with live interpreting between
sides. An earlier version of the language-detection fix (see "Fixed:
repetition loop" above) solved a real problem the same way many single-
language transcription tools do: sample several speech-bearing chunks,
majority-vote, then lock that one language for the whole file via
`ChangeLanguage()`. That fixed real flapping (per-chunk auto-detect settling
on `ko`/`nn` — languages nobody spoke) and a real mistranscription (forcing
the first speech chunk's language, `en` from a brief aside, turned genuine
Japanese dialogue into fabricated English). But locking to one language is
wrong for a genuinely multilingual meeting — it mangles every other language
in the recording.

**Current design** (`WhisperTranscriber.TranscribeFileAsync` /
`WhisperTranscriberOptions`):

1. **`AllowedLanguages`** (default `ja,en,zh,pl,fr`) is the set of languages
   the active decode language is allowed to switch into. Null/empty
   disables the restriction (any language whisper.cpp detects is eligible).
2. **Per-chunk detection, constrained to that set.** Each chunk (target 15s
   / cap 25s) gets its own fast language-ID pass — not a full decode. A
   detection outside the allowed set (the historical `ko`/`nn` noise) never
   switches the active language; the file keeps decoding in whichever
   in-set language is already active. That detection is not thrown away,
   though: it is counted per language on
   `TranscriptionResult.OutOfSetLanguageDetections`, so a language that
   isn't in the list yet shows up as a visible count instead of silently
   being mistranscribed as one of the allowed languages forever.
3. **Hysteresis, confirmed by looking ahead.** `LanguageSwitchConfirmationChunks`
   (default 2) requires that many chunks in a row to agree on the same new
   in-set language before switching — but confirmation looks *ahead* of the
   candidate chunk, not behind it, so a genuine sustained switch takes
   effect starting at the first chunk of the new language, at zero added
   lag. Only a false alarm (a single odd chunk that doesn't hold up) costs
   anything, and what it costs is staying on the old language for that one
   chunk — a real switch is never delayed by waiting for it.
4. **Seed, not lock.** The existing majority-vote-over-speech-chunks logic
   still picks a starting language (restricted to the allowed set), but it
   is only a starting point for the per-chunk logic above, which can
   override it starting from the very first chunk if warranted.
5. **Recorded, not just applied.** Every confirmed switch is logged on
   `TranscriptionResult.LanguageSwitches` (timestamp, from-language,
   to-language). Per-segment `Language` (already existed on
   `TranscriptionSegment`, now populated per this scheme) reflects the
   language actually used to decode that segment, and already flows through
   to `transcript.json`.

**Hard limit — honestly stated, not glossed over:** whisper.cpp assigns
exactly one language to an entire decode window (one chunk). This scheme
changes language at chunk boundaries, so it handles passage-level
code-switching (a few minutes of Japanese, then a stretch of Mandarin
interpreting, then back). It does **not** fix intra-sentence mixing within a
single chunk, and cannot: this is a whisper.cpp architecture limit, not a
bug in this code. It is not hypothetical, either — the regression recording
used throughout this document contains real examples of a speaker switching
languages mid-utterance, mixing Mandarin sentence structure with Japanese
words and clauses inside a single sentence. (Illustrative example
constructed for this document, not a transcript excerpt: 「所以我們的前提是
プランA」, "so our premise is Plan A", opens in Mandarin and finishes with a
Japanese loanword; 「因為預算はもう決まっている」, "because the budget is
already decided," opens with a Mandarin conjunction and finishes as a full
Japanese clause.) A chunk containing that kind of switch decodes entirely in
whichever language wins the chunk; the other language's words inside it
degrade to best-effort noise in the winning language.

**Configurable, and meant to grow.** `AllowedLanguages` is not a closed list.
It is exposed on the Settings tab (`MainWindow.xaml`, "Language" group —
comma-separated codes, e.g. `ja,en,zh,pl,fr`) via `AppSettings.AllowedLanguages`,
so a language can be enabled without a rebuild. If `OutOfSetLanguageDetections`
on a real meeting's result shows a language repeatedly, that is the signal
to add it to the list rather than leave it silently mistranscribed.

**Regression test.** `MeetingScribe.Whisper.TestHarness` re-run on the same
real 26-minute recording used throughout this document, medium model:

| metric | known-good baseline (old single-lock design) | switching on (defaults, this design) | control run (switching disabled, same code) |
|---|---|---|---|
| segments | 253 | 325 | 253 |
| topic keyword A occurrences (Japanese; the meeting's main recurring subject, genericized here — see note below) | 8 | 7 | 8 |
| topic keyword A – root form occurrences (incl. inflected variants) | not measured | 9 | 10 |
| topic keyword B occurrences (Mandarin word for the same concept) | not measured | **13** | **0** |
| unrelated control-word occurrences (off-topic sanity check, should stay 0) | 0 | 0 | 0 |
| last segment | ~00:26:14.600, short closing pleasantry | 00:26:14.600–00:26:16.600, short closing pleasantry | 00:26:14.600–00:26:16.600, short closing pleasantry |
| dominant language (by segment count) | ja | zh (154 ja / 157 zh / 14 en) | ja |
| realtime factor | 2.5x–2.8x | 1.94x (2.37x on a second run — see machine-load-variance note above) | 1.17x* |
| confirmed language switches | n/a (locked) | 8 | 0 |

\* Control run sets `LanguageSwitchConfirmationChunks` to an extreme value so
no switch can ever confirm within the file — same code path, behaviourally
equivalent to the old single-lock design. It reproduces the known-good
baseline exactly (253 segments, keyword A 8x, `ja`, 0 switches), which confirms
the chunking/`NoContext`/lead-in-skip pipeline this feature was built on top
of is unchanged, and isolates every difference in the switching-on row to
the language-switching feature itself. Its 1.17x realtime figure is an
artifact of that extreme value (the look-ahead confirmation loop scans an
entire same-language run before giving up on an unconfirmable candidate) and
is not representative of real usage — nobody would configure
`LanguageSwitchConfirmationChunks` that high.

**The original bar for this test was "keyword A occurrences must stay >= 8."
That bar was retired as an invalid metric, not relaxed for convenience.**
Keyword A is a Japanese word for the meeting's main recurring topic
(genericized in this document to avoid quoting the real meeting's business
vocabulary; the actual word is an ordinary Japanese noun meaning
"quote/estimate"). The old single-lock baseline hit exactly 8 by
force-decoding the entire file as Japanese, including a ~6-minute span
(16:52–22:57) that is genuinely Mandarin: switching on correctly recognizes
that span as Mandarin instead, and the same span contains 13 occurrences of
keyword B — the Mandarin word for the same concept — plus fluent, on-topic
discussion in that language, not garbled noise. The control run above proves
this directly: forcing the same audio through Japanese-only decoding (0
occurrences of keyword B, matching the old baseline's method exactly) is the
only way to get keyword A back to 8. A single-language keyword count cannot
measure the quality of a bilingual transcript — by construction it rewards
forcing everything into one language, which is the exact defect this
feature exists to fix. Holding that bar would have meant preferring the bug
over the fix. Total topic-relevant coverage (keyword-A-root + keyword B) is
22 with switching on vs. 10 with it off: the underlying content is not
lost, it is rendered in whichever language was actually spoken.

Two more effects are expected consequences of correct per-chunk switching,
not regressions: segment count rises 253→325 (Mandarin's faster, shorter
back-and-forth exchanges segment differently than a single forced-Japanese
decode of the same audio would have), and the reported dominant language
flips ja→zh by raw segment count (154 ja / 157 zh / 14 en — near-even, and a
segment-count vote, not a duration-weighted one; `DetermineLanguage`'s
existing majority-vote heuristic was not changed by this work and is a
plausible follow-up if duration-weighting is wanted later).

Realtime factor drops from the 2.5x–2.8x baseline to 1.94x (measured twice:
1.94x and 2.37x across two runs — see the machine-load-variance note earlier
in this document), from the added per-chunk language-ID pass (not a full
decode, but real cost run once per ~15–25s chunk instead of ~7 calls for the
whole file). That is an acceptable cost for correctness on multilingual
audio, and the run is still comfortably faster than real time; recorded here
so it is not a surprise later.

**Out-of-set detections observed on this same real recording:** `ko: 2,
ms: 1, th: 1` chunks — the historical `ko`/`nn` noise-flapping languages,
correctly never switched to, and now visible on
`TranscriptionResult.OutOfSetLanguageDetections` instead of silently
discarded. This is the surfacing mechanism (see point 2 above) working as
designed.

**What was not verified:** whether the missing 8th keyword-A occurrence
represents a real, brief Japanese aside inside the 6-minute Mandarin-labeled
chunk that whisper.cpp's one-language-per-window limit swallowed, or a
Japanese-shaped hallucination of Mandarin audio in the old baseline that is
now correctly gone. The keyword-B / keyword-A-root counts above support the
second reading, but this has not been confirmed by listening to the actual
audio at 16:52–22:57 — treat it as ~70% confidence, inference from text
plausibility only, not a verified fact.

See `MeetingScribe.Whisper/WhisperTranscriber.cs` (`TranscribeFileAsync`,
`ResolveChunkLanguage`, `GetChunkLanguage`, `DetectDominantLanguage`) and
`MeetingScribe.Whisper/WhisperTranscriberOptions.cs` (`AllowedLanguages`,
`LanguageSwitchConfirmationChunks`) for the implementation and full
doc-comment reasoning.

## Whisper on macOS

`Whisper.net.Runtime.Vulkan` is Windows-only and is now conditioned on host
OS; macOS gets plain `Whisper.net.Runtime`, which bundles
`libggml-metal-whisper.dylib` for macos-arm64. Measured on Apple M2 with the
medium model: **9.57x realtime** (69 s clip) and **6.40x realtime** (33 s
clip), backend reported as Metal.

CoreML was deliberately not used — it needs a separately generated
`ggml-<model>-encoder.mlmodelc` bundle (Python + coremltools + Xcode) that
this build does not produce.

Whisper.net's `RuntimeLibrary` enum has no Metal value, so macOS previously
mis-reported `Cpu`/non-GPU; backend detection now infers Metal from the
native log.

## Project layout

| Project | What it does |
|---|---|
| `MeetingScribe.App` | Avalonia UI + app-level orchestration, multi-targeted `net10.0-windows;net10.0`: `MeetingSessionController` drives the 3-stage pipeline (record -> live rough transcript -> accurate re-transcription -> minutes) against `IMeetingRecorder`/`IAudioPlatform`. Minutes generation is pluggable behind `IMinutesProvider` (`Services/Minutes/`) - `ClaudeMinutesProvider` (`claude -p`), `CodexMinutesProvider` (`codex exec`), `OllamaMinutesProvider` (local HTTP) - see "Minutes generation backends" above. `TranscriptWriter`/`TranscriptMerger` produce `transcript.txt`/`.vtt`/`.json`, `SettingsStore` persists JSON settings under `%LocalAppData%\MeetingScribeCS`. `Bootstrap/AudioPlatformProvider.*.cs` picks the Windows or Mac audio provider per TFM. |
| `MeetingScribe.Audio.Abstractions` | Plain `net10.0`, no platform code. The seam: `IMeetingRecorder`, `IAudioPlatform`, and every platform-neutral model type (`AudioDeviceInfo`, `AudioLevel`, `AudioSourceKind`, `MeetingRecorderOptions`, `AudioDeviceNotFoundException`, the `Audio*EventArgs` types). |
| `MeetingScribe.Audio.Windows` | `net10.0-windows`. The real NAudio/WASAPI-based recorder (`MeetingRecorder`, `AudioDeviceEnumerator`, `WindowsAudioPlatform`). Captures mic and system-audio (loopback) as independent tracks plus a mixed track; device enumeration and resolution. This is what ships today. |
| `MeetingScribe.Audio.Mac` | Plain `net10.0`. `MacAudioPlatform` + `MacMeetingRecorder` - microphone via AVFoundation `AVCaptureSession`, system audio via ScreenCaptureKit (`SCStream`), both resampled to 16 kHz mono 16-bit PCM via `AVAudioConverter`. Backed by an Objective-C helper dylib under `native/` built by an MSBuild `clang` target on macOS hosts, P/Invoked with `[UnmanagedCallersOnly]` callbacks. See "Cross-platform status" above for what is and isn't verified. |
| `MeetingScribe.Audio.TestHarness` | Console harness (`net10.0-windows`, references the Windows provider directly): lists audio devices, records N seconds, reports per-track RMS/peak so a silent track is caught immediately without opening the full app. `--system-device <name-substring>` picks a specific render device to loopback instead of the OS default - useful when the default render endpoint isn't wired to real speakers. |
| `MeetingScribe.Whisper` | Whisper.net wrapper. Model download/caching (`ModelManager`), whole-file and streaming transcription (`WhisperTranscriber`, `StreamingWhisperSession`), backend proof (`BackendInfo` — confirms whether Vulkan/GPU actually loaded vs. silent CPU fallback). Untouched by the Avalonia/audio-abstraction port - already cross-platform. |
| `MeetingScribe.Whisper.TestHarness` | Console harness: loads a model, transcribes a WAV file, reports realtime factor, backend, and a repetition-loop check (flags if one segment's text dominates the transcript). |

`MeetingScribeCS.sln` ties all eight together (`MeetingScribe.App` counts as
one project but builds two TFMs).

## Build instructions

Requires the .NET 10 SDK.

```
dotnet build MeetingScribeCS.sln -c Release
```

Run the app (`net10.0-windows` on Windows, `net10.0` on macOS - both TFMs have a real audio provider; see `installer/README.md` for the macOS `.dmg`):

```
dotnet run --project MeetingScribe.App -c Release -f net10.0-windows
```

Run a harness directly, e.g.:

```
dotnet run --project MeetingScribe.Audio.TestHarness -c Release -- --seconds 10
dotnet run --project MeetingScribe.Whisper.TestHarness -c Release -- --audio path\to\file.wav --model-size Medium
```

## Whisper models are not committed

Ggml model files (e.g. `ggml-medium.bin`, hundreds of MB to a few GB
depending on size) are **downloaded on first run**, not bundled with the app
or checked into this repo. `MeetingScribe.Whisper/ModelManager.cs` downloads
from Hugging Face into a local cache directory (default
`%LocalAppData%\MeetingScribeCS\models`) the first time a given model size is
requested, and reuses the cached file after that.

## Packaging

A Windows installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php).
The source lives in [`installer/`](installer/) — `MeetingScribe.iss` plus a
one-command `build-installer.ps1` that publishes the app and compiles the
setup. Build output goes to `dist/`, which is git-ignored: it is generated,
not source.

`build-installer.ps1` also emits `SHA256SUMS.txt` next to the installer.
That sidecar is what the in-app updater (see "Automatic updates" below)
verifies a downloaded installer against, so it needs to be attached to the
GitHub release alongside the installer, not just built locally.

The installer is **per-user** — it installs to
`%LocalAppData%\Programs\MeetingScribe`, needs no administrator rights, and
triggers no UAC prompt. Uninstalling leaves `%LocalAppData%\MeetingScribeCS`
alone, so your settings and the downloaded Whisper models (potentially
several GB) survive.

**The binary is not code-signed.** There is no code-signing certificate for
this project, so Windows SmartScreen will show "Windows protected your PC"
on first run of a downloaded installer; you have to click More info → Run
anyway. That is the honest state of it, not something to be talked around —
if you would rather not, build from source with the instructions above.

## Automatic updates

On startup the app asks the GitHub Releases API for the latest tag and
compares it against its own assembly version. If there's a newer one, it
downloads the installer, verifies it, launches it, and exits. The in-place
upgrade path itself (installer over an existing install) was already
verified independently of this feature, so the updater's own job is just to
notice and fetch — verification is what makes that fetch safe to run
unattended.

**Why verification is mandatory, not optional:** this project's builds are
not reproducible — identical inputs produce a different SHA-256 on every
compile, because the PE header embeds a build timestamp. A hash can
therefore never be committed to the repo as a known-good value; it can only
come from the actual artifact that was uploaded to a release. That's what
`SHA256SUMS.txt` is for (see "Packaging" above) — generated at build time,
attached to the release, checked by the updater against the file it just
downloaded.

- A hash mismatch is a loud, visible refusal, never a silent skip.
- A release published without a `SHA256SUMS.txt` sidecar is never run
  unverified — the app points at the release page instead and lets you
  install it yourself.
- Quiet by design otherwise: no network, a DNS failure, GitHub being down,
  or being rate-limited all resolve to silence on the startup path — no
  popup, no log spam. Only clicking "Check now" explicitly reports "up to
  date" or a real error.
- It will not relaunch the app mid-recording.
- `CheckForUpdatesOnStartup` is a Settings checkbox, default on.

**Privacy note, stated plainly because it matters:** the central claim of
this README is that recording and transcription never leave your machine.
The startup update check is the one network call the app makes on its own —
it sends nothing but a version number to GitHub's release API, and it can be
turned off in Settings.

**Version handling.** `Directory.Build.props` at the repo root is the single
source of the version number. It feeds every assembly, the installer
filename, Inno Setup's `AppVersion`, and the updater's own runtime version
comparison — one number, not four places to keep in sync by hand.

## Application icon

The mark is a waveform resolving into lines of text — audio on the left
becoming a document on the right, which is what the app does.

| File | What it is |
|---|---|
| `assets/icon.svg` | Editable master. Change this, then regenerate the rest. |
| `assets/icon.ico` | Multi-resolution: 16, 24, 32, 48, 64, 128, 256. |
| `assets/icon-256.png`, `assets/icon-512.png` | For docs, installers, and a future macOS `.icns`. |
| `tools/generate_icons.py` | Rasterises the SVG into the `.ico` and PNGs. |

Wired in two independent places, both needed:

- `<ApplicationIcon>` in `MeetingScribe.App.csproj` embeds it as a PE resource,
  which is what Explorer and the Start-menu shortcut show. This only applies to
  the `net10.0-windows` target.
- `Icon="avares://MeetingScribe.App/Assets/icon.ico"` on the window is what
  Avalonia actually reads at runtime for the title bar and Alt-Tab, on every
  platform.

**Design constraint, if you replace it:** it has to stay legible at 16×16 —
that is the taskbar size and where most icons fall apart. Design at 16px first
and scale up. Generate every size in the `.ico` from the SVG; a single-size
`.ico` upscaled by Windows looks poor in Explorer and Alt-Tab.

macOS will additionally need an `.icns` when that port happens; the 512px PNG
is the starting point.

## License

MIT. See [`LICENSE`](LICENSE).

This is an independent personal project, not an official product of any
employer.
