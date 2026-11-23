#!/usr/bin/env bash
# Builds the release files for one version:
#   DiskJanitor-linux-x64.tar.gz, diskjanitor_<version>_amd64.deb, DiskJanitor-x86_64.AppImage, SHA256SUMS
# Usage: packaging/build-packages.sh <version> [output-dir]     (version without the leading "v")
set -euo pipefail

VERSION="${1:?usage: $0 <version> [output-dir]}"
VERSION="${VERSION#v}"
ROOT="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
OUT="$(mkdir -p "${2:-$ROOT/dist}" && cd "${2:-$ROOT/dist}" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-x86_64.AppImage"
PKG="$ROOT/packaging"

echo "==> Publishing self-contained binary ($VERSION)"
dotnet publish "$ROOT/DiskJanitor.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -p:Version="$VERSION" -o "$WORK/bin"
BIN="$WORK/bin/DiskJanitor"

echo "==> Tarball"
mkdir -p "$WORK/tar"
cp "$BIN" "$ROOT/LICENSE" "$ROOT/README.md" "$WORK/tar/"
tar -czf "$OUT/DiskJanitor-linux-x64.tar.gz" -C "$WORK/tar" .

echo "==> Debian package"
DEB="$WORK/deb"
install -Dm755 "$BIN" "$DEB/usr/lib/diskjanitor/DiskJanitor"
mkdir -p "$DEB/usr/bin"
ln -s ../lib/diskjanitor/DiskJanitor "$DEB/usr/bin/diskjanitor"
install -Dm644 "$PKG/diskjanitor.desktop" "$DEB/usr/share/applications/diskjanitor.desktop"
install -Dm644 "$PKG/diskjanitor.png" "$DEB/usr/share/icons/hicolor/256x256/apps/diskjanitor.png"
install -Dm644 "$ROOT/LICENSE" "$DEB/usr/share/doc/diskjanitor/copyright"
mkdir -p "$DEB/DEBIAN"
cat > "$DEB/DEBIAN/control" <<EOF
Package: diskjanitor
Version: $VERSION
Architecture: amd64
Maintainer: ebundala <https://github.com/ebundala/DiskJanitor>
Installed-Size: $(du -sk "$DEB/usr" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70
Recommends: pkexec | policykit-1
Section: utils
Priority: optional
Homepage: https://github.com/ebundala/DiskJanitor
Description: Find and delete disk hogs on a developer machine
 Lists project build folders, Docker images, containers, volumes and build
 cache, package manager and app caches, trash and system logs, shows what
 depends on each item, and deletes the ones you pick.
EOF
dpkg-deb --root-owner-group --build "$DEB" "$OUT/diskjanitor_${VERSION}_amd64.deb"

echo "==> AppImage"
APPDIR="$WORK/DiskJanitor.AppDir"
install -Dm755 "$BIN" "$APPDIR/usr/bin/diskjanitor"
install -Dm644 "$PKG/diskjanitor.desktop" "$APPDIR/diskjanitor.desktop"
install -Dm644 "$PKG/diskjanitor.png" "$APPDIR/diskjanitor.png"
install -Dm644 "$PKG/diskjanitor.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/diskjanitor.png"
ln -s usr/bin/diskjanitor "$APPDIR/AppRun"
TOOL="${APPIMAGETOOL:-$WORK/appimagetool}"
if [ ! -x "$TOOL" ]; then
  curl -fsSL -o "$TOOL" "$APPIMAGETOOL_URL"
  chmod +x "$TOOL"
fi
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 VERSION="$VERSION" \
  "$TOOL" --no-appstream "$APPDIR" "$OUT/DiskJanitor-x86_64.AppImage"

echo "==> Checksums"
(cd "$OUT" && sha256sum DiskJanitor-linux-x64.tar.gz "diskjanitor_${VERSION}_amd64.deb" DiskJanitor-x86_64.AppImage > SHA256SUMS)
cat "$OUT/SHA256SUMS"
