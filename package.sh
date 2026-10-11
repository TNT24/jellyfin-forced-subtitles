#!/usr/bin/env sh
# Builds the plugin for each supported Jellyfin line:
#   dist/jellyfin-12/ForcedSubtitles_<version>/      (Jellyfin 12.x,  .NET 10)
#   dist/jellyfin-10.11/ForcedSubtitles_<version>/   (Jellyfin 10.11, .NET 9)
# plus a zip of each.
#
# Built-in API key: put your OpenSubtitles API key in opensubtitles-api-key.txt (git-ignored)
# or set OPENSUBTITLES_API_KEY. Users then only need a username and password.
set -e
cd "$(dirname "$0")"
# Each Jellyfin line gets its own last version number (….12 / ….11) so a Jellyfin 12 server, which can
# also install 10.11 builds, always picks the newer 12 build.
BASE_VERSION=1.2.1
CHANGELOG="Test connection now saves the settings first, so a newly entered API key or login is used"

KEY="${OPENSUBTITLES_API_KEY:-}"
if [ -z "$KEY" ] && [ -f opensubtitles-api-key.txt ]; then
  KEY="$(tr -d ' \r\n' < opensubtitles-api-key.txt)"
fi
if [ -z "$KEY" ]; then
  echo "Note: no built-in API key; users will have to enter one in the plugin settings."
fi

rm -rf dist

build() {
  TARGET="$1"   # 12 or 10.11
  ABI="$2"      # targetAbi for meta.json
  VERSION="$BASE_VERSION.$3"
  NAME="ForcedSubtitles_$VERSION"
  DIR="dist/jellyfin-$TARGET"
  OUT="$DIR/$NAME"

  dotnet publish Jellyfin.Plugin.ForcedSubtitles -c Release -o "$DIR/publish" \
    -p:JellyfinTarget="$TARGET" -p:OpenSubtitlesApiKey="$KEY" \
    -p:AssemblyVersion=$VERSION -p:FileVersion=$VERSION
  mkdir -p "$OUT"
  cp "$DIR/publish/Jellyfin.Plugin.ForcedSubtitles.dll" "$OUT/"
  cp assets/logo.png "$OUT/logo.png"
  rm -rf "$DIR/publish"
  cat > "$OUT/meta.json" <<JSON
{
  "category": "Subtitles",
  "changelog": "$CHANGELOG",
  "description": "Checks your libraries for missing subtitles. English-audio videos get a forced subtitle when one exists; foreign-audio videos get a full subtitle.",
  "guid": "5c6b4d1e-9f3a-4b8e-a2d7-0f1e6c3b9a47",
  "name": "Forced Subtitles",
  "overview": "Download missing forced and foreign-language subtitles from OpenSubtitles.com",
  "owner": "local",
  "targetAbi": "$ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "logo.png",
  "assemblies": []
}
JSON
  # zip, or bsdtar (built into Windows 10+). Both write portable '/' paths; PowerShell 5's
  # Compress-Archive writes '\' which Linux/Docker servers unpack as a single odd file name.
  # The files go at the root of the zip, not in a folder: Jellyfin unpacks a repository install
  # straight into its own plugins/<name>_<version> folder and expects meta.json there.
  ZIP="../${NAME}_jellyfin-$TARGET.zip"
  FILES="Jellyfin.Plugin.ForcedSubtitles.dll logo.png meta.json"
  if command -v zip >/dev/null 2>&1; then
    (cd "$OUT" && zip -q "$ZIP" $FILES)
  else
    (cd "$OUT" && /c/Windows/System32/tar.exe -a -cf "$ZIP" $FILES)
  fi
  echo "Built $OUT"
}

build 12 12.0.0.0 12
build 10.11 10.11.0.0 11
