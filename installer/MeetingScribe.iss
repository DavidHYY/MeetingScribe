; ============================================================================
; MeetingScribe -- customer-facing Windows installer
;
; Wraps the self-contained net10.0-windows win-x64 publish output
; (dotnet publish -p:PublishSingleFile=true -p:SelfContained=true, see
; installer\build-installer.ps1) into a per-user, no-admin Inno Setup
; installer. Reproduces exactly what dist\install.ps1 / dist\uninstall.ps1
; already did by hand (install path %LocalAppData%\Programs\MeetingScribe,
; Start Menu shortcut, HKCU uninstall entry, no elevation) -- this script
; replaces those two PowerShell scripts as the shipped install mechanism,
; it does not change what gets installed or where.
;
; Whisper ggml model files are NOT bundled (downloaded on first run to
; %LocalAppData%\MeetingScribeCS\models -- can be 1.5-3 GB, see README.md).
; Do not add them to [Files].
; ============================================================================

#define MyAppName "MeetingScribe"
; MyAppVersion's real source of truth is Directory.Build.props at the repo
; root (also what gets baked into the built assemblies, which the in-app
; updater reads). build-installer.ps1 parses that file and passes the value
; here via ISCC's command line (/DMyAppVersion=...), which always wins over
; a #define. This #ifndef only fires if someone opens this .iss directly in
; the Inno Setup IDE and compiles it without going through the script --
; keep it in sync manually for that one fallback case, but the real build
; path never reads it.
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#define MyAppPublisher "David Hao-Yu Yang"
#define MyAppURL "https://github.com/DavidHYY/MeetingScribe"
#define MyAppExeName "MeetingScribe.App.exe"
; Publish output produced by build-installer.ps1 (net10.0-windows, win-x64,
; self-contained single-file). Script lives in installer\, publish tree is
; one level up under MeetingScribe.App\bin\...
#define PublishDir "..\MeetingScribe.App\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
; Fixed AppId -- a random GUID, hardcoded here, and MUST NEVER CHANGE on any
; future build. Windows/Inno use it to recognize "an upgrade of the same
; product" vs. a brand-new install; regenerating it would orphan every
; existing install's uninstall entry and install path detection.
AppId={{16DBE56D-7A97-4624-BA91-586378039C13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\MeetingScribe
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install: no admin rights requested, no UAC prompt at any point.
; Matches dist\install.ps1's behaviour exactly (HKCU + %LocalAppData% only).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 (build 17763) is the practical floor for a net10.0 app;
; earlier builds are EOL and unsupported by the .NET 10 runtime anyway.
MinVersion=10.0.17763
Compression=lzma2
SolidCompression=yes
; The app has no named mutex (AppMutex) in code, so none is declared here --
; do not invent one. Restart Manager (CloseApplications) detects file locks
; directly, it does not require AppMutex; AppMutex is only a *faster*
; detection path when a mutex happens to already exist.
; CloseApplications=yes: if MeetingScribe.App.exe (or Explorer with the
; install folder open) is holding a file lock, ask Restart Manager to close
; it automatically instead of popping the interactive "Setup was unable to
; automatically close all applications" page -- that page is NOT suppressed
; by /SUPPRESSMSGBOXES, it is a distinct wizard page, not a MsgBox.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=MeetingScribe-Setup-{#MyAppVersion}-win-x64
WizardStyle=modern
; No code-signing certificate exists for this build -- unsigned .exe and
; unsigned installer will trigger SmartScreen ("Windows protected your PC")
; on first download. See installer\README.md. Not a bug, do not silently
; "fix" by adding a signing step without a certificate to sign with.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Start Menu shortcut is always created (below, unconditional [Icons] entry).
; Desktop shortcut is opt-in, unchecked by default -- matches dist\install.ps1
; where -DesktopShortcut was an explicit opt-in switch, not the default.
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Recursive copy of the whole publish tree so runtimes\vulkan\win-x64\*.dll
; (Whisper.net native runtime) lands at the correct relative path next to
; the exe -- do not flatten this with individual Source lines, Whisper.net's
; native loader resolves the DLLs relative to the exe's own directory.
; Excludes *.pdb: debug symbols (MeetingScribe.App.pdb + the referenced
; projects' .pdb, plus Skia/HarfBuzzSharp's own .pdb which together are
; ~100 MB) are a dev-time artifact, not something an end user's install
; needs. runtimes\vulkan\linux-x64 is no longer emitted by publish at all
; (stripped at the source in MeetingScribe.Whisper.csproj), so no matching
; Inno-level exclude is needed for it.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] entry for %LocalAppData%\MeetingScribeCS is
; deliberate: that folder holds settings.json and any already-downloaded
; Whisper models (potentially several GB the user chose to keep). Inno's
; default uninstall only removes what [Files] put under {app}; user data
; outside {app} is left untouched with zero extra script needed here. This
; comment exists so a future edit doesn't "helpfully" add a delete rule.

[Code]
// Optional "Minutes backends" step (Claude/Codex/Ollama detection, install,
// PATH bookkeeping) -- see installer\backends.iss for the full design note
// and wiring checklist. Pulled in here, inside this [Code] section, because
// backends.iss is pure Pascal with no [Code] header of its own (confirmed
// by direct ISCC compile-test: a nested `[Code]` marker or `;`-style INI
// comment mid-Pascal is a compile error).
#include "backends.iss"

procedure RemoveStalePowerShellInstallerArtifacts;
var
  OldUninstallKey: String;
  OldUninstallScript: String;
begin
  { The pre-Inno per-user installer (dist\install.ps1) wrote its own HKCU
    "Installed apps" entry under the literal name "MeetingScribe" (Inno's
    own entry is registered as "MeetingScribe_is1" -- Inno always suffixes
    the AppId-derived key with "_is1", so the two never collide by name)
    and copied its own uninstall.ps1 into the install folder. Both are dead
    once this installer's [Files]/[UninstallRun] take over the same
    %LocalAppData%\Programs\MeetingScribe install path. Left alone, Add/
    Remove Programs would show two "MeetingScribe" entries pointing at the
    same folder -- the stale one's UninstallString (uninstall.ps1) would
    still "work" (it does not delete the models/settings folder either) but
    would leave Inno's own entry and driver-store-equivalent bookkeeping
    behind. Only ever touches this exact old key name and this exact old
    file -- never anything Inno itself wrote. }
  OldUninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingScribe';
  if RegKeyExists(HKCU, OldUninstallKey) then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, OldUninstallKey);
  end;

  OldUninstallScript := ExpandConstant('{app}\uninstall.ps1');
  if FileExists(OldUninstallScript) then
  begin
    DeleteFile(OldUninstallScript);
  end;
end;

procedure InitializeWizard;
begin
  InitBackendsWizard;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  BackendsCurPageChanged(CurPageID);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    RemoveStalePowerShellInstallerArtifacts;
  end;
  if CurStep = ssPostInstall then
  begin
    // Runs after MeetingScribe's own files are in place, so a backend
    // install failure (network, winget missing, etc.) never leaves
    // MeetingScribe itself half-installed -- see backends.iss's own header
    // comment for the full rationale.
    RunBackendInstalls;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  BackendsCurUninstallStepChanged(CurUninstallStep);
end;
