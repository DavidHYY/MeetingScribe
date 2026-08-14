// ============================================================================
// MeetingScribe -- optional "Minutes backends" installer step [Code] fragment.
//
// NOT a standalone .iss -- this file has no [Setup]/[Files]/etc sections, and
// (deliberately) no [Code] header of its own either: every line in this file
// is Pascal, because it is always textually inserted INSIDE an already-open
// [Code] section (Inno's preprocessor is mid-Pascal-parsing at the insertion
// point, not in section/INI mode -- a leading `;`-style INI comment or a
// redundant `[Code]` marker at that point is a compile error, confirmed by
// direct ISCC compile-test 2026-08-14). Pull it in with:
//
//   #include "backends.iss"
//
// placed INSIDE the main script's [Code] section (anywhere after the
// section's own `[Code]` header line).
//
// WIRING REQUIRED IN THE MAIN SCRIPT (do this, do not just #include and stop):
//   1. Call InitBackendsWizard; from the main script's InitializeWizard (or
//      define InitializeWizard here if the main script doesn't have one yet
//      -- check first, Pascal Script does not allow two procedures with the
//      same name in one compiled unit).
//   2. Call BackendsCurPageChanged(CurPageID); from the main script's
//      CurPageChanged (same "check first, don't duplicate" rule).
//   3. Call RunBackendInstalls; from the main script's EXISTING CurStepChanged
//      at CurStep = ssPostInstall, AFTER RemoveStalePowerShellInstallerArtifacts
//      (files must already be in place; backend installs don't depend on
//      MeetingScribe's own files, but running them last means a backend
//      install failure never leaves MeetingScribe's own install half-done).
//      Deliberately NOT named CurStepChanged here -- the main script already
//      declares that procedure; redeclaring it would be a duplicate-identifier
//      compile error the moment both files are combined.
//   4. Call BackendsCurUninstallStepChanged(CurUninstallStep); from the main
//      script's CurUninstallStepChanged (define it there if absent).
//
// None of the four names above (InitializeWizard, CurPageChanged,
// CurUninstallStepChanged, RunBackendInstalls) existed in MeetingScribe.iss as
// of the last read before this file was written -- re-read the live file
// before wiring; another engineer is actively editing it concurrently.
//
// DESIGN CHOICE: three independent backends are offered via TNewCheckBox
// controls on a custom TWizardPage, NOT [Tasks] entries. [Tasks] checkboxes
// on the built-in "Select Additional Tasks" page have no room for a live
// "Found: C:\...\claude.exe" status line or a literal command/URL under each
// item, and can't be disabled+greyed conditionally without extra plumbing a
// custom page gives for free. This is a deliberate deviation from a plain
// [Tasks]-based approach -- flag to PM/PI if a [Tasks]-based UI was assumed.
//
// Syntax verified 2026-08-14: standalone ISCC compile of this file #included
// inside a minimal scratch [Code] section, exit 0, no errors/warnings.
// ============================================================================

// ---------------------------------------------------------------------------
// Types & globals
// ---------------------------------------------------------------------------

type
  TBackendResult = record
    Attempted: Boolean;   // False = skipped (already installed, or unchecked)
    Succeeded: Boolean;
    Detail: String;       // human-readable outcome, always set when Attempted
    PathDirAdded: String; // dir this run added to PATH, '' if none
  end;

var
  BackendsPage: TWizardPage;
  BackendsResultsPage: TOutputMsgMemoWizardPage;

  ClaudeCheck, CodexCheck, OllamaCheck: TNewCheckBox;
  ClaudeStatus, CodexStatus, OllamaStatus, NodeStatus: TNewStaticText;
  ClaudeCmdBox, CodexCmdBox, OllamaCmdBox: TNewEdit;

  // Detection results, filled by DetectBackends (called when the page is
  // shown -- not once at wizard start -- so a user who installs one of these
  // by hand mid-wizard-session still sees the true state).
  ClaudeFound, CodexFound, OllamaFound, NodeFound, NpmFound: Boolean;
  ClaudePath, CodexPath, OllamaPath, NodePath, NpmPath: String;
  DetectedOnce: Boolean;

  ClaudeResult, CodexResult, OllamaResult, NodeResult: TBackendResult;

  // Cached copy of the last summary RunBackendInstalls built, so
  // BackendsCurPageChanged can defensively re-apply it (see there for why).
  BackendsSummaryText: String;

const
  // Literal commands -- verified-current 2026-08-14, do not substitute.
  // Displayed verbatim on the wizard page (requirement: show exactly what
  // will run) and used verbatim (wrapped only for non-interactive
  // invocation) to actually run it.
  ClaudeInstallCmdDisplay = 'PowerShell: irm https://claude.ai/install.ps1 | iex';
  CodexInstallCmdDisplay = 'npm install -g @openai/codex';
  OllamaInstallCmdDisplay = 'winget install --id Ollama.Ollama --scope user --accept-source-agreements --accept-package-agreements';
  // Fallback prerequisite for Codex when npm/node are both absent. Verified
  // empirically 2026-08-14 on this machine: `winget install --id
  // OpenJS.NodeJS.LTS --scope user ...` genuinely installs per-user, no UAC
  // -- winget resolves it to a portable ZIP (not the MSI, which is
  // machine-scope-only and has no declared user Scope in the manifest) and
  // writes the extracted dir straight onto HKCU\Environment\Path itself.
  // IMPORTANT: npm's default global-install prefix for that portable Node is
  // the Node install directory ITSELF (confirmed via `npm config get
  // prefix`), NOT %APPDATA%\npm -- %APPDATA%\npm is a convention specific to
  // the official Windows *MSI* installer, which writes that default into its
  // own npmrc. Do not hardcode %APPDATA%\npm for a backend-installed Node;
  // always read npm's actual prefix after install (see ResolveNpmPrefixDir).
  NodeInstallCmd = 'winget install --id OpenJS.NodeJS.LTS --scope user --accept-source-agreements --accept-package-agreements --silent';

// ---------------------------------------------------------------------------
// PATH resolution -- mirrors Services/Minutes/CliExecutableResolver.cs
// exactly (PATHEXT-suffixed candidates before the bare name, in PATHEXT
// order) so the installer's idea of "found" never disagrees with what the
// app itself will find at runtime. Detection here is pure file-existence
// probing (no process spawn, no timeout handling needed) -- CliAvailability
// Checker's *health* check (actually running `--version`) is an app-runtime
// concern; the installer's job is presence, per the task spec.
// ---------------------------------------------------------------------------

