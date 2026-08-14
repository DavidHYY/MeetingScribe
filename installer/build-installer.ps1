#Requires -Version 5.1
<#
.SYNOPSIS
    One-command build: dotnet publish -> Inno Setup compile -> report.
.DESCRIPTION
    1. dotnet publish MeetingScribe.App for net10.0-windows/win-x64,
       self-contained, single-file (matches what dist\MeetingScribe already
       is: a 128 MB MeetingScribe.App.exe with no loose System.*.dll next to
       it, plus the Vulkan/Skia/HarfBuzz native DLLs as loose files -- see
       "Why self-contained" below).
    2. Runs ISCC.exe against installer\MeetingScribe.iss.
    3. Prints the output .exe path, size, and SHA-256.

    Fails loudly (non-zero exit, explicit error message) if dotnet publish,
    ISCC.exe, or the expected output file is missing -- never silently
    swallows a missing step.
.PARAMETER Configuration
    Build configuration. Default Release.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$InstallerDir     = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot         = Split-Path -Parent $InstallerDir
$AppProject       = Join-Path $RepoRoot 'MeetingScribe.App\MeetingScribe.App.csproj'
$IssFile          = Join-Path $InstallerDir 'MeetingScribe.iss'
$DirectoryProps   = Join-Path $RepoRoot 'Directory.Build.props'
$IsccPath         = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
$Tfm              = 'net10.0-windows'
$Rid              = 'win-x64'

if (-not (Test-Path $AppProject)) {
    throw "Cannot find $AppProject -- run this script from a checkout of MeetingScribeCS."
}
if (-not (Test-Path $IsccPath)) {
    throw "ISCC.exe not found at $IsccPath. Inno Setup 6 must be installed at this path (per packaging brief); it is not on PATH."
}

# --- Version: parse from Directory.Build.props, don't hardcode it here -----
# Directory.Build.props is the single source of truth for the product
# version -- it is what MSBuild bakes into every assembly's
# AssemblyVersion/FileVersion (which the in-app updater reads at runtime to
# compare against the latest GitHub Release), so a build recipe that reads
# the version from anywhere else (e.g. the .iss) could ship an installer
# whose filename doesn't match what the app itself reports. Parse it here
# and pass it into ISCC via /DMyAppVersion so there is exactly one place to
# bump. Fail loudly if the property is missing or malformed rather than
# falling back to a guessed default, which would just recreate the same bug
# in a quieter form.
if (-not (Test-Path $DirectoryProps)) {
    throw "Cannot find $DirectoryProps -- run this script from a checkout of MeetingScribeCS."
}
$propsContent = Get-Content -Path $DirectoryProps -Raw
$versionMatch = [regex]::Match($propsContent, '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $versionMatch.Success) {
    throw "Could not find '<Version>...</Version>' in $DirectoryProps -- cannot determine the version to build. Fix Directory.Build.props rather than adding a default here."
}
$AppVersion = $versionMatch.Groups[1].Value
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version in $DirectoryProps is '$AppVersion', which doesn't look like a semantic version (expected X.Y.Z). Refusing to build with an unvalidated version string."
}
Write-Host "Building version $AppVersion (from Version in $DirectoryProps)"

