#!/usr/bin/env sh
# Installs (or updates) the Linux meter for the current user. Run it from the unpacked tarball, as yourself (it asks for sudo once):
#   ./install.sh [--layer [--monitor NAME]] [--prefix DIR] [--no-system]
#   --layer       the card is a layer-shell overlay (stays above a fullscreen game on wlroots compositors / KDE) instead of a window
#   --monitor     output for the --layer card (e.g. DP-1; default: the focused one)
#   --prefix      where the program goes (default ~/.local/opt/aionmeter)
#   --no-system   skip the root part (systemd unit + polkit rule), e.g. on NixOS where the module in nixos/ provides it
# Files: program in the prefix, commands in ~/.local/bin, data in ~/.local/share/AionMeter (kept by updates and by uninstall.sh).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
PREFIX=$HOME/.local/opt/aionmeter; SYSTEM=1; LAYER=0; MONITOR=""
while [ $# -gt 0 ]; do case $1 in
  --layer) LAYER=1;; --monitor) MONITOR=$2; shift;; --prefix) PREFIX=$2; shift;; --no-system) SYSTEM=0;;
  -h|--help) sed -n 2,9p "$0"; exit 0;; *) echo "unknown option: $1" >&2; exit 2;; esac; shift; done
[ "$(id -u)" = 0 ] && { echo "run install.sh as your normal user (the one that plays), not as root" >&2; exit 1; }
[ -f "$HERE/daemon/aionmeter-daemon.dll" ] && [ -f "$HERE/overlay/AionMeter.Overlay.dll" ] || { echo "run install.sh from the unpacked AION2DpsMeter-linux folder" >&2; exit 1; }
USER_NAME=$(id -un); GROUP_NAME=$(id -gn)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}/AionMeter

missing=""
DOTNET=$(command -v dotnet 2>/dev/null || true); [ -z "$DOTNET" ] && [ -x "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" ] && DOTNET=${DOTNET_ROOT:-$HOME/.dotnet}/dotnet
if [ -z "$DOTNET" ] || ! "$DOTNET" --list-runtimes 2>/dev/null | grep -q '^Microsoft.NETCore.App 10\.'; then missing="$missing .NET-10-runtime"; fi
lib() { ldconfig -p 2>/dev/null | grep -q "$1"; }
lib 'libpcap\.so' || missing="$missing libpcap"
lib 'libgtk-4\.so\.1' || missing="$missing GTK-4"
[ "$LAYER" = 1 ] && ! lib 'libgtk4-layer-shell\.so' && missing="$missing gtk4-layer-shell"
if [ -n "$missing" ]; then
  echo "Missing:$missing"
  cat <<'EOF'
Install them with your package manager, then run install.sh again:
  Arch / CachyOS / EndeavourOS:         sudo pacman -S dotnet-runtime libpcap gtk4 gtk4-layer-shell webp-pixbuf-loader
  Fedora 43:                            sudo dnf install dotnet-runtime-10.0 libpcap gtk4 gtk4-layer-shell webp-pixbuf-loader
  Ubuntu 25.10+:                        sudo apt install dotnet-runtime-10.0 libpcap0.8t64 libgtk-4-1 libgtk4-layer-shell0 webp-pixbuf-loader
  Debian 13:                            sudo apt install libpcap0.8t64 libgtk-4-1 libgtk4-layer-shell0 webp-pixbuf-loader  (+ .NET below)
  .NET where the distro has none:       https://learn.microsoft.com/dotnet/core/install/linux (dotnet-install.sh --runtime dotnet --channel 10.0)
gtk4-layer-shell is only needed for --layer; webp-pixbuf-loader only for questlog's WebP skill icons.
EOF
  exit 1
fi
DOTNET=$(readlink -f "$DOTNET")

echo "== program -> $PREFIX"
mkdir -p "$(dirname "$PREFIX")" "$DATA" "$HOME/.local/bin"
NEXT=$PREFIX.next; rm -rf "$NEXT"; mkdir -p "$NEXT"
cp -r "$HERE/daemon" "$HERE/overlay" "$HERE/bin" "$HERE/share" "$NEXT/"
cp "$HERE/BUILD-INFO.txt" "$HERE/LICENSE" "$NEXT/" 2>/dev/null || true
chmod +x "$NEXT/bin/"*
if [ -d "$PREFIX" ]; then rm -rf "$PREFIX.prev"; mv "$PREFIX" "$PREFIX.prev"; fi
mv "$NEXT" "$PREFIX"
for c in aionmeter aionmeter-toggle aionmeter-clicks; do ln -sf "$PREFIX/bin/$c" "$HOME/.local/bin/$c"; done

CONF=${XDG_CONFIG_HOME:-$HOME/.config}/aionmeter
mkdir -p "$CONF"
if [ "$LAYER" = 1 ]; then echo "CARD_ARGS=\"--layer${MONITOR:+ --monitor $MONITOR}\"" > "$CONF/launcher.conf"
elif [ ! -f "$CONF/launcher.conf" ]; then echo 'CARD_ARGS=""' > "$CONF/launcher.conf"; fi
[ "$PREFIX" = "$HOME/.local/opt/aionmeter" ] || echo "AIONMETER_PREFIX=\"$PREFIX\"" >> "$CONF/launcher.conf"

APPS=${XDG_DATA_HOME:-$HOME/.local/share}/applications; mkdir -p "$APPS"
sed -e "s|@BIN@|$PREFIX/bin/aionmeter|g" -e "s|@ICON@|$PREFIX/share/aionmeter.png|g" "$HERE/share/aionmeter.desktop" > "$APPS/aionmeter.desktop"

if [ "$SYSTEM" = 1 ]; then
  echo "== systemd unit + polkit rule (sudo)"
  TMP=$(mktemp -d)
  sed -e "s|@USER@|$USER_NAME|g" -e "s|@GROUP@|$GROUP_NAME|g" -e "s|@HOME@|$HOME|g" -e "s|@DOTNET@|$DOTNET|g" \
      -e "s|@PREFIX@|$PREFIX|g" -e "s|@DATA@|$DATA|g" "$HERE/share/aion2-meter-daemon.service" > "$TMP/aion2-meter-daemon.service"
  sed -e "s|@USER@|$USER_NAME|g" "$HERE/share/50-aion2-meter.rules" > "$TMP/50-aion2-meter.rules"
  sudo install -m 0644 "$TMP/aion2-meter-daemon.service" /etc/systemd/system/aion2-meter-daemon.service
  sudo install -d -m 0755 /etc/polkit-1/rules.d
  sudo install -m 0644 "$TMP/50-aion2-meter.rules" /etc/polkit-1/rules.d/50-aion2-meter.rules
  rm -rf "$TMP"
  sudo systemctl daemon-reload
  # An update while the daemon runs: restart it on the new dll (the card keeps running and reconnects).
  sudo systemctl try-restart aion2-meter-daemon.service || true
fi

cat <<EOF

Installed $(sed -n 's/^release //p' "$PREFIX/BUILD-INFO.txt" 2>/dev/null || echo "(dev build)").
Start:   aionmeter          (or "AION 2 DPS Meter" in your app menu; it starts the capture daemon and stops it on exit)
Keys:    bind aionmeter-toggle (show/hide) and aionmeter-clicks (click-through) in your compositor
Data:    $DATA
Logs:    journalctl -u aion2-meter-daemon -f
EOF
case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "Note: ~/.local/bin is not on your PATH; add it or use $PREFIX/bin/aionmeter";; esac