function BackendsGetPathExt: TArrayOfString;
var
  Raw: String;
begin
  Raw := GetEnv('PATHEXT');
  if Raw = '' then
    Raw := '.COM;.EXE;.BAT;.CMD';
  Result := StringSplit(Raw, [';'], stExcludeEmpty);
end;

// Returns the resolved full path if found on PATH, '' otherwise. Command
// must be a bare name with no path separators (mirrors the C# resolver's
// contract -- callers here never pass a rooted path).
function BackendsResolveOnPath(const Command: String): String;
var
  PathVar, Dir, BareCandidate, Candidate: String;
  Dirs, Exts: TArrayOfString;
  I, J: Integer;
begin
  Result := '';
  PathVar := GetEnv('PATH');
  if PathVar = '' then
    Exit;

  Dirs := StringSplit(PathVar, [';'], stExcludeEmpty);
  Exts := BackendsGetPathExt;

  for I := 0 to GetArrayLength(Dirs) - 1 do
  begin
    Dir := Trim(Dirs[I]);
    if Dir = '' then
      Continue;
    BareCandidate := AddBackslash(Dir) + Command;

    // PATHEXT-suffixed candidates first -- an extension-less file on disk
    // (e.g. an npm POSIX shim) is not necessarily launchable on Windows.
    // Lowercase the appended extension purely for a cleaner display string
    // -- %PATHEXT% entries are conventionally uppercase (".EXE;.CMD;...")
    // and FileExists is case-insensitive on Windows either way, so this is
    // cosmetic only, not a behavior change.
    for J := 0 to GetArrayLength(Exts) - 1 do
    begin
      Candidate := BareCandidate + Lowercase(Trim(Exts[J]));
      if FileExists(Candidate) then
      begin
        Result := Candidate;
        Exit;
      end;
    end;

    // Bare name fallback -- covers a name that already carries a real
    // executable extension (e.g. someone put "node.exe" itself in PATH text).
    if FileExists(BareCandidate) then
    begin
      Result := BareCandidate;
      Exit;
    end;
  end;
end;

// ---------------------------------------------------------------------------
// Detection
// ---------------------------------------------------------------------------

procedure DetectBackends;
begin
  ClaudePath := BackendsResolveOnPath('claude');
  ClaudeFound := ClaudePath <> '';

  CodexPath := BackendsResolveOnPath('codex');
  CodexFound := CodexPath <> '';

  OllamaPath := BackendsResolveOnPath('ollama');
  OllamaFound := OllamaPath <> '';

  NodePath := BackendsResolveOnPath('node');
  NodeFound := NodePath <> '';

  NpmPath := BackendsResolveOnPath('npm');
  NpmFound := NpmPath <> '';

  DetectedOnce := True;
end;

// ---------------------------------------------------------------------------
// Install-phase audit log -- Inno's own /LOG captures Setup's own actions,
// NOT this file's Pascal-level decisions (which backend was selected, the
// exact command line run, the raw exit code, paths probed, verification
// result). Without this there is zero artifact on a user's machine to
// diagnose a silent-looking backend-install failure from. Written to the
// same %LocalAppData%\MeetingScribeCS\logs\ directory Program.cs (the app
// itself) already uses for crash.log, so "check your logs folder" finds
// both in one place.
//
// Declared this early (right after Detection, before everything else that
// calls it -- Wizard page, PATH functions, per-backend installers,
// orchestration) because Pascal Script requires a function be declared
// before its first call site within the compiled unit.
// ---------------------------------------------------------------------------

var
  BackendsLogPathCache: String;

function BackendsLogFilePath: String;
begin
  if BackendsLogPathCache = '' then
    BackendsLogPathCache := ExpandConstant('{localappdata}\MeetingScribeCS\logs\backends-install.log');
  Result := BackendsLogPathCache;
end;

function BackendsResultFilePath: String;
begin
  Result := ExpandConstant('{localappdata}\MeetingScribeCS\logs\backends-install-result.txt');
end;

function BoolStr(const B: Boolean): String;
begin
  if B then
    Result := 'True'
  else
    Result := 'False';
end;

function BoolTo01(const B: Boolean): String;
begin
  if B then
    Result := '1'
  else
    Result := '0';
end;

// Never raises and never aborts the calling install step on a logging
// failure (e.g. logs\ not writable) -- ForceDirectories/SaveStringToFile
// both return Boolean rather than raising, so a lost log line is silently
// tolerated by design: the backend install itself must never fail because
// logging failed, same rationale as Program.cs's own LogCrash catch blocks.
procedure BackendsLog(const Line: String);
var
  LogFile, Stamped: String;
