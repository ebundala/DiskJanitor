#!/usr/bin/env bash
# Fills in PKGBUILD for one release and copies the files the AUR package ships.
# Usage: packaging/aur/prepare.sh <version> <tarball> <output-dir>
# Then, on Arch (or in an archlinux container): makepkg --printsrcinfo > .SRCINFO
set -euo pipefail

VERSION="${1#v}"
TARBALL="$2"
OUT="$3"
PKG="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"

mkdir -p "$OUT"
cp "$PKG/diskjanitor.desktop" "$PKG/diskjanitor.png" "$OUT/"
sha() { sha256sum "$1" | cut -d' ' -f1; }
sed -e "s/@VERSION@/$VERSION/" \
    -e "s/@TARBALL_SHA256@/$(sha "$TARBALL")/" \
    -e "s/@DESKTOP_SHA256@/$(sha "$OUT/diskjanitor.desktop")/" \
    -e "s/@ICON_SHA256@/$(sha "$OUT/diskjanitor.png")/" \
    "$PKG/aur/PKGBUILD.in" > "$OUT/PKGBUILD"