# --- Step 1: dotnet publish -------------------------------------------------
# Self-contained + single-file, NOT framework-dependent: dist\MeetingScribe\
# (the existing hand-built payload this installer must match) is a single
# ~128 MB MeetingScribe.App.exe with no loose Microsoft.*/System.*.dll next
# to it -- that shape only comes from PublishSingleFile+SelfContained, and
# dist\README-INSTALL.md's own "What was verified" section confirms the
# build command used was `-r win-x64 --self-contained true
# -p:PublishSingleFile=true`. Framework-dependent would require the target
# machine to have the matching .NET 10 desktop runtime installed separately,
# which this per-user, no-admin installer does not attempt to provision.
Write-Host "=== [1/3] dotnet publish ($Tfm, $Rid, self-contained, single-file) ===" -ForegroundColor Cyan
$publishArgs = @(
    'publish', $AppProject,
    '-c', $Configuration,
    '-f', $Tfm,
    '-r', $Rid,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$PublishDir = Join-Path $RepoRoot "MeetingScribe.App\bin\$Configuration\$Tfm\$Rid\publish"
if (-not (Test-Path $PublishDir)) {
    throw "Expected publish output not found at $PublishDir -- dotnet publish reported success but the folder is missing."
}
$exePath = Join-Path $PublishDir 'MeetingScribe.App.exe'
if (-not (Test-Path $exePath)) {
    throw "Publish output at $PublishDir does not contain MeetingScribe.App.exe."
}
Write-Host "Publish output: $PublishDir"

# --- Step 2: ISCC compile ----------------------------------------------------
# /DMyAppVersion overrides the .iss's own #define (see the #ifndef guard
# there) so the version this script just validated from Directory.Build.props
# is what actually ends up in AppVersion and OutputBaseFilename -- not
# whatever the .iss happens to have hardcoded as its IDE-only fallback.
Write-Host "`n=== [2/3] ISCC compile ===" -ForegroundColor Cyan
& $IsccPath "/DMyAppVersion=$AppVersion" $IssFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

# --- Step 3: locate + report -------------------------------------------------
Write-Host "`n=== [3/3] Result ===" -ForegroundColor Cyan
$DistDir  = Join-Path $RepoRoot 'dist'
# Filename derived from the same $AppVersion parsed above, not a second
# hardcoded literal -- must stay in lockstep with MeetingScribe.iss's own
# OutputBaseFilename=MeetingScribe-Setup-{#MyAppVersion}-win-x64.
$OutExe   = Join-Path $DistDir "MeetingScribe-Setup-$AppVersion-win-x64.exe"
if (-not (Test-Path $OutExe)) {
    throw "Expected installer output not found at $OutExe -- ISCC reported success but the file is missing (check OutputDir/OutputBaseFilename in MeetingScribe.iss)."
}

$sizeBytes = (Get-Item $OutExe).Length
$hash      = (Get-FileHash -Path $OutExe -Algorithm SHA256).Hash

Write-Host "Installer:  $OutExe"
Write-Host ("Size:       {0:N0} bytes ({1:N1} MB)" -f $sizeBytes, ($sizeBytes / 1MB))
Write-Host "SHA-256:    $hash"

# --- Step 4: SHA256SUMS.txt sidecar -----------------------------------------
# This build is NOT reproducible -- the PE header embeds a compile timestamp,
# so identical source produces a different SHA-256 every run. A hash therefore
# cannot live in source control; it can only be trusted if it comes from the
# exact artifact a release actually ships. This sidecar is that: one line,
# next to the installer, hashing the file this run just produced. The in-app
# updater (MeetingScribe.App\Services\Update\UpdateChecker.cs) downloads this
# alongside the installer from the GitHub Release and refuses to launch
# anything that doesn't match it -- so this file MUST be attached to the
# release as a second asset alongside the installer .exe, or older-style
# releases that lack it, or a manual upload that forgets it, leave the
# updater with nothing to verify against (by design it then refuses to
# download-and-run the installer unverified, and tells the user to grab it
# from the release page manually instead).
#
# Format: "<sha256-hex>  <filename>" (two spaces) -- lowercase hex to match
# what Sha256SumsFile.cs / a plain `sha256sum -c` would expect.
Write-Host "`n=== [4/4] SHA256SUMS.txt sidecar ===" -ForegroundColor Cyan
$SumsPath = Join-Path $DistDir 'SHA256SUMS.txt'
$sumsLine = "{0}  {1}" -f $hash.ToLowerInvariant(), (Split-Path -Leaf $OutExe)
# Set-Content's -Encoding utf8NoBOM value only exists on PowerShell 6+; this script targets
# 5.1 too (#Requires above), so write via .NET directly for a BOM-less UTF-8 file on either.
[System.IO.File]::WriteAllText($SumsPath, $sumsLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote:      $SumsPath"
Write-Host "Contents:   $sumsLine"
