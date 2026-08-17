#!/usr/bin/env bash
# ==============================================================================
# MeetingScribe -- macOS .dmg builder
#
# Mirrors installer/build-installer.ps1's shape for the Windows side:
#   1. dotnet publish MeetingScribe.App (net10.0, osx-arm64, self-contained).
#      This also triggers MeetingScribe.App.csproj's CreateMacOSAppBundle
#      MSBuild target (AfterTargets="Publish", macOS-host-only) which turns
#      the publish output into an ad-hoc-signed MeetingScribe.app.
#   2. hdiutil packages that .app plus an /Applications symlink into a
#      drag-install .dmg.
#   3. Prints the output path, size, and SHA-256.
#   4. Appends one line to dist/SHA256SUMS.txt, same "<sha256>  <filename>"
#      format build-installer.ps1 writes for the Windows installer.
#
# Fails loudly (non-zero exit, explicit message) if a required tool is
# missing, this is not run on macOS, dotnet publish fails, or an expected
# output path is missing -- never silently swallows a missing step.
#
# Not notarized -- ad-hoc signed only (see CreateMacOSAppBundle's own doc
# comment). First launch on a machine that downloaded this .dmg will show
# Gatekeeper's "Apple could not verify ... is free of malware" / unidentified
# developer warning; the user must right-click -> Open (or
# System Settings > Privacy & Security > Open Anyway) once. No Apple
# Developer certificate is used or required by this script.
#
# Usage:
#   installer/build-dmg.sh [Configuration]
#   Configuration defaults to Release.
# ==============================================================================

set -euo pipefail

# --- Preconditions -----------------------------------------------------------

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "error: build-dmg.sh only runs on macOS (needs hdiutil/codesign, and" >&2
    echo "       CreateMacOSAppBundle in MeetingScribe.App.csproj only fires on a" >&2
    echo "       macOS build host). Detected: $(uname -s)." >&2
    exit 1
fi

for tool in dotnet hdiutil codesign shasum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "error: required tool '$tool' not found on PATH. Install it and re-run." >&2
        exit 1
    fi
done

Configuration="${1:-Release}"

ScriptDir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RepoRoot="$(cd "$ScriptDir/.." && pwd)"
AppProject="$RepoRoot/MeetingScribe.App/MeetingScribe.App.csproj"
DirectoryProps="$RepoRoot/Directory.Build.props"
Tfm="net10.0"
Rid="osx-arm64"

if [[ ! -f "$AppProject" ]]; then
    echo "error: cannot find $AppProject -- run this script from a checkout of MeetingScribeCS." >&2
    exit 1
fi

# --- Version: parse from Directory.Build.props, don't hardcode it here ------
# Same single source of truth build-installer.ps1 uses for the Windows
# installer filename -- see that script's own comment on why (in-app updater
# reads this exact number from the built assembly).
if [[ ! -f "$DirectoryProps" ]]; then
    echo "error: cannot find $DirectoryProps -- run this script from a checkout of MeetingScribeCS." >&2
    exit 1
fi

AppVersion="$(grep -o '<Version>[^<]*</Version>' "$DirectoryProps" | head -n1 | sed -E 's/<Version>([^<]*)<\/Version>/\1/')"
if [[ -z "$AppVersion" ]]; then
    echo "error: could not find '<Version>...</Version>' in $DirectoryProps -- cannot determine the version to build. Fix Directory.Build.props rather than adding a default here." >&2
    exit 1
