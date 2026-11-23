#!/usr/bin/env bash
# Start Disk Janitor as root (via sudo) while still cleaning *your* home folder.
DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
DOTNET="$(readlink -f "$(command -v dotnet)")"

# Let root draw windows on this user's display.
command -v xhost >/dev/null && xhost +SI:localuser:root >/dev/null

vars=("DISKJANITOR_USER=$USER" "DOTNET_ROOT=$(dirname "$DOTNET")" "NO_AT_BRIDGE=1")
[ -n "$DISPLAY" ] && vars+=("DISPLAY=$DISPLAY")
[ -n "$XAUTHORITY" ] && vars+=("XAUTHORITY=$XAUTHORITY")
if [ -n "$WAYLAND_DISPLAY" ]; then
  case "$WAYLAND_DISPLAY" in
    /*) vars+=("WAYLAND_DISPLAY=$WAYLAND_DISPLAY") ;;
    *)  vars+=("WAYLAND_DISPLAY=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/$WAYLAND_DISPLAY") ;;
  esac
fi

exec sudo env "${vars[@]}" "$DOTNET" "$DIR/publish/DiskJanitor.dll" "$@"
