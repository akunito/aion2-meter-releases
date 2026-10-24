#!/usr/bin/env sh
# Removes the program, commands, menu entry, systemd unit and polkit rule. Keeps ~/.local/share/AionMeter unless --purge.
set -eu
PREFIX=$HOME/.local/opt/aionmeter; PURGE=0
while [ $# -gt 0 ]; do case $1 in --prefix) PREFIX=$2; shift;; --purge) PURGE=1;; *) echo "unknown option: $1" >&2; exit 2;; esac; shift; done
"$PREFIX/bin/aionmeter" --quit 2>/dev/null || true
if [ -f /etc/systemd/system/aion2-meter-daemon.service ]; then
  sudo systemctl stop aion2-meter-daemon.service 2>/dev/null || true
  sudo rm -f /etc/systemd/system/aion2-meter-daemon.service /etc/polkit-1/rules.d/50-aion2-meter.rules
  sudo systemctl daemon-reload
fi
for c in aionmeter aionmeter-toggle aionmeter-clicks; do rm -f "$HOME/.local/bin/$c"; done
rm -f "${XDG_DATA_HOME:-$HOME/.local/share}/applications/aionmeter.desktop"
rm -rf "$PREFIX" "$PREFIX.prev" "$PREFIX.next" "${XDG_CONFIG_HOME:-$HOME/.config}/aionmeter"
if [ "$PURGE" = 1 ]; then rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/AionMeter"; echo "removed, data included"; else echo "removed; your data stays in ${XDG_DATA_HOME:-$HOME/.local/share}/AionMeter (--purge deletes it)"; fi