begin
  LogFile := BackendsLogFilePath;
  if not ForceDirectories(ExtractFileDir(LogFile)) then
    Exit;
  Stamped := '[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] ' + Line;
  SaveStringToFile(LogFile, Stamped + #13#10, True);
end;

// Strips characters that would break the sidecar's '|'-delimited format out
// of a Detail string before it's written -- every Detail here is built by
// this script from literal text plus paths/exit codes, none of which are
// expected to contain '|' or newlines, but a value we didn't fully control
// (e.g. captured stdout in ResolveNpmPrefixDir) could in principle.
function BackendsSanitizeField(const S: String): String;
var
  R: String;
begin
  R := S;
  StringChangeEx(R, '|', ';', True);
  StringChangeEx(R, #13#10, ' ', True);
  StringChangeEx(R, #13, ' ', True);
  StringChangeEx(R, #10, ' ', True);
  Result := R;
end;

// Machine-readable sidecar, overwritten every run, so MeetingScribe.App
// itself (Settings tab) can tell "never tried to install this" apart from
// "the installer tried and failed" -- a live PATH probe alone only ever
// sees "missing right now", it can't distinguish those two cases. Kept
// separate from backends-install.log (append-only, human-readable, every
// run ever) rather than parsing that: fixed 4-field '|'-delimited format
// is trivial and robust to parse from C#, a growing timestamped log is not.
procedure BackendsWriteResultLine(const F: TStringList; const Name: String; const R: TBackendResult);
begin
  F.Add(Name + '|' + BoolTo01(R.Attempted) + '|' + BoolTo01(R.Succeeded) + '|' + BackendsSanitizeField(R.Detail));
end;

procedure BackendsWriteResultFile;
var
  F: TStringList;
  ResultPath: String;
begin
  ResultPath := BackendsResultFilePath;
  if not ForceDirectories(ExtractFileDir(ResultPath)) then
  begin
    BackendsLog('WARN: could not create ' + ExtractFileDir(ResultPath) + ' -- result sidecar not written.');
    Exit;
  end;

  F := TStringList.Create;
  try
    F.Add('# MeetingScribe backend install result -- machine-readable, overwritten every run. Do not hand-edit.');
    F.Add('timestamp|' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':'));
    BackendsWriteResultLine(F, 'claude', ClaudeResult);
    BackendsWriteResultLine(F, 'codex', CodexResult);
    BackendsWriteResultLine(F, 'ollama', OllamaResult);
    if SaveStringToFile(ResultPath, F.Text, False) then
      BackendsLog('Result sidecar written: ' + ResultPath)
    else
      BackendsLog('WARN: SaveStringToFile failed for ' + ResultPath);
  finally
    F.Free;
  end;
end;

// ---------------------------------------------------------------------------
// Wizard page
// ---------------------------------------------------------------------------

function BackendsAddLabel(AParent: TWinControl; ATop: Integer; const AText: String; ABold: Boolean): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := AParent;
  Result.Left := ScaleX(0);
  Result.Top := ATop;
  Result.Width := BackendsPage.SurfaceWidth;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Caption := AText;
  if ABold then
    Result.Font.Style := [fsBold];
end;

function BackendsAddCmdBox(AParent: TWinControl; ATop: Integer; const ACmd: String): TNewEdit;
begin
  Result := TNewEdit.Create(WizardForm);
  Result.Parent := AParent;
  Result.Left := ScaleX(16);
  Result.Top := ATop;
  Result.Width := BackendsPage.SurfaceWidth - ScaleX(16);
  Result.ReadOnly := True;
  Result.Text := ACmd;
end;

procedure InitBackendsWizard;
var
  Y: Integer;
begin
  BackendsPage := CreateCustomPage(
    wpSelectTasks,
    'Minutes Backends (Optional)',
    'MeetingScribe transcribes locally and free. Minutes generation can optionally use one of these.');

  Y := ScaleY(0);
  BackendsAddLabel(BackendsPage.Surface, Y,
    'Installing none of these is a supported outcome -- MeetingScribe installs and runs fine either way. ' +
    'You can install any backend later, by hand, and MeetingScribe will pick it up with no reinstall.', False);
  Y := Y + ScaleY(34);

  // --- Claude ---
  ClaudeCheck := TNewCheckBox.Create(WizardForm);
  ClaudeCheck.Parent := BackendsPage.Surface;
  ClaudeCheck.Left := ScaleX(0);
  ClaudeCheck.Top := Y;
  ClaudeCheck.Width := BackendsPage.SurfaceWidth;
  ClaudeCheck.Caption := 'Claude (subscription + interactive login required, works offline for nothing else)';
  ClaudeCheck.Checked := False;
  Y := Y + ScaleY(17);
  ClaudeStatus := BackendsAddLabel(BackendsPage.Surface, Y, 'Checking...', False);
  Y := Y + ScaleY(17);
  ClaudeCmdBox := BackendsAddCmdBox(BackendsPage.Surface, Y, ClaudeInstallCmdDisplay + '   (from https://claude.ai/install.ps1)');
  Y := Y + ScaleY(24);

  // --- Codex ---
  CodexCheck := TNewCheckBox.Create(WizardForm);
  CodexCheck.Parent := BackendsPage.Surface;
  CodexCheck.Left := ScaleX(0);
  CodexCheck.Top := Y;
  CodexCheck.Width := BackendsPage.SurfaceWidth;
  CodexCheck.Caption := 'Codex (subscription + interactive login required; needs Node.js/npm, installed automatically if missing)';
  CodexCheck.Checked := False;
  Y := Y + ScaleY(17);
  CodexStatus := BackendsAddLabel(BackendsPage.Surface, Y, 'Checking...', False);
  Y := Y + ScaleY(17);
  CodexCmdBox := BackendsAddCmdBox(BackendsPage.Surface, Y, CodexInstallCmdDisplay);
  Y := Y + ScaleY(17);
  NodeStatus := BackendsAddLabel(BackendsPage.Surface, Y, '', False);
  Y := Y + ScaleY(24);

  // --- Ollama ---
  OllamaCheck := TNewCheckBox.Create(WizardForm);
  OllamaCheck.Parent := BackendsPage.Surface;
  OllamaCheck.Left := ScaleX(0);
  OllamaCheck.Top := Y;
  OllamaCheck.Width := BackendsPage.SurfaceWidth;
  OllamaCheck.Caption := 'Ollama (local, offline, no subscription -- weaker minutes quality on a CPU-only machine)';
  OllamaCheck.Checked := False;
  Y := Y + ScaleY(17);
  OllamaStatus := BackendsAddLabel(BackendsPage.Surface, Y, 'Checking...', False);
  Y := Y + ScaleY(17);
  OllamaCmdBox := BackendsAddCmdBox(BackendsPage.Surface, Y, OllamaInstallCmdDisplay);
  Y := Y + ScaleY(24);

  BackendsAddLabel(BackendsPage.Surface, Y,
    'Neither Claude nor Codex can be logged in by this installer. If installed, you must run "claude" or ' +
    '"codex" once in a terminal afterward to log in before minutes generation with that backend will work.', False);

  // Results page: shown after the install progress page, before Finish.
  // AMsg is a real, non-empty placeholder (never '') so the memo can never
  // render as a blank box even in an edge case where RunBackendInstalls's
  // ssPostInstall population somehow runs after the page has already been
  // constructed -- BackendsCurPageChanged below also re-applies the real
  // summary defensively at the moment this page becomes active, so the
  // memo is populated from two independent points, not one fragile one.
  BackendsResultsPage := CreateOutputMsgMemoPage(
    wpInstalling,
    'Minutes Backends -- Results',
    'What happened with the backends you selected.',
    'A failed backend install does not affect MeetingScribe -- it is installed either way.',
    'Not yet run.');
  BackendsResultsPage.RichEditViewer.ReadOnly := True;
end;

// Reflect current checkbox state (installed backends forced-checked and
// disabled) and current detection results into the page's controls.
procedure RefreshBackendsPageUi;
begin
  if ClaudeFound then
  begin
    ClaudeStatus.Caption := 'Installed: ' + ClaudePath;
    ClaudeCheck.Checked := True;
    ClaudeCheck.Enabled := False;
  end
  else
  begin
    ClaudeStatus.Caption := 'Not found.';
    ClaudeCheck.Enabled := True;
  end;

  if CodexFound then
  begin
    CodexStatus.Caption := 'Installed: ' + CodexPath;
    CodexCheck.Checked := True;
    CodexCheck.Enabled := False;
    NodeStatus.Caption := '';
  end
  else
  begin
    CodexStatus.Caption := 'Not found.';
    CodexCheck.Enabled := True;
    if NodeFound and NpmFound then
      NodeStatus.Caption := 'Node.js/npm found: ' + NpmPath
    else
      NodeStatus.Caption := 'Node.js/npm not found -- will install Node.js LTS (per-user, no admin) first.';
  end;

  if OllamaFound then
  begin
    OllamaStatus.Caption := 'Installed: ' + OllamaPath;
    OllamaCheck.Checked := True;
    OllamaCheck.Enabled := False;
  end
  else
  begin
    OllamaStatus.Caption := 'Not found.';
    OllamaCheck.Enabled := True;
  end;
end;

// Call from the main script's CurPageChanged.
procedure BackendsCurPageChanged(CurPageID: Integer);
begin
  if CurPageID = BackendsPage.ID then
  begin
    DetectBackends;
    RefreshBackendsPageUi;
  end;

  // Defensive re-apply: RunBackendInstalls (CurStepChanged/ssPostInstall)
  // already sets this memo's content well before this page can possibly be
  // reached (Inno cannot show a page after wpInstalling before wpInstalling
  // itself has finished running ssPostInstall), but re-applying here too,
  // from a cached string rather than recomputing, costs nothing and removes
  // any dependency on that ordering being exactly right in every Inno
  // version/run mode -- see the empty-memo defect this closes.
  if (CurPageID = BackendsResultsPage.ID) and (BackendsSummaryText <> '') then
    BackendsResultsPage.RichEditViewer.Lines.Text := BackendsSummaryText;
end;

// ---------------------------------------------------------------------------
// PATH append/remove -- HKCU\Environment\Path, REG_EXPAND_SZ, append-only,
// idempotent (case-insensitive, trailing-backslash-insensitive), broadcasts
// WM_SETTINGCHANGE so already-open shells/Explorer pick it up without a
// logoff. RootKey/SubKey/ValueName are parameters (not hardcoded to the
// real Environment key) specifically so this logic can be exercised against
// a scratch registry value in isolation before ever touching the real PATH.
// ---------------------------------------------------------------------------

function SendMessageTimeoutW(hWnd: Longint; Msg: Longint; wParam: Longint; lParam: String;
  fuFlags: Longint; uTimeout: Longint; var lpdwResult: Longint): Longint;
  external 'SendMessageTimeoutW@user32.dll stdcall';

// HWND_BROADCAST is already a predefined Inno constant (confirmed via ISCC
// compile error on a redeclaration attempt, 2026-08-14) -- do not redeclare
// it. WM_SETTINGCHANGE and SMTO_ABORTIFHUNG are NOT predefined (same
// empirical check), so those two are declared here.
const
  WM_SETTINGCHANGE = $001A;
  SMTO_ABORTIFHUNG = $0002;

procedure BroadcastEnvironmentChange;
var
  ResultCode: Longint;
begin
  SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, 'Environment', SMTO_ABORTIFHUNG, 5000, ResultCode);
end;

function DirsEqual(const A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(A), RemoveBackslashUnlessRoot(B)) = 0;
end;

// Case/trailing-backslash-insensitive membership check.
function PathListContainsDir(const PathValue, Dir: String): Boolean;
var
  Parts: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Parts := StringSplit(PathValue, [';'], stExcludeEmpty);
  for I := 0 to GetArrayLength(Parts) - 1 do
  begin
    if DirsEqual(Trim(Parts[I]), Dir) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// Appends Dir to RootKey\SubKey\ValueName if not already present (idempotent).
// Preserves REG_EXPAND_SZ (never downgrades to REG_SZ -- Path legitimately
// contains %SystemRoot%-style entries elsewhere; RegWriteStringValue would
// silently change the value's type and break those). Returns True if it
// changed anything.
//
// Byte-exact by construction: this is a pure concatenation, it never
// rewrites a single existing character of Current. In particular it does
// NOT special-case "Current already ends with ';'" and skip adding a
// separator there -- that used to make append non-reversible (a trailing
// ';' means Current's last segment is an empty one; reusing that ';' as the
// separator for Dir silently deletes that empty segment). Always inserting
// ';' + Dir keeps Current's own trailing separator/empty segment intact and
// is exactly what RemoveDirFromPathValue below can undo byte-for-byte
// (proven by the append-then-remove round-trip test).
function AppendDirToPathValue(RootKey: Integer; const SubKey, ValueName, Dir: String): Boolean;
var
  Current, NewValue: String;
begin
  Result := False;
  if not RegQueryStringValue(RootKey, SubKey, ValueName, Current) then
    Current := '';

  if PathListContainsDir(Current, Dir) then
    Exit; // already present -- idempotent no-op

  if Current = '' then
    NewValue := Dir // no existing value -- no separator to add
  else
    NewValue := Current + ';' + Dir;

  if RegWriteExpandStringValue(RootKey, SubKey, ValueName, NewValue) then
    Result := True;
end;

// Removes Dir from RootKey\SubKey\ValueName if present (case/trailing-slash
// -insensitive match), leaving every other segment -- including empty
// segments produced by a trailing ';' or a doubled ';;', and any leading/
// trailing whitespace around them -- byte-for-byte untouched and in
// original order. Returns True if it changed anything.
//
// Byte-exact by construction: split with stAll (NOT stExcludeEmpty, which
// would silently drop a trailing/duplicated empty segment -- the original
// defect) so every segment survives the round trip, then rebuild by joining
// the segments that don't match Dir. Joining N-1 remaining segments always
// uses exactly N-2 separators, i.e. removing one segment always removes
// exactly one adjacent separator and nothing else -- this is the exact
// inverse of AppendDirToPathValue's unconditional "Current + ';' + Dir".
// Kept segments are copied verbatim (untrimmed); only the comparison uses
// Trim, so whitespace around an unrelated entry is never altered.
function RemoveDirFromPathValue(RootKey: Integer; const SubKey, ValueName, Dir: String): Boolean;
var
  Current, NewValue, Part: String;
  Parts: TArrayOfString;
  I: Integer;
  Changed, FirstKept: Boolean;
begin
  Result := False;
  if not RegQueryStringValue(RootKey, SubKey, ValueName, Current) then
    Exit;

  Parts := StringSplit(Current, [';'], stAll);
  NewValue := '';
  Changed := False;
  FirstKept := True;
  for I := 0 to GetArrayLength(Parts) - 1 do
  begin
    Part := Parts[I]; // verbatim -- do not Trim, only compare trimmed
    if DirsEqual(Trim(Part), Dir) then
    begin
      Changed := True;
      Continue; // drop this one segment (and, via join, exactly one separator)
    end;
    if FirstKept then
    begin
      NewValue := Part;
      FirstKept := False;
    end
    else
      NewValue := NewValue + ';' + Part;
  end;

  if Changed then
  begin
    if RegWriteExpandStringValue(RootKey, SubKey, ValueName, NewValue) then
      Result := True;
  end;
end;

// Updates THIS process's (Setup.exe's) own environment block, not just the
// registry. RegWriteExpandStringValue above only affects FUTURE processes
// that re-read HKCU\Environment; this running process's own env block is a
// snapshot taken before any of these writes and is NOT live-refreshed by
// BroadcastEnvironmentChange (that broadcast only prompts other top-level
// windows, e.g. Explorer, to pick up the change for whatever THEY launch
// next -- it does nothing for this process or its children).
//
// Two consequences if this is skipped: (1) any Exec() this script makes for
// the rest of THIS run still can't find a backend it just installed on
// PATH (worked around ad hoc for Node/npm in InstallNodeIfNeeded via a
// directory scan, before this function existed); and (2) far more visibly,
// Inno's own [Run] "Launch MeetingScribe" entry -- which fires when the
// user clicks Finish, i.e. strictly after this whole install including
// this call has completed -- spawns MeetingScribe.App.exe as a child of
// THIS process with no explicit environment override, so CreateProcess
// gives it a verbatim copy of this process's (still-stale) env block. A
// user who checks "Install Claude" then immediately launches MeetingScribe
// from the Finish page would see Settings report Claude "not found on
// PATH" -- true for that inherited env block, but indistinguishable to the
// user from the install having silently failed, when it actually
// succeeded. SetEnvironmentVariableW updates the calling process's
// (Setup.exe's) own block; any child process spawned afterward via
// CreateProcess with no explicit environment -- which is what both this
// script's own Exec() calls and Inno's [Run] launch use -- inherits the
// updated copy.
function SetEnvironmentVariableW(lpName, lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

procedure RefreshOwnProcessPathEnv(const Dir: String);
var
  CurrentPath, NewPath: String;
begin
  CurrentPath := GetEnv('PATH');
  if PathListContainsDir(CurrentPath, Dir) then
    Exit; // already present in this process's own env -- nothing to do
  if CurrentPath = '' then
    NewPath := Dir
  else
    NewPath := CurrentPath + ';' + Dir;
  if SetEnvironmentVariableW('PATH', NewPath) then
    BackendsLog('PATH: refreshed this process''s own env so ' + Dir + ' is visible to it and to anything it launches next (e.g. the post-install "Launch MeetingScribe" entry).')
  else
    BackendsLog('WARN: SetEnvironmentVariableW failed to refresh this process''s own PATH after adding ' + Dir + ' -- a "Launch MeetingScribe" right after this install may still show the backend as not found until the app is restarted.');
end;

// Convenience wrappers against the real HKCU\Environment\Path.
function AppendUserPathDir(const Dir: String): Boolean;
begin
  Result := AppendDirToPathValue(HKCU, 'Environment', 'Path', Dir);
  if Result then
  begin
    BroadcastEnvironmentChange;
    RefreshOwnProcessPathEnv(Dir);
  end;
end;

function RemoveUserPathDir(const Dir: String): Boolean;
begin
  Result := RemoveDirFromPathValue(HKCU, 'Environment', 'Path', Dir);
  if Result then
    BroadcastEnvironmentChange;
end;

// ---------------------------------------------------------------------------
// Bookkeeping -- exactly which dirs THIS installer added, so uninstall never
// touches anything it did not add itself.
// ---------------------------------------------------------------------------

const
  BookkeepingKey = 'Software\MeetingScribe';
  BookkeepingValue = 'InstallerAddedPathDirs';

procedure RecordPathDirAdded(const Dir: String);
var
  Current, NewValue: String;
begin
  if not RegQueryStringValue(HKCU, BookkeepingKey, BookkeepingValue, Current) then
    Current := '';
  if PathListContainsDir(Current, Dir) then
    Exit;
  if Current = '' then
    NewValue := Dir
  else
    NewValue := Current + ';' + Dir;
  RegWriteStringValue(HKCU, BookkeepingKey, BookkeepingValue, NewValue);
end;

// Call from the main script's CurUninstallStepChanged.
procedure BackendsCurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Recorded: String;
  Dirs: TArrayOfString;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if not RegQueryStringValue(HKCU, BookkeepingKey, BookkeepingValue, Recorded) then
    Exit;

  Dirs := StringSplit(Recorded, [';'], stExcludeEmpty);
  for I := 0 to GetArrayLength(Dirs) - 1 do
  begin
    if Trim(Dirs[I]) <> '' then
      RemoveUserPathDir(Trim(Dirs[I])); // no-op if already absent -- fine
  end;

  RegDeleteValue(HKCU, BookkeepingKey, BookkeepingValue);
  // Only deletes MeetingScribe's own bookkeeping key/value -- never touches
  // anything a backend's own installer (winget, npm, the Claude installer)
  // wrote for itself, same "don't remove a shared dependency" rule this
  // codebase already applies to pnputil driver packages.
end;

// ---------------------------------------------------------------------------
// Process execution helpers
// ---------------------------------------------------------------------------

// Resolves the PowerShell 5.1 exe deterministically rather than trusting
// "powershell" to be on PATH inside a non-interactive Exec call (same class
// of gotcha as winget.exe not reliably being on PATH here).
function ResolvePowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(Result) then
    Result := BackendsResolveOnPath('powershell.exe');
end;

// winget.exe is not reliably on PATH in a non-interactive Exec context --
// resolve the known location first, PATH as fallback, and let the caller
// report "not found" rather than failing silently.
function ResolveWingetExe: String;
begin
  Result := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if not FileExists(Result) then
    Result := BackendsResolveOnPath('winget.exe');
end;

// Runs ShimPath with Args, choosing the right indirection for its extension
// (.cmd/.bat via cmd.exe /c, .ps1 via powershell.exe -File, anything else
// -- e.g. .exe -- directly). Mirrors the same "don't assume the extension"
// lesson CliExecutableResolver.cs already documents for the app itself.
function RunShim(const ShimPath, Args: String; var ResultCode: Integer): Boolean;
var
  Ext, ComSpecExe, PsExe: String;
begin
  Ext := Lowercase(ExtractFileExt(ShimPath));
  if (Ext = '.cmd') or (Ext = '.bat') then
  begin
    ComSpecExe := GetEnv('ComSpec');
    if ComSpecExe = '' then
      ComSpecExe := ExpandConstant('{sys}\cmd.exe');
    Result := Exec(ComSpecExe, '/C ""' + ShimPath + '" ' + Args + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else if Ext = '.ps1' then
  begin
    PsExe := ResolvePowerShellExe;
    Result := Exec(PsExe, '-NoProfile -ExecutionPolicy Bypass -File "' + ShimPath + '" ' + Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else
  begin
    Result := Exec(ShimPath, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Runs Filename/Params and captures its stdout+stderr via a temp-file
// redirect (Exec itself has no stdout capture). Used only for `npm config
// get prefix`, where we need the actual value, not just an exit code.
function RunCaptureOutput(const Filename, Params: String; var Output: String): Boolean;
var
  TempFile, ComSpecExe: String;
  ResultCode: Integer;
  Ext: String;
  RawOutput: AnsiString;
begin
  Output := '';
  TempFile := ExpandConstant('{tmp}\meetingscribe-backend-out.txt');
  ComSpecExe := GetEnv('ComSpec');
  if ComSpecExe = '' then
    ComSpecExe := ExpandConstant('{sys}\cmd.exe');

  Ext := Lowercase(ExtractFileExt(Filename));
  if (Ext = '.cmd') or (Ext = '.bat') then
    Result := Exec(ComSpecExe, '/C ""' + Filename + '" ' + Params + ' > "' + TempFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else if Ext = '.ps1' then
    Result := Exec(ComSpecExe, '/C ""' + ResolvePowerShellExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + Filename + '" ' + Params + ' > "' + TempFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Result := Exec(ComSpecExe, '/C ""' + Filename + '" ' + Params + ' > "' + TempFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if Result and (ResultCode = 0) and FileExists(TempFile) then
  begin
    if LoadStringFromFile(TempFile, RawOutput) then
      Output := Trim(String(RawOutput))
    else
      Output := '';
  end;
  if FileExists(TempFile) then
    DeleteFile(TempFile);
end;

// After a successful `npm install -g`, ask npm itself where it put things --
// do NOT hardcode %APPDATA%\npm, see the NodeInstallCmd comment above for why.
function ResolveNpmPrefixDir(const NpmExePath: String): String;
var
  Output: String;
begin
  Result := '';
  if RunCaptureOutput(NpmExePath, 'config get prefix', Output) then
  begin
    if (Output <> '') and DirExists(Output) then
      Result := Output;
  end;
end;

// ---------------------------------------------------------------------------
// Per-backend install actions. Each returns a filled TBackendResult; never
// raises, never leaves ResultCode/Detail unset. A failure here is reported,
// never propagated as a MeetingScribe install failure (requirement: backend
// failures are non-fatal to the main install, which has already completed
// by the time these run at ssPostInstall).
//
// Small helpers first -- Pascal Script requires a function be declared
// before its first call site within the same compiled unit (confirmed by
// direct ISCC compile-test 2026-08-14: no forward-reference resolution
// across the file), so these come before InstallNodeIfNeeded/InstallOllama.
// ---------------------------------------------------------------------------

// Strips a leading "winget " from a full command string, since Exec takes
// the exe and its args separately -- keeps the [const] command strings
// exactly as documented/displayed (copy-pasteable as one line) while still
// being usable with Exec.
function RemoveWingetCmdPrefix(const FullCmd: String): String;
begin
  if Pos('winget ', FullCmd) = 1 then
    Result := Copy(FullCmd, Length('winget ') + 1, MaxInt)
  else
    Result := FullCmd;
end;

function FindNodeExeUnder(const ParentDir: String): String;
var
  FindPattern: String;
  FindRec: TFindRec;
begin
  Result := '';
  FindPattern := AddBackslash(ParentDir) + 'node-*-win-x64';
  if FindFirst(FindPattern, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if FileExists(AddBackslash(ParentDir) + FindRec.Name + '\node.exe') then
          begin
            Result := AddBackslash(ParentDir) + FindRec.Name + '\node.exe';
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Fallback locator: scans %LOCALAPPDATA%\Microsoft\WinGet\Packages for a
// OpenJS.NodeJS.LTS_* dir and returns its node.exe, for the same-session
// case described in InstallNodeIfNeeded's comment.
function FindWingetPortableNodeExe: String;
var
  BaseDir, FindPattern, PkgDir: String;
  FindRec: TFindRec;
begin
  Result := '';
  BaseDir := ExpandConstant('{localappdata}\Microsoft\WinGet\Packages');
  if not DirExists(BaseDir) then
    Exit;

  FindPattern := AddBackslash(BaseDir) + 'OpenJS.NodeJS.LTS_*';
  if FindFirst(FindPattern, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          PkgDir := AddBackslash(BaseDir) + FindRec.Name;
          Result := FindNodeExeUnder(PkgDir);
          if Result <> '' then
            Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Same same-session PATH problem as Node: scan the documented install dir
// directly rather than relying on our own process's stale inherited PATH.
function FindWingetOllamaExe: String;
var
  Dir: String;
begin
  Result := '';
  Dir := ExpandConstant('{localappdata}\Programs\Ollama');
  if FileExists(AddBackslash(Dir) + 'ollama.exe') then
    Result := AddBackslash(Dir) + 'ollama.exe';
end;

function InstallClaude: TBackendResult;
var
  PsExe, CmdLine: String;
  ResultCode: Integer;
  Dir: String;
begin
  Result.Attempted := True;
  Result.PathDirAdded := '';
  BackendsLog('Claude: install started.');

  PsExe := ResolvePowerShellExe;
  if PsExe = '' then
  begin
    Result.Succeeded := False;
    Result.Detail := 'PowerShell not found -- cannot run the Claude installer.';
    BackendsLog('Claude: ' + Result.Detail);
    Exit;
  end;

  CmdLine := '-NoProfile -ExecutionPolicy Bypass -Command "irm https://claude.ai/install.ps1 | iex"';
  BackendsLog('Claude: command: "' + PsExe + '" ' + CmdLine);
  if not Exec(PsExe, CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result.Succeeded := False;
    Result.Detail := 'Failed to launch the Claude installer (PowerShell could not be started).';
    BackendsLog('Claude: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Claude: process exited, raw exit code ' + IntToStr(ResultCode) + '.');
  if ResultCode <> 0 then
  begin
    Result.Succeeded := False;
    Result.Detail := 'Claude installer exited with code ' + IntToStr(ResultCode) + '.';
    BackendsLog('Claude: ' + Result.Detail);
    Exit;
  end;

  // Verified per requirement: lands in %USERPROFILE%\.local\bin.
  Dir := ExpandConstant('{%USERPROFILE}\.local\bin');
  BackendsLog('Claude: probing ' + Dir + '\claude.exe -> found=' + BoolStr(FileExists(Dir + '\claude.exe')));
  if FileExists(Dir + '\claude.exe') then
  begin
    if AppendUserPathDir(Dir) then
      RecordPathDirAdded(Dir);
    Result.PathDirAdded := Dir;
    Result.Succeeded := True;
    Result.Detail := 'Installed claude.exe to ' + Dir + '.';
  end
  else
  begin
    Result.Succeeded := False;
    Result.Detail := 'Installer exited 0 but claude.exe was not found at the expected location (' + Dir + ').';
  end;
  BackendsLog('Claude: ' + Result.Detail);
end;

function InstallNodeIfNeeded: TBackendResult;
var
  WingetExe: String;
  ResultCode: Integer;
begin
  Result.Attempted := True;
  Result.PathDirAdded := '';
  BackendsLog('Node: prerequisite check started (needed by Codex).');

  if NodeFound and NpmFound then
  begin
    Result.Succeeded := True;
    Result.Detail := 'Node.js/npm already present (' + NpmPath + ') -- nothing to install.';
    BackendsLog('Node: ' + Result.Detail);
    Exit;
  end;

  WingetExe := ResolveWingetExe;
  if WingetExe = '' then
  begin
    Result.Succeeded := False;
    Result.Detail := 'winget.exe not found (checked %LOCALAPPDATA%\Microsoft\WindowsApps and PATH) -- cannot install Node.js.';
    BackendsLog('Node: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Node: command: "' + WingetExe + '" ' + RemoveWingetCmdPrefix(NodeInstallCmd));
  if not Exec(WingetExe, RemoveWingetCmdPrefix(NodeInstallCmd), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result.Succeeded := False;
    Result.Detail := 'Failed to launch winget for Node.js install.';
    BackendsLog('Node: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Node: winget process exited, raw exit code ' + IntToStr(ResultCode) + '.');
  if ResultCode <> 0 then
  begin
    Result.Succeeded := False;
    Result.Detail := 'winget install of Node.js LTS exited with code ' + IntToStr(ResultCode) + '.';
    BackendsLog('Node: ' + Result.Detail);
    Exit;
  end;

  // winget's own per-user portable-Node install writes its own PATH entry
  // directly (verified empirically) -- do not duplicate that bookkeeping
  // here, and do not record it for our own uninstaller to remove: Node is a
  // reusable dependency the user may keep using after uninstalling
  // MeetingScribe, same reasoning as never /delete-driver'ing a shared
  // bridge driver. Re-resolve so the Codex step can find node/npm right
  // away, in THIS process, without relying on a PATH refresh that a running
  // process never picks up (env is captured at process-creation time).
  NodePath := BackendsResolveOnPath('node');
  NpmPath := BackendsResolveOnPath('npm');
  if (NodePath = '') or (NpmPath = '') then
  begin
    // Even right after a successful install, our own already-running
    // process's inherited PATH does not include the new dir (Windows does
    // not live-refresh a running process's env from the registry) --
    // resolving again with GetEnv('PATH') will *still* fail here. Fall back
    // to a WinGet-Packages directory scan so Codex's own install can proceed
    // in the same session instead of silently requiring a second run.
    NodePath := FindWingetPortableNodeExe;
    if NodePath <> '' then
      NpmPath := ExtractFilePath(NodePath) + 'npm.cmd';
  end;

  NodeFound := NodePath <> '';
  NpmFound := NpmPath <> '';

  Result.Succeeded := NodeFound and NpmFound;
  if Result.Succeeded then
    Result.Detail := 'Installed Node.js LTS (per-user, no admin) via winget; npm at ' + NpmPath + '.'
  else
    Result.Detail := 'winget reported success but node/npm could not be located afterward.';
  BackendsLog('Node: ' + Result.Detail);
end;

function InstallCodex: TBackendResult;
var
  NodeStep: TBackendResult;
  ResultCode: Integer;
  Dir: String;
begin
  Result.Attempted := True;
  Result.PathDirAdded := '';
  BackendsLog('Codex: install started.');

  NodeStep := InstallNodeIfNeeded;
  NodeResult := NodeStep;
  if not NodeStep.Succeeded then
  begin
    Result.Succeeded := False;
    Result.Detail := 'Skipped -- Node.js/npm prerequisite failed: ' + NodeStep.Detail;
    BackendsLog('Codex: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Codex: command: "' + NpmPath + '" install -g @openai/codex');
  if not RunShim(NpmPath, 'install -g @openai/codex', ResultCode) then
  begin
    Result.Succeeded := False;
    Result.Detail := 'Failed to launch npm.';
    BackendsLog('Codex: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Codex: npm process exited, raw exit code ' + IntToStr(ResultCode) + '.');
  if ResultCode <> 0 then
  begin
    Result.Succeeded := False;
    Result.Detail := 'npm install -g @openai/codex exited with code ' + IntToStr(ResultCode) + '.';
    BackendsLog('Codex: ' + Result.Detail);
    Exit;
  end;

  Dir := ResolveNpmPrefixDir(NpmPath);
  BackendsLog('Codex: resolved npm global prefix -> "' + Dir + '"');
  if Dir = '' then
  begin
    Result.Succeeded := False;
    Result.Detail := 'npm exited 0 but its global prefix could not be determined afterward.';
    BackendsLog('Codex: ' + Result.Detail);
    Exit;
  end;

  if AppendUserPathDir(Dir) then
    RecordPathDirAdded(Dir);
  Result.PathDirAdded := Dir;
  Result.Succeeded := True;
  Result.Detail := 'Installed via npm; global bin dir ' + Dir + '.';
  BackendsLog('Codex: ' + Result.Detail);
end;

function InstallOllama: TBackendResult;
var
  WingetExe: String;
  ResultCode: Integer;
  Dir, ResolvedOllama: String;
begin
  Result.Attempted := True;
  Result.PathDirAdded := '';
  BackendsLog('Ollama: install started.');

  WingetExe := ResolveWingetExe;
  if WingetExe = '' then
  begin
    Result.Succeeded := False;
    Result.Detail := 'winget.exe not found (checked %LOCALAPPDATA%\Microsoft\WindowsApps and PATH) -- cannot install Ollama.';
    BackendsLog('Ollama: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Ollama: command: "' + WingetExe + '" ' + RemoveWingetCmdPrefix(OllamaInstallCmdDisplay));
  if not Exec(WingetExe, RemoveWingetCmdPrefix(OllamaInstallCmdDisplay), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result.Succeeded := False;
    Result.Detail := 'Failed to launch winget for Ollama install.';
    BackendsLog('Ollama: ' + Result.Detail);
    Exit;
  end;

  BackendsLog('Ollama: winget process exited, raw exit code ' + IntToStr(ResultCode) + '.');
  if ResultCode <> 0 then
  begin
    Result.Succeeded := False;
    Result.Detail := 'winget install of Ollama exited with code ' + IntToStr(ResultCode) + '.';
    BackendsLog('Ollama: ' + Result.Detail);
    Exit;
  end;

  ResolvedOllama := FindWingetOllamaExe;
  if ResolvedOllama = '' then
    Dir := ExpandConstant('{localappdata}\Programs\Ollama')
  else
    Dir := ExtractFilePath(ResolvedOllama);

  BackendsLog('Ollama: probing ' + Dir + 'ollama.exe -> found=' + BoolStr(FileExists(AddBackslash(Dir) + 'ollama.exe')));
  if not FileExists(AddBackslash(Dir) + 'ollama.exe') then
  begin
    Result.Succeeded := False;
    Result.Detail := 'winget reported success but ollama.exe was not found afterward (checked ' + Dir + ').';
    BackendsLog('Ollama: ' + Result.Detail);
    Exit;
  end;

  if AppendUserPathDir(Dir) then
    RecordPathDirAdded(Dir);
  Result.PathDirAdded := Dir;
  Result.Succeeded := True;
  Result.Detail := 'Installed to ' + Dir + '.';
  BackendsLog('Ollama: ' + Result.Detail);
end;

// ---------------------------------------------------------------------------
// Orchestration -- call from the main script's CurStepChanged at
// CurStep = ssPostInstall.
// ---------------------------------------------------------------------------

function FormatBackendLine(const Name: String; const R: TBackendResult): String;
begin
  if not R.Attempted then
    Result := Name + ': skipped (not selected, or already installed).'
  else if R.Succeeded then
    Result := Name + ': OK -- ' + R.Detail
  else
    Result := Name + ': FAILED -- ' + R.Detail;
end;

procedure RunBackendInstalls;
var
  Summary: TStringList;
  AttemptedCount, FailedCount: Integer;
begin
  BackendsLog('=== Backend install run started -- log file: ' + BackendsLogFilePath + ' ===');
  BackendsLog('Claude: selected=' + BoolStr(ClaudeCheck.Checked) + ' enabled=' + BoolStr(ClaudeCheck.Enabled));
  BackendsLog('Codex: selected=' + BoolStr(CodexCheck.Checked) + ' enabled=' + BoolStr(CodexCheck.Enabled));
  BackendsLog('Ollama: selected=' + BoolStr(OllamaCheck.Checked) + ' enabled=' + BoolStr(OllamaCheck.Enabled));

  ClaudeResult.Attempted := False;
  CodexResult.Attempted := False;
  OllamaResult.Attempted := False;

  if ClaudeCheck.Checked and ClaudeCheck.Enabled then
    ClaudeResult := InstallClaude;

  if CodexCheck.Checked and CodexCheck.Enabled then
    CodexResult := InstallCodex;

  if OllamaCheck.Checked and OllamaCheck.Enabled then
    OllamaResult := InstallOllama;

  // Counted separately from the per-backend Detail text below so the
  // results page can lead with an impossible-to-miss "N of M failed" line
  // rather than requiring the user to read three lines and notice one says
  // FAILED -- a user who only glances at the page (or whose window is
  // small) should still see that something needs attention.
  AttemptedCount := 0;
  FailedCount := 0;
  if ClaudeResult.Attempted then
  begin
    AttemptedCount := AttemptedCount + 1;
    if not ClaudeResult.Succeeded then
      FailedCount := FailedCount + 1;
  end;
  if CodexResult.Attempted then
  begin
    AttemptedCount := AttemptedCount + 1;
    if not CodexResult.Succeeded then
      FailedCount := FailedCount + 1;
  end;
  if OllamaResult.Attempted then
  begin
    AttemptedCount := AttemptedCount + 1;
    if not OllamaResult.Succeeded then
      FailedCount := FailedCount + 1;
  end;

  Summary := TStringList.Create;
  try
    Summary.Add('MINUTES BACKENDS');
    Summary.Add('');
    if FailedCount > 0 then
    begin
      Summary.Add('*** ' + IntToStr(FailedCount) + ' of ' + IntToStr(AttemptedCount) +
        ' selected backend install(s) FAILED -- see the FAILED line(s) below, and the full log at:');
      Summary.Add('    ' + BackendsLogFilePath);
      Summary.Add('');
    end;
    Summary.Add(FormatBackendLine('Claude', ClaudeResult));
    Summary.Add(FormatBackendLine('Codex', CodexResult));
    Summary.Add(FormatBackendLine('Ollama', OllamaResult));
    Summary.Add('');
    Summary.Add('A failed backend install does NOT affect MeetingScribe -- it is installed and will run.');
    Summary.Add('');
    if (ClaudeResult.Attempted and ClaudeResult.Succeeded) then
      Summary.Add('Claude: run "claude" once in a terminal to log in before minutes generation will work.');
    if (CodexResult.Attempted and CodexResult.Succeeded) then
      Summary.Add('Codex: run "codex" once in a terminal to log in before minutes generation will work.');
    Summary.Add('');
    Summary.Add('Installed zero backends? MeetingScribe still transcribes locally and free. Install a ' +
      'backend later, by hand, any time -- MeetingScribe detects it with no reinstall needed.');
    Summary.Add('');
    Summary.Add('Full install log: ' + BackendsLogFilePath);

    BackendsSummaryText := Summary.Text;
    BackendsResultsPage.RichEditViewer.Lines.Text := BackendsSummaryText;
    BackendsLog('--- Results page summary text follows ---');
    BackendsLog(BackendsSummaryText);
    BackendsLog('--- end summary ---');
  finally
    Summary.Free;
  end;

  BackendsWriteResultFile;
end;
