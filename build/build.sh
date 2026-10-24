#!/usr/bin/env sh
# Reproducible build inside the official .NET SDK image; nothing but docker is needed on the host. Run from anywhere:
#   build/build.sh                 tests + Windows zip + Linux tarball into dist/
#   build/build.sh --linux-only    tests + Linux tarball (skips the minutes-long win-x64 single-file publish)
#   build/build.sh --no-tests      skip the test suite (local iteration only; releases always run it)
#   VERSION=0.6.0 build/build.sh   stamps the assemblies and BUILD-INFO with "release v0.6.0" (builds without it never self-update)
# Output: dist/AION2DpsMeter-<ver>-win-x64.zip, dist/AION2DpsMeter-<ver>-linux-x64.tar.gz, dist/SHA256SUMS.txt (<ver> = v$VERSION or the short commit).
set -eu
LINUX_ONLY=0; TESTS=1
for a in "$@"; do case $a in --linux-only) LINUX_ONLY=1;; --no-tests) TESTS=0;; *) echo "unknown option: $a" >&2; exit 2;; esac; done
SRC=$(cd "$(dirname "$0")/.." && pwd)
SHA=$(git -C "$SRC" rev-parse --short HEAD 2>/dev/null || echo unknown)
VERSION=${VERSION:-}
TAG=${VERSION:+v$VERSION}; TAG=${TAG:-$SHA}
OUT=$SRC/build/out/$TAG
DIST=$SRC/dist
NUGET=$SRC/build/.nuget
rm -rf "$OUT"; mkdir -p "$OUT" "$DIST" "$NUGET"
IMG=mcr.microsoft.com/dotnet/sdk:10.0
# Rootless docker maps container root onto the host user; `--user` there becomes an unmapped subuid that cannot write the bind mounts,
# so the flag is only for rootful docker. The test step runs as root (apt-get libpcap); under rootful docker it hands the files back
# to the caller (under rootless a chown would map them to a subuid the host user cannot write).
case "$(docker info -f '{{.SecurityOptions}}' 2>/dev/null)" in *rootless*) USER_FLAG=""; OWNER="";; *) USER_FLAG="--user $(id -u):$(id -g)"; OWNER="$(id -u):$(id -g)";; esac
ENVS="-e HOME=/tmp/home -e DOTNET_CLI_HOME=/tmp/home -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e NUGET_PACKAGES=/nuget"
MOUNTS="-v $SRC:/src -v $NUGET:/nuget -v $OUT:/out"
run_root() { docker run --rm ${DOCKER_CPUS:+--cpus "$DOCKER_CPUS"} --memory 8g $ENVS -e OWNER="$OWNER" -e TEST_FILTER="${TEST_FILTER:-}" $MOUNTS -w /src "$IMG" "$@"; }
run() { docker run --rm ${DOCKER_CPUS:+--cpus "$DOCKER_CPUS"} --memory 8g $USER_FLAG $ENVS $MOUNTS -w /src "$IMG" "$@"; }

if [ "$TESTS" = 1 ]; then
  echo "== tests =="
  # SharpPcap needs libpcap.so even to read a capture file (the sdk image has none: SIGABRT without it).
  run_root sh -c 'if ! ldconfig -p | grep -q libpcap; then apt-get update -qq >/dev/null && apt-get install -y -qq libpcap0.8 >/dev/null; fi; dotnet test tests/AionMeter.Tests -c Release --nologo -v q ${TEST_FILTER:+--filter "$TEST_FILTER" --logger "console;verbosity=detailed"}; rc=$?; [ -n "$OWNER" ] && chown -R "$OWNER" /src/src /src/tests /nuget /out 2>/dev/null; exit $rc'
fi
printf 'source %s\nbuilt %s\n' "$(git -C "$SRC" rev-parse HEAD 2>/dev/null || echo unknown)" "$(date -u +%FT%TZ)" > "$OUT/BUILD-INFO.txt"
[ -n "$VERSION" ] && echo "release v$VERSION" >> "$OUT/BUILD-INFO.txt"
VER=${VERSION:+-p:Version=$VERSION}
: > "$DIST/SHA256SUMS.txt.new"

if [ "$LINUX_ONLY" = 0 ]; then
  echo "== win-x64: self-contained single file =="
  # WPF cross-compiles from Linux with EnableWindowsTargeting.
  run dotnet publish src/AionMeter.App/AionMeter.App.csproj -c Release -r win-x64 --self-contained true \
      -p:EnableWindowsTargeting=true -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en%3Bru \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true $VER -o /out/AION2DpsMeter
  cp "$SRC/LICENSE" "$SRC/INSTALL.txt" "$OUT/BUILD-INFO.txt" "$OUT/AION2DpsMeter/"
  (cd "$OUT/AION2DpsMeter" && find . -type f ! -name SHA256SUMS.txt -exec sha256sum {} + | sort -k2 > SHA256SUMS.txt)
  Z=AION2DpsMeter-$TAG-win-x64.zip; rm -f "$DIST/$Z"
  (cd "$OUT" && python3 -c 'import shutil,sys; shutil.make_archive(sys.argv[1], "zip", ".", "AION2DpsMeter")' "$DIST/${Z%.zip}")
  (cd "$DIST" && sha256sum "$Z") >> "$DIST/SHA256SUMS.txt.new"
fi

# Linux: framework-dependent (the host's .NET 10 runtime runs the dlls; libpcap, GTK 4 and gtk4-layer-shell come from the host too).
echo "== linux-x64: daemon + card =="
run dotnet publish src/AionMeter.Daemon/AionMeter.Daemon.csproj -c Release -r linux-x64 --self-contained false \
    -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en $VER -o /out/AION2DpsMeter-linux/daemon
run dotnet publish src/AionMeter.Overlay/AionMeter.Overlay.csproj -c Release -r linux-x64 --self-contained false \
    -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en $VER -o /out/AION2DpsMeter-linux/overlay
L=$OUT/AION2DpsMeter-linux
cp -r "$SRC/linux/." "$L/"
cp "$SRC/LICENSE" "$OUT/BUILD-INFO.txt" "$L/"
chmod +x "$L/install.sh" "$L/uninstall.sh" "$L/bin/"*
(cd "$L" && find . -type f ! -name SHA256SUMS.txt -exec sha256sum {} + | sort -k2 > SHA256SUMS.txt)
T=AION2DpsMeter-$TAG-linux-x64.tar.gz
tar -C "$OUT" --owner=0 --group=0 -czf "$DIST/$T" AION2DpsMeter-linux
(cd "$DIST" && sha256sum "$T") >> "$DIST/SHA256SUMS.txt.new"
mv "$DIST/SHA256SUMS.txt.new" "$DIST/SHA256SUMS.txt"
echo "== done =="; ls -la "$DIST"