fi
if ! [[ "$AppVersion" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "error: version in $DirectoryProps is '$AppVersion', which doesn't look like a semantic version (expected X.Y.Z). Refusing to build with an unvalidated version string." >&2
    exit 1
fi
echo "Building version $AppVersion (from Version in $DirectoryProps)"

# --- Step 1: dotnet publish (also runs CreateMacOSAppBundle) ----------------
echo "=== [1/4] dotnet publish ($Tfm, $Rid, self-contained) ===" >&2
if ! dotnet publish "$AppProject" \
    -c "$Configuration" \
    -f "$Tfm" \
    -r "$Rid" \
    --self-contained true; then
    echo "error: dotnet publish failed." >&2
    exit 1
fi

PublishDir="$RepoRoot/MeetingScribe.App/bin/$Configuration/$Tfm/$Rid/publish"
if [[ ! -d "$PublishDir" ]]; then
    echo "error: expected publish output not found at $PublishDir -- dotnet publish reported success but the folder is missing." >&2
    exit 1
fi

# --- Step 2: locate + sanity-check the .app bundle ---------------------------
# CreateMacOSAppBundle (MeetingScribe.App.csproj) writes here -- see that
# target's own doc comment for why it's a sibling "bundle/" dir, not
# "$(PublishDir)/../MeetingScribe.app" directly (APFS case-collision with the
# published "MeetingScribe.App" apphost).
echo "=== [2/4] Locate .app bundle ===" >&2
AppBundleDir="$RepoRoot/MeetingScribe.App/bin/$Configuration/$Tfm/$Rid/bundle/MeetingScribe.app"
if [[ ! -d "$AppBundleDir" ]]; then
    echo "error: expected app bundle not found at $AppBundleDir -- dotnet publish succeeded but CreateMacOSAppBundle did not produce it (check that target's Condition in MeetingScribe.App.csproj)." >&2
    exit 1
fi
if [[ ! -f "$AppBundleDir/Contents/Info.plist" ]]; then
    echo "error: $AppBundleDir is missing Contents/Info.plist -- incomplete bundle." >&2
    exit 1
fi
if [[ ! -x "$AppBundleDir/Contents/MacOS/MeetingScribe.App" ]]; then
    echo "error: $AppBundleDir is missing an executable Contents/MacOS/MeetingScribe.App -- incomplete bundle." >&2
    exit 1
fi
echo "App bundle: $AppBundleDir"

# --- Step 3: package into a .dmg ---------------------------------------------
# Staging folder holds exactly what should appear as top-level items in the
# mounted volume: the .app, plus a symlink to /Applications so the user can
# drag-drop install. hdiutil turns that staging folder's *contents* into the
# volume, not the folder itself.
echo "=== [3/4] hdiutil: build .dmg ===" >&2
StagingDir="$(mktemp -d "${TMPDIR:-/tmp}/meetingscribe-dmg.XXXXXX")"
trap 'rm -rf "$StagingDir"' EXIT

cp -R "$AppBundleDir" "$StagingDir/MeetingScribe.app"
ln -s /Applications "$StagingDir/Applications"

DistDir="$RepoRoot/dist"
mkdir -p "$DistDir"
OutDmg="$DistDir/MeetingScribe-$AppVersion-osx-arm64.dmg"
rm -f "$OutDmg"

if ! hdiutil create \
    -volname "MeetingScribe" \
    -srcfolder "$StagingDir" \
    -fs HFS+ \
    -format UDZO \
    -ov \
    "$OutDmg"; then
    echo "error: hdiutil create failed." >&2
    exit 1
fi

if [[ ! -f "$OutDmg" ]]; then
    echo "error: expected .dmg output not found at $OutDmg -- hdiutil reported success but the file is missing." >&2
    exit 1
fi

# --- Step 4: report + SHA256SUMS.txt sidecar ---------------------------------
# Same rationale as build-installer.ps1's sidecar: this build is not
# reproducible byte-for-byte (compile timestamps, HFS+ metadata), so the hash
# can only be trusted coming from the exact artifact a release actually
# ships -- format matches that script's: "<sha256-hex>  <filename>", lowercase
# hex, two spaces, so a plain `shasum -c` (or the in-app updater once it
# supports macOS artifacts) can verify either platform's sidecar the same way.
echo "=== [4/4] Result ===" >&2
SizeBytes="$(stat -f%z "$OutDmg")"
Sha256="$(shasum -a 256 "$OutDmg" | awk '{print $1}')"
SizeMb="$(awk -v b="$SizeBytes" 'BEGIN { printf "%.1f", b / 1048576 }')"

echo "DMG:       $OutDmg"
echo "Size:      $SizeBytes bytes ($SizeMb MB)"
echo "SHA-256:   $Sha256"

SumsPath="$DistDir/SHA256SUMS.txt"
SumsLine="$(echo "$Sha256" | tr '[:upper:]' '[:lower:]')  $(basename "$OutDmg")"
printf '%s\n' "$SumsLine" >> "$SumsPath"
echo "Appended:  $SumsPath"
echo "Contents:  $SumsLine"

echo ""
echo "NOTE: this .dmg is ad-hoc signed, not notarized. First launch will show" >&2
echo "      Gatekeeper's unidentified-developer warning -- right-click the app" >&2
echo "      -> Open (or System Settings > Privacy & Security > Open Anyway)." >&2
