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
VERSION=1.1.0.0

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
  "changelog": "Built-in API key, reuse OpenSubtitles plugin login, save location setting, logo, Jellyfin 10.11 support",
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
  ZIP="${NAME}_jellyfin-$TARGET.zip"
  if command -v zip >/dev/null 2>&1; then
    (cd "$DIR" && zip -qr "$ZIP" "$NAME")
  else
    (cd "$DIR" && /c/Windows/System32/tar.exe -a -cf "$ZIP" "$NAME")
  fi
  echo "Built $OUT"
}

build 12 12.0.0.0
build 10.11 10.11.0.0
