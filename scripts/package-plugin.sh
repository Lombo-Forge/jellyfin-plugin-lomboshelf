#!/bin/sh
# Builds the plugin zip a Jellyfin plugin repository serves, and prints its md5 and the manifest entry.
# Usage: sh scripts/package-plugin.sh 0.1.0 [outdir]     (needs the .NET 10 SDK, zip and md5sum)
# Does the same job as jprm (zip holds the dll, checksum is the md5 of the zip) without installing it.
set -eu
cd "$(dirname "$0")/.."
ver="${1:?usage: package-plugin.sh <version like 0.1.0> [outdir]}"
ver="${ver#v}"
out="${2:-dist}"
case "$ver" in *.*.*.*) full="$ver" ;; *.*.*) full="$ver.0" ;; *) echo "version must look like 0.1.0" >&2; exit 1 ;; esac
guid=$(sed -n 's/^guid: *"\(.*\)"/\1/p' plugin/build.yaml)
abi=$(sed -n 's/^targetAbi: *"\(.*\)"/\1/p' plugin/build.yaml)
build=$(mktemp -d)
mkdir -p "$out"
out=$(cd "$out" && pwd)
dotnet publish plugin/Jellyfin.Plugin.LomboShelf/Jellyfin.Plugin.LomboShelf.csproj -c Release \
  -p:Version="$full" -p:AssemblyVersion="$full" -p:FileVersion="$full" -o "$build" >&2
zip="$out/lomboshelf_$full.zip"
rm -f "$zip"
# Only our own dll goes in: Jellyfin supplies every other assembly.
(cd "$build" && zip -q -X "$zip" Jellyfin.Plugin.LomboShelf.dll)
md5=$(md5sum "$zip" | cut -d' ' -f1)
rm -rf "$build"
cat <<JSON
zip:  $zip
md5:  $md5

Manifest entry for the "versions" list in the hub repository's manifest.json (guid $guid):
{
  "version": "$full",
  "changelog": "EDIT ME",
  "targetAbi": "$abi",
  "sourceUrl": "https://github.com/Lombo-Forge/jellyfin-plugin-lomboshelf/releases/download/v$ver/lomboshelf_$full.zip",
  "checksum": "$md5",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
JSON
