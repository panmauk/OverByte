#!/usr/bin/env bash
# OverByte — Linux build + packaging, from scratch.
#
# Produces in linux/dist/:
#   overbyte_<ver>_amd64.deb      overbyte_<ver>_arm64.deb
#   OverByte-<ver>-x86_64.AppImage  OverByte-<ver>-aarch64.AppImage
#   SHA256SUMS
#
# Requirements (no root needed):
#   - .NET SDK 10  (DOTNET_ROOT, default ~/.dotnet)
#   - appimagetool (APPIMAGETOOL, default ~/lvtools/appimagetool-x86_64.AppImage)
#   - AppImage runtimes (RUNTIME_X64 / RUNTIME_ARM64, default ~/lvtools/runtime-{x86_64,aarch64})
#   - dpkg-deb (fakeroot is used when present)
#   - network access for NuGet restore (Avalonia + runtime packs) on the first run
#
# Building on a slow drive (e.g. /mnt/d in WSL) is avoided: sources are copied to $WORK
# (default ~/build/overbyte), built there, and only the artifacts are copied back.
#
# Usage:  ./build-linux.sh [x64|arm64|all]      (default: all)
#         SKIP_APPIMAGE=1 ./build-linux.sh      (debs only)

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(dirname "$HERE")"
DIST="$HERE/dist"
WORK="${WORK:-$HOME/build/overbyte}"
TARGETS="${1:-all}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export APPIMAGE_EXTRACT_AND_RUN=1

APPIMAGETOOL="${APPIMAGETOOL:-$HOME/lvtools/appimagetool-x86_64.AppImage}"
RUNTIME_X64="${RUNTIME_X64:-$HOME/lvtools/runtime-x86_64}"
RUNTIME_ARM64="${RUNTIME_ARM64:-$HOME/lvtools/runtime-aarch64}"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$HERE/OverByte.Linux.csproj" | head -1)"
VERSION="${VERSION:-1.0.0}"

MAINTAINER="Panmox <panmauk@users.noreply.github.com>"
HOMEPAGE="https://panmox.org"

# Shared-library dependencies Avalonia/SkiaSharp/.NET load at runtime (verified with ldd on libSkiaSharp.so,
# libHarfBuzzSharp.so and Avalonia's X11 backend, see the verification notes in the repo history).
DEPENDS="libc6 (>= 2.31), libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libfreetype6, libice6, libsm6, libx11-6, libxext6, libxrandr2, libxi6, libxcursor1"
RECOMMENDS="xdg-desktop-portal, fonts-dejavu-core | fonts-liberation | fonts-noto-core"

case "$TARGETS" in
  all) RIDS=(x64 arm64) ;;
  x64|arm64) RIDS=("$TARGETS") ;;
  *) echo "usage: $0 [x64|arm64|all]" >&2; exit 2 ;;
esac

command -v dotnet >/dev/null || { echo "dotnet not found (set DOTNET_ROOT)" >&2; exit 1; }
command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found" >&2; exit 1; }

echo "==> OverByte $VERSION  targets: ${RIDS[*]}  work: $WORK"

