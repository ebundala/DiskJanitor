#!/usr/bin/env bash
# Adds "Disk Janitor" to your application menu (current user only).
DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
APPS="$HOME/.local/share/applications"
mkdir -p "$APPS"
cat > "$APPS/disk-janitor.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Disk Janitor
Comment=Find and delete build folders, Docker leftovers and caches
Exec=$DIR/run.sh
Icon=drive-harddisk
Terminal=false
Categories=Utility;System;
EOF
echo "Installed $APPS/disk-janitor.desktop"
