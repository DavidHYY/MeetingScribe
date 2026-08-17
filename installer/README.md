# MeetingScribe installer

Inno Setup replacement for the hand-rolled `dist\install.ps1` /
`dist\uninstall.ps1` pair. Same install behaviour (per-user,
`%LocalAppData%\Programs\MeetingScribe`, no admin, no UAC prompt), packaged
as one double-click `.exe`.

## macOS

`installer/build-dmg.sh` (bash, `set -euo pipefail`, macOS-host-only) does,
in order:

1. `dotnet publish MeetingScribe.App -c Release -f net10.0 -r osx-arm64 --self-contained true`
   — this also triggers `CreateMacOSAppBundle`, the macOS-host-only MSBuild
   target in `MeetingScribe.App.csproj` that turns the publish output into
   an ad-hoc-signed `MeetingScribe.app`.
2. `hdiutil create` packages that `.app` plus an `/Applications` symlink
   into a drag-install `.dmg`.
3. Prints the output path, size, and SHA-256.
4. Appends one line to `dist/SHA256SUMS.txt` — same `<sha256>  <filename>`
   format as the Windows sidecar (see "SHA256SUMS.txt must be attached to
   every GitHub Release" below).

Output: `dist/MeetingScribe-1.1.2-osx-arm64.dmg` (`dist/` is git-ignored,
same as the Windows installer).

**Unsigned, not notarized** — ad-hoc `codesign` only, no Apple Developer
certificate involved or required. First launch on a machine that downloaded
the `.dmg` shows Gatekeeper's "Apple could not verify this app is free of
malware" / unidentified-developer warning; the user must right-click the app
→ Open once (or System Settings → Privacy & Security → Open Anyway).

Requires an Apple Silicon Mac (`osx-arm64`) with the .NET 10 SDK; not tested
on Intel Macs or on macOS versions older than 26.5.

## Build

```powershell
.\installer\build-installer.ps1
```

Requires Inno Setup 6 installed at
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` (not on PATH; the script
resolves the full path itself and fails loudly if it's missing).

This does, in order:

1. `dotnet publish MeetingScribe.App -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true`
2. `ISCC.exe installer\MeetingScribe.iss`
3. Prints the output path, size, and SHA-256.
4. Writes `dist\SHA256SUMS.txt` — one line, `<sha256>  <installer-filename>`,
   hashing the exact `.exe` this run just produced.

## Output

`dist\MeetingScribe-Setup-1.1.0-win-x64.exe` — `dist\` is git-ignored, so the
installer artifact itself is never committed; only `installer\*.iss` /
`*.ps1` (this build recipe) is.

## SHA256SUMS.txt must be attached to every GitHub Release

The in-app updater (`MeetingScribe.App\Services\Update\UpdateChecker.cs`)
downloads `SHA256SUMS.txt` alongside the installer from the release and
refuses to run anything that doesn't match it. **This file must be uploaded
as a second release asset next to the installer `.exe` for every release, or
the in-app updater will not auto-install that release** — it will tell the
user it can't verify it and point them at the release page instead, rather
than run an unverified binary. Builds of this project are not reproducible
(the PE header embeds a compile timestamp), so a hash checked into source or
docs would never match a real download — the sidecar this script writes,
from the artifact actually being shipped, is the only trustworthy source.

## Unsigned binary — read this before sending it to anyone

There is no code-signing certificate for this build. Both the installer
`.exe` and the app `.exe` inside it are unsigned. That means:

- Windows SmartScreen will show **"Windows protected your PC"** the first
  time someone downloads and runs the installer. This is expected, not a
  bug — do not tell a recipient it won't happen.
- To proceed: **More info → Run anyway.**
- Some browsers/AV may also flag the download itself on first sight (an
  unknown, unsigned binary with no reputation). Same cause, same fix.

The actual fix is Authenticode code-signing (EV cert bypasses SmartScreen
immediately, OV builds reputation over time — both cost money, both are a
PI/cost decision, not something this build can add on its own). Until that
exists, ship this installer knowing the first-run warning is part of the
experience, not a defect to explain away.

## What the installer does and does not touch

- Installs to `%LocalAppData%\Programs\MeetingScribe` — no admin rights, no
  `Program Files`, no `HKLM`.
- Start Menu shortcut always; Desktop shortcut is an unchecked opt-in task
  during install.
- Uninstall removes the install folder, shortcuts, and the HKCU uninstall
  entry. It deliberately does **not** touch
  `%LocalAppData%\MeetingScribeCS` (settings + downloaded Whisper models,
  potentially several GB) — that folder is outside `{app}` and Inno's
  default uninstall never reaches it. See the comment at the bottom of
  `MeetingScribe.iss`.
- Whisper ggml model files are not bundled; the app downloads them to
  `%LocalAppData%\MeetingScribeCS\models` on first use.

## AppId

`MeetingScribe.iss` hardcodes a fixed GUID as `AppId`. **Never regenerate
it on a future build** — it's how Windows/Inno recognize an upgrade of "the
same product" vs. a second, parallel install. Changing it would orphan
every existing install's uninstall entry.

## Migrating from the old `install.ps1`/`uninstall.ps1`

If a machine already has MeetingScribe installed via the old PowerShell
installer, running this installer over it is safe: `[Code]` in
`MeetingScribe.iss` detects and removes the old installer's HKCU
"Installed apps" entry and its leftover `uninstall.ps1` in the install
folder during install, so Add/Remove Programs ends up with exactly one
MeetingScribe entry (Inno's), not two pointing at the same folder. Verified
2026-08-14: seeded a fake old HKCU key + old `uninstall.ps1`, ran the new
installer silently, both were gone afterward and the new entry installed
correctly.

## Real measured numbers (2026-08-14 build, 1.1.0)

Exact byte counts and hashes below are a snapshot of one specific build and
will not match the next rebuild even with zero code changes (LZMA output
size shifts a few bytes run to run). Do not treat this section as the
authoritative hash source for a given release — that's what the GitHub
Releases page asset listing is for; a stale hash checked into a doc that
nobody remembers to update on every bump is worse than no hash at all. This
section stays for the qualitative facts (what's excluded and why, what was
verified) which don't change per-rebuild the way the byte count does.

- Installer: `MeetingScribe-Setup-1.1.0-win-x64.exe`, 49,650,825 bytes
  (47.4 MB).
- Installed footprint: 212,072,054 bytes (202.2 MB), 11 files — down from
  an earlier 360.7 MB / 23-file build that shipped debug symbols (`*.pdb`,
  ~100 MB) and a Linux-x64 Vulkan runtime (`runtimes\vulkan\linux-x64\*.so`,
  ~58 MB) that Windows never uses. Both are now excluded: `.pdb` via
  `Excludes: "*.pdb"` in `[Files]`; the Linux `.so` files are stripped at
  the source by an MSBuild target in `MeetingScribe.Whisper.csproj`
  (`Whisper.net.Runtime.Vulkan` 1.9.1 ships them unconditionally — see the
  comment there) so they never reach `dotnet publish` output at all, not
  just the installer.
- Re-verified after the Linux-`.so` strip that Vulkan still loads: ran
  `MeetingScribe.Whisper.TestHarness` against the trimmed build —
  `LoadedLibrary: Vulkan`, `IsGpuBackend: True`, native log confirms
  `ggml_vulkan: Found 1 Vulkan devices: 0 = Intel(R) UHD Graphics`, real
  transcription at 16.48x realtime on a 60s clip.
- Full clean install → verify → uninstall → verify cycle completed with
  zero interactive dialogs (`CloseApplications=yes` +
  `/FORCECLOSEAPPLICATIONS` on the install command line handle the file-in-
  use case that showed up before this was added).