# ── 1. Stage sources on a fast filesystem ─────────────────────────────────────
rm -rf "$WORK/src"
mkdir -p "$WORK/src/linux" "$WORK/src/Core" "$DIST"
cp "$REPO"/Core/*.cs "$WORK/src/Core/"
cp "$HERE"/*.cs "$HERE"/OverByte.Linux.csproj "$WORK/src/linux/"
cp -r "$HERE/Assets" "$WORK/src/linux/Assets"
cp "$REPO/LICENSE" "$WORK/src/LICENSE"

# ── 2. Publish (self-contained, so no system .NET is needed) ──────────────────
for arch in "${RIDS[@]}"; do
  rid="linux-$arch"
  echo "==> dotnet publish $rid"
  rm -rf "$WORK/publish/$rid"
  dotnet publish "$WORK/src/linux/OverByte.Linux.csproj" -c Release -r "$rid" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -p:PublishSingleFile=false -p:GenerateDocumentationFile=false \
    -o "$WORK/publish/$rid" -v minimal -nologo
  chmod 755 "$WORK/publish/$rid/overbyte"
done

# ── packaging helpers ─────────────────────────────────────────────────────────
write_desktop() { # $1 = path, $2 = Exec value
cat > "$1" <<EOF
[Desktop Entry]
Type=Application
Name=OverByte
GenericName=File Inflator
Comment=Inflate files and folders to a target size while keeping them valid
Exec=$2
Icon=overbyte
Terminal=false
Categories=Utility;FileTools;
Keywords=inflate;file;size;padding;zip;png;jpeg;pdf;
StartupWMClass=overbyte
StartupNotify=true
EOF
}

build_deb() { # $1 = arch (x64|arm64)
  local arch="$1" darch
  [ "$arch" = x64 ] && darch=amd64 || darch=arm64
  local pkg="$WORK/deb-$darch"
  echo "==> .deb ($darch)"
  rm -rf "$pkg"
  mkdir -p "$pkg/DEBIAN" "$pkg/opt/overbyte" "$pkg/usr/bin" "$pkg/usr/share/applications" "$pkg/usr/share/doc/overbyte"
  cp -r "$WORK/publish/linux-$arch/." "$pkg/opt/overbyte/"

  cat > "$pkg/usr/bin/overbyte" <<'EOF'
#!/bin/sh
exec /opt/overbyte/overbyte "$@"
EOF
  write_desktop "$pkg/usr/share/applications/overbyte.desktop" "overbyte %f"

  for s in 256 128 48; do
    mkdir -p "$pkg/usr/share/icons/hicolor/${s}x${s}/apps"
    cp "$HERE/Assets/overbyte-$s.png" "$pkg/usr/share/icons/hicolor/${s}x${s}/apps/overbyte.png"
  done

  cat > "$pkg/usr/share/doc/overbyte/copyright" <<EOF
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: OverByte
Source: $HOMEPAGE

Files: *
Copyright: Panmox
License: GPL-3.0
 This program is free software: you can redistribute it and/or modify it under
 the terms of the GNU General Public License as published by the Free Software
 Foundation, version 3.
 .
 On Debian systems, the complete text of the GNU General Public License
 version 3 can be found in /usr/share/common-licenses/GPL-3.
EOF
  cat > "$pkg/usr/share/doc/overbyte/changelog.Debian" <<EOF
overbyte ($VERSION) stable; urgency=medium

  * Linux release (Avalonia UI), shared inflation core with the Windows version.

 -- $MAINTAINER  $(date -R)
EOF
  gzip -9n "$pkg/usr/share/doc/overbyte/changelog.Debian"

  cat > "$pkg/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "configure" ] || [ "$1" = "abort-upgrade" ]; then
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database -q /usr/share/applications || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
    fi
fi
exit 0
EOF
  cat > "$pkg/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database -q /usr/share/applications || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
    fi
fi
exit 0
EOF

  # permissions: dirs 755, files 644, executables 755
  find "$pkg" -type d -exec chmod 755 {} +
  find "$pkg" -type f -exec chmod 644 {} +
  chmod 755 "$pkg/DEBIAN/postinst" "$pkg/DEBIAN/postrm" "$pkg/usr/bin/overbyte" "$pkg/opt/overbyte/overbyte"
  # keep any other ELF executables executable (shared objects stay 644 like normal packages)
  while IFS= read -r f; do
    if head -c4 "$f" | grep -q 'ELF' && [ "${f##*.}" != "so" ]; then chmod 755 "$f"; fi
  done < <(find "$pkg/opt/overbyte" -type f ! -name '*.so' ! -name '*.dll' ! -name '*.json')

  local size_kb
  size_kb=$(du -sk --exclude=DEBIAN "$pkg" | cut -f1)
  cat > "$pkg/DEBIAN/control" <<EOF
Package: overbyte
Version: $VERSION
Section: utils
Priority: optional
Architecture: $darch
Installed-Size: $size_kb
Depends: $DEPENDS
Recommends: $RECOMMENDS
Maintainer: $MAINTAINER
Homepage: $HOMEPAGE
Description: Reverse compression - inflate files to a target size
 OverByte inflates a file or a whole folder to any target size while keeping
 it valid: PNG, JPEG, MP4/MOV, PDF, ZIP-based documents and archives, or any
 other file type. Folders can be packed into one big ZIP or inflated file by
 file. Self-contained; no .NET runtime is required.
 .
 License: GPL-3.0
EOF
  (cd "$pkg" && find . -type f ! -path './DEBIAN/*' -printf '%P\0' | sort -z | xargs -0 md5sum > DEBIAN/md5sums)
  chmod 644 "$pkg/DEBIAN/md5sums"

  local out="$DIST/overbyte_${VERSION}_${darch}.deb"
  rm -f "$out"
  if command -v fakeroot >/dev/null 2>&1; then
    fakeroot dpkg-deb -Zxz -z9 --build "$pkg" "$out" >/dev/null
  else
    dpkg-deb --root-owner-group -Zxz -z9 --build "$pkg" "$out" >/dev/null
  fi
  echo "    $out"
}

build_appimage() { # $1 = arch (x64|arm64)
  local arch="$1" aarch runtime
  if [ "$arch" = x64 ]; then aarch=x86_64; runtime="$RUNTIME_X64"; else aarch=aarch64; runtime="$RUNTIME_ARM64"; fi
  echo "==> AppImage ($aarch)"
  [ -f "$APPIMAGETOOL" ] || { echo "appimagetool not found: $APPIMAGETOOL" >&2; return 1; }
  [ -f "$runtime" ] || { echo "runtime not found: $runtime" >&2; return 1; }
  local ad="$WORK/AppDir-$aarch"
  rm -rf "$ad"
  mkdir -p "$ad/usr/lib/overbyte" "$ad/usr/share/icons/hicolor/256x256/apps"
  cp -r "$WORK/publish/linux-$arch/." "$ad/usr/lib/overbyte/"
  chmod 755 "$ad/usr/lib/overbyte/overbyte"
  cp "$HERE/Assets/overbyte-256.png" "$ad/overbyte.png"
  cp "$HERE/Assets/overbyte-256.png" "$ad/.DirIcon"
  cp "$HERE/Assets/overbyte-256.png" "$ad/usr/share/icons/hicolor/256x256/apps/overbyte.png"
  write_desktop "$ad/overbyte.desktop" "overbyte %f"
  chmod 644 "$ad/overbyte.png" "$ad/.DirIcon" "$ad/overbyte.desktop" "$ad/usr/share/icons/hicolor/256x256/apps/overbyte.png"
  cat > "$ad/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/overbyte/overbyte" "$@"
EOF
  chmod 755 "$ad/AppRun"
  local out="$DIST/OverByte-${VERSION}-${aarch}.AppImage"
  rm -f "$out"
  ARCH="$aarch" "$APPIMAGETOOL" --no-appstream --runtime-file "$runtime" "$ad" "$out" >/dev/null
  chmod 755 "$out"
  echo "    $out"
}

for arch in "${RIDS[@]}"; do
  build_deb "$arch"
  if [ -z "${SKIP_APPIMAGE:-}" ]; then build_appimage "$arch"; fi
done

(cd "$DIST" && sha256sum overbyte_*.deb OverByte-*.AppImage 2>/dev/null > SHA256SUMS || true)
echo "==> done. Artifacts in $DIST"
ls -l "$DIST"
