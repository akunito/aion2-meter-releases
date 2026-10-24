# NixOS module for the Linux meter. The program itself (daemon/ + overlay/) is the release tarball unpacked by
# `./install.sh --no-system` into ~/.local/opt/aionmeter; this module provides what install.sh would write as root, with
# Nix store paths: the capture unit, the polkit rule and the launchers (dotnet, libpcap, GTK 4 and gtk4-layer-shell on their paths).
#
#   imports = [ ./aion2-meter.nix ];
#   programs.aion2-meter = { enable = true; user = "alice"; };   # optional: layer = true; monitor = "DP-1";
#
# Updates: unpack the new tarball and run `./install.sh --no-system` again; the module needs no change.
{ config, lib, pkgs, ... }:
let
  cfg = config.programs.aion2-meter;
  home = config.users.users.${cfg.user}.home;
  prefix = if cfg.prefix != null then cfg.prefix else "${home}/.local/opt/aionmeter";
  dataDir = if cfg.dataDir != null then cfg.dataDir else "${home}/.local/share/AionMeter";
  unit = "aion2-meter-daemon.service";
  socketPath = "/run/aionmeter/aionmeter.sock";
  cardArgs = lib.optionals cfg.layer ([ "--layer" ] ++ lib.optionals (cfg.monitor != null) [ "--monitor" cfg.monitor ]);
  # questlog's skill icons are WebP: gdk-pixbuf has png/jpeg built in but WebP needs the loader, and GDK_PIXBUF_MODULE_FILE
  # replaces the whole cache, so one file lists the stock loaders plus webp.
  pixbufLoaders = pkgs.runCommand "aionmeter-pixbuf-loaders" { } ''
    { cat ${pkgs.gdk-pixbuf}/lib/gdk-pixbuf-2.0/2.10.0/loaders.cache
      ${pkgs.gdk-pixbuf.dev}/bin/gdk-pixbuf-query-loaders ${pkgs.webp-pixbuf-loader}/lib/gdk-pixbuf-2.0/2.10.0/loaders/libpixbufloader-webp.so | grep -v '^#'; } > $out
  '';
  livePid = ''
    run=''${XDG_RUNTIME_DIR:-/run/user/$(id -u)}
    live_pid() {
      [ -f "$run/aionmeter-overlay.pid" ] || return 1
      p=$(cat "$run/aionmeter-overlay.pid" 2>/dev/null) || return 1
      case $p in ""|*[!0-9]*) return 1;; esac
      [ -r "/proc/$p/cmdline" ] && tr '\0' ' ' < "/proc/$p/cmdline" | grep -q AionMeter.Overlay.dll || return 1
      echo "$p"
    }
  '';
  aionmeter = pkgs.writeShellScriptBin "aionmeter" ''
    dll=${prefix}/overlay/AionMeter.Overlay.dll
    if [ ! -f "$dll" ]; then
      ${pkgs.libnotify}/bin/notify-send -a "AION 2 meter" aionmeter "Not installed: $dll (unpack the release and run ./install.sh --no-system)"
      exit 1
    fi
    # GirCore opens every library by soname. getLib: glib/pango/fontconfig are split outputs.
    export LD_LIBRARY_PATH=${lib.makeLibraryPath (map lib.getLib (with pkgs; [
      gtk4 gtk4-layer-shell glib cairo pango gdk-pixbuf graphene harfbuzz fontconfig freetype wayland
    ]))}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
    # GTK's own file chooser aborts the process without the FileChooser schema ("No GSettings schemas are installed").
    export XDG_DATA_DIRS=${pkgs.gtk4}/share/gsettings-schemas/${pkgs.gtk4.name}:${pkgs.gsettings-desktop-schemas}/share/gsettings-schemas/${pkgs.gsettings-desktop-schemas.name}''${XDG_DATA_DIRS:+:$XDG_DATA_DIRS}
    export GDK_PIXBUF_MODULE_FILE=${pixbufLoaders}
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    # Sway: a shell started before a sway restart carries a stale SWAYSOCK; never take the newest socket blindly
    # (nested headless sways are newer than the session's).
    if [ -z "''${SWAYSOCK:-}" ] || [ ! -S "$SWAYSOCK" ]; then
      for s in $(ls -t /run/user/"$(id -u)"/sway-ipc.*.sock 2>/dev/null); do
        pid=''${s%.sock}; pid=''${pid##*.}
        [ -r /proc/"$pid"/environ ] || continue
        if ! tr '\0' '\n' < /proc/"$pid"/environ 2>/dev/null | grep -q '^WLR_BACKENDS=headless'; then export SWAYSOCK="$s"; break; fi
      done
    fi
    run_card() { ${pkgs.dotnet-runtime_10}/bin/dotnet "$dll" --socket ${socketPath} ${lib.escapeShellArgs cardArgs} "$@"; }
    ${livePid}
    if live_pid >/dev/null; then run_card "$@"; exit; fi
    case " $* " in *" --quit "*|*" --hide "*) run_card "$@"; exit;; esac
    sc() { ${pkgs.systemd}/bin/systemctl --no-block --no-ask-password "$1" ${unit}; }
    sc start || ${pkgs.libnotify}/bin/notify-send -a "AION 2 meter" aionmeter "Could not start ${unit}"
    trap 'sc stop' EXIT
    trap 'exit 143' TERM HUP INT
    run_card "$@"
  '';
  aionmeterToggle = pkgs.writeShellScriptBin "aionmeter-toggle" ''
    ${livePid}
    if p=$(live_pid); then exec kill -USR2 "$p"; fi
    exec setsid -f ${aionmeter}/bin/aionmeter </dev/null >/dev/null 2>&1
  '';
  aionmeterClicks = pkgs.writeShellScriptBin "aionmeter-clicks" ''
    ${livePid}
    if p=$(live_pid); then exec kill -USR1 "$p"; fi
    echo "aionmeter: the card is not running" >&2; exit 1
  '';
