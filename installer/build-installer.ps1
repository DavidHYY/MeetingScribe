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

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir
$AppProject   = Join-Path $RepoRoot 'MeetingScribe.App\MeetingScribe.App.csproj'
$IssFile      = Join-Path $InstallerDir 'MeetingScribe.iss'
$IsccPath     = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
$Tfm          = 'net10.0-windows'
$Rid          = 'win-x64'

if (-not (Test-Path $AppProject)) {
    throw "Cannot find $AppProject -- run this script from a checkout of MeetingScribeCS."
}
if (-not (Test-Path $IsccPath)) {
    throw "ISCC.exe not found at $IsccPath. Inno Setup 6 must be installed at this path (per packaging brief); it is not on PATH."
}

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
Write-Host "`n=== [2/3] ISCC compile ===" -ForegroundColor Cyan
& $IsccPath $IssFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

# --- Step 3: locate + report -------------------------------------------------
Write-Host "`n=== [3/3] Result ===" -ForegroundColor Cyan
$DistDir  = Join-Path $RepoRoot 'dist'
$OutExe   = Join-Path $DistDir 'MeetingScribe-Setup-1.0.0-win-x64.exe'
if (-not (Test-Path $OutExe)) {
    throw "Expected installer output not found at $OutExe -- ISCC reported success but the file is missing (check OutputDir/OutputBaseFilename in MeetingScribe.iss)."
}

$sizeBytes = (Get-Item $OutExe).Length
$hash      = (Get-FileHash -Path $OutExe -Algorithm SHA256).Hash

Write-Host "Installer:  $OutExe"
Write-Host ("Size:       {0:N0} bytes ({1:N1} MB)" -f $sizeBytes, ($sizeBytes / 1MB))
Write-Host "SHA-256:    $hash"
