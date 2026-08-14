# MeetingScribe installer

Inno Setup replacement for the hand-rolled `dist\install.ps1` /
`dist\uninstall.ps1` pair. Same install behaviour (per-user,
`%LocalAppData%\Programs\MeetingScribe`, no admin, no UAC prompt), packaged
as one double-click `.exe`.

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

## Output

`dist\MeetingScribe-Setup-1.0.0-win-x64.exe` — `dist\` is git-ignored, so the
installer artifact itself is never committed; only `installer\*.iss` /
`*.ps1` (this build recipe) is.

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

## Real measured numbers (2026-08-14 build)

- Installer: `MeetingScribe-Setup-1.0.0-win-x64.exe`, 49,642,873 bytes
  (47.3 MB).
- Installed footprint: 211,904,763 bytes (202.1 MB), 11 files — down from
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
