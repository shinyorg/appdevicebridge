#!/usr/bin/env bash
# Publishes Sample.Blazor as a release the sample server offers:  ./publish-release.sh 1.1.0
# Change something in Sample.Blazor first so the new version is visibly different.
# An app with client variants (WebAppHostOptions.Variants) zips each publish's wwwroot into a folder of its own
# instead: release/mobile/…, release/desktop/…, then zips release/.
set -euo pipefail

version="${1:?usage: publish-release.sh <version>}"
here="$(cd "$(dirname "$0")" && pwd)"
target="$here/Sample.ReleaseServer/releases/sample"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet publish "$here/Sample.Blazor/Sample.Blazor.csproj" -c Release -o "$work" --nologo -v quiet

mkdir -p "$target"
rm -f "$target/$version.zip"
(cd "$work/wwwroot" && zip -qr "$target/$version.zip" .)

echo "Published $target/$version.zip"
