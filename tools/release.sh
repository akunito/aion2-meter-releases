#!/usr/bin/env sh
# Publishes a release from a clean, pushed main:
#   tools/release.sh 0.6.0 NOTES.md          stable (= /releases/latest, what installs with the Stable channel update to)
#   tools/release.sh 0.6.1 NOTES.md --dev    pre-release (Development builds channel)
# Builds with the full test suite (build/build.sh), then `gh release create vX.Y.Z` with the Windows zip, the Linux tarball and SHA256SUMS.txt.
# The Windows updater picks the asset ending in -win-x64.zip and checks it against GitHub's size and SHA-256.
set -eu
[ $# -ge 2 ] || { sed -n 2,5p "$0"; exit 2; }
V=$1; NOTES=$2; DEV=${3:-}
case $V in [0-9]*.[0-9]*.[0-9]*) ;; *) echo "version must be X.Y.Z" >&2; exit 2;; esac
[ -f "$NOTES" ] || { echo "no notes file: $NOTES" >&2; exit 2; }
ROOT=$(cd "$(dirname "$0")/.." && pwd); cd "$ROOT"
GH=${GH:-gh}; command -v "$GH" >/dev/null || GH="nix shell nixpkgs#gh -c gh"
[ -z "$(git status --porcelain)" ] || { echo "working tree not clean" >&2; exit 1; }
git fetch -q origin main
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "HEAD is not origin/main (push first)" >&2; exit 1; }
git rev-parse -q --verify "refs/tags/v$V" >/dev/null && { echo "tag v$V exists" >&2; exit 1; }
VERSION=$V build/build.sh
for f in "dist/AION2DpsMeter-v$V-win-x64.zip" "dist/AION2DpsMeter-v$V-linux-x64.tar.gz" dist/SHA256SUMS.txt; do [ -f "$f" ] || { echo "missing $f" >&2; exit 1; }; done
# shellcheck disable=SC2086
$GH release create "v$V" --target "$(git rev-parse HEAD)" --title "AION2 DPS Meter v$V" --notes-file "$NOTES" ${DEV:+--prerelease} \
  "dist/AION2DpsMeter-v$V-win-x64.zip" "dist/AION2DpsMeter-v$V-linux-x64.tar.gz" dist/SHA256SUMS.txt
