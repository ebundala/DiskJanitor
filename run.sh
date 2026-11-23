#!/usr/bin/env bash
# Start Disk Janitor as the current user. Pass --report to print a scan in the terminal instead.
DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
exec "$(command -v dotnet)" "$DIR/publish/DiskJanitor.dll" "$@"