in
{
  options.programs.aion2-meter = {
    enable = lib.mkEnableOption "the AION 2 DPS meter (capture daemon + GTK 4 card)";
    user = lib.mkOption { type = lib.types.str; description = "The user who plays: the daemon runs as this user, with the capture capabilities."; };
    prefix = lib.mkOption { type = lib.types.nullOr lib.types.str; default = null; description = "Where install.sh put the program (default ~/.local/opt/aionmeter)."; };
    dataDir = lib.mkOption { type = lib.types.nullOr lib.types.str; default = null; description = "Data directory (default ~/.local/share/AionMeter)."; };
    layer = lib.mkOption { type = lib.types.bool; default = false; description = "Layer-shell card (above a fullscreen game on wlroots compositors / KDE) instead of a window."; };
    monitor = lib.mkOption { type = lib.types.nullOr lib.types.str; default = null; description = "Output for the layer-shell card, e.g. DP-1."; };
    forward = lib.mkOption { type = lib.types.nullOr lib.types.str; default = null; example = "127.0.0.1:47013"; description = "Also stream the captured packets (plaintext, no auth) to a Windows meter under Wine or in a VM. Never 0.0.0.0."; };
  };

  config = lib.mkIf cfg.enable {
    # ReadWritePaths of a missing directory fails the unit's namespace setup.
    systemd.tmpfiles.rules = [ "d ${dataDir} 0755 ${cfg.user} ${config.users.users.${cfg.user}.group} -" ];
    environment.systemPackages = [ aionmeter aionmeterToggle aionmeterClicks ];
    # The card's CSS asks for Inter, then Noto Sans / DejaVu Sans.
    fonts.packages = [ pkgs.inter ];
    security.polkit.enable = true;
    security.polkit.extraConfig = ''
      polkit.addRule(function(action, subject) {
        if (action.id == "org.freedesktop.systemd1.manage-units" &&
            action.lookup("unit") == "${unit}" && subject.user == "${cfg.user}") {
          var verb = action.lookup("verb");
          if (verb == "start" || verb == "stop" || verb == "restart" || verb == "try-restart") return polkit.Result.YES;
        }
      });
    '';
    # A system unit with User= because a user unit cannot be granted the capture capabilities. Same uid as the game, so its
    # /proc/<pid>/fd is readable without CAP_SYS_PTRACE. Started by the launcher only (no wantedBy).
    systemd.services.aion2-meter-daemon = {
      description = "AION 2 DPS meter daemon (passive capture)";
      unitConfig.ConditionPathExists = "${prefix}/daemon/aionmeter-daemon.dll";
      # SharpPcap dlopens libpcap.so. getLib: ${pkgs.libpcap}/lib holds only pkgconfig (the daemon then says "libpcap missing").
      environment = { LD_LIBRARY_PATH = "${lib.getLib pkgs.libpcap}/lib"; DOTNET_CLI_TELEMETRY_OPTOUT = "1"; DOTNET_NOLOGO = "1"; HOME = home; };
      serviceConfig = {
        Type = "simple";
        User = cfg.user;
        Group = config.users.users.${cfg.user}.group;
        WorkingDirectory = "${prefix}/daemon";
        ExecStart = lib.escapeShellArgs ([ "${pkgs.dotnet-runtime_10}/bin/dotnet" "${prefix}/daemon/aionmeter-daemon.dll" "--data-dir" dataDir "--socket" socketPath ]
          ++ lib.optionals (cfg.forward != null) [ "--forward" cfg.forward ]);
        RuntimeDirectory = "aionmeter";
        RuntimeDirectoryMode = "0750";
        AmbientCapabilities = [ "CAP_NET_RAW" "CAP_NET_ADMIN" ];
        CapabilityBoundingSet = [ "CAP_NET_RAW" "CAP_NET_ADMIN" ];
        KillSignal = "SIGTERM";
        TimeoutStopSec = 20;
        Restart = "on-failure";
        RestartSec = 10;
        Nice = 5;
        ProtectSystem = "strict";
        ReadWritePaths = [ dataDir ];
        PrivateTmp = true;
        ProtectKernelTunables = true;
        ProtectKernelModules = true;
        ProtectControlGroups = true;
        RestrictSUIDSGID = true;
        LockPersonality = true;
      };
    };
  };
}
