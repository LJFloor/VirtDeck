#!/usr/bin/env bash
# Build the Linux release artifact: a self-contained AppImage.
#
#   packaging/build-appimage.sh [version]
#
# Output: publish/VirtDeck-<version>-x86_64.AppImage
#
# AppImage is the primary Linux artifact because it keeps the app on the *host's* libusb and
# libpulse. USB redirection talks to /dev/bus/usb directly and audio goes to the user's running
# PulseAudio/PipeWire daemon; a sandbox (Flatpak, Snap) has to be punched open for both, while an
# AppImage is just a mounted directory with the same access as any other program the user runs.
#
# Only the .NET runtime is bundled (--self-contained). libusb/usbredirhost/libpulse come from the
# distro on purpose: they are the parts that must match the host's kernel drivers and sound daemon,
# and every one of them degrades gracefully when missing (see packaging/README.md).
set -euo pipefail

VERSION="${1:-1.0.0}"
RID="linux-x64"
ARCH="x86_64"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$REPO_ROOT/publish/appimage"
APPDIR="$BUILD_DIR/VirtDeck.AppDir"
OUTPUT="$REPO_ROOT/publish/VirtDeck-$VERSION-$ARCH.AppImage"
# Pinned so a new upstream release can't silently change the artifact; bump deliberately.
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
APPIMAGETOOL="${APPIMAGETOOL:-$BUILD_DIR/appimagetool-$ARCH.AppImage}"

echo "=== [1/5] Publishing VirtDeck.Avalonia ($RID, self-contained, $VERSION) ==="
rm -rf "$APPDIR"
dotnet publish "$REPO_ROOT/VirtDeck.Avalonia/VirtDeck.Avalonia.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" \
    -o "$APPDIR/usr/bin"
# Debug symbols are a third of the payload and nothing reads them from a release AppImage.
find "$APPDIR/usr/bin" -name '*.pdb' -delete

echo "=== [2/5] Laying out the AppDir ==="
install -Dm644 "$REPO_ROOT/packaging/virtdeck.desktop" "$APPDIR/usr/share/applications/virtdeck.desktop"
install -Dm644 "$REPO_ROOT/packaging/io.github.ljfloor.VirtDeck.metainfo.xml" \
    "$APPDIR/usr/share/metainfo/io.github.ljfloor.VirtDeck.metainfo.xml"
# mkdir first: without an existing icons/ directory, cp -r would copy the *contents* of hicolor/
# into it and the theme would end up at usr/share/icons/48x48/ instead of icons/hicolor/48x48/.
mkdir -p "$APPDIR/usr/share/icons"
cp -r "$REPO_ROOT/packaging/icons/hicolor" "$APPDIR/usr/share/icons/hicolor"

# An AppImage also needs the desktop file and the icon at the AppDir root, plus .DirIcon (what
# file managers show for the .AppImage file itself). Real files, not symlinks: appimagetool
# copies them into a squashfs where a dangling link would just vanish.
cp "$REPO_ROOT/packaging/virtdeck.desktop" "$APPDIR/virtdeck.desktop"
cp "$REPO_ROOT/packaging/icons/hicolor/256x256/apps/virtdeck.png" "$APPDIR/virtdeck.png"
cp "$APPDIR/virtdeck.png" "$APPDIR/.DirIcon"

# The udev rule can't be installed from inside an AppImage (no install step, no root), so ship it
# where the user can find it; the USB picker points at this path when libusb_open is denied.
install -Dm644 "$REPO_ROOT/packaging/70-virtdeck-usb.rules" \
    "$APPDIR/usr/share/virtdeck/70-virtdeck-usb.rules"
install -Dm644 "$REPO_ROOT/LICENSE.txt" "$APPDIR/usr/share/virtdeck/LICENSE.txt"
install -Dm644 "$REPO_ROOT/SpiceClient/COPYING.LESSER" "$APPDIR/usr/share/virtdeck/licenses/LGPL-3.0.txt"
install -Dm644 "$REPO_ROOT/SpiceClient/COPYING" "$APPDIR/usr/share/virtdeck/licenses/GPL-3.0.txt"
install -Dm644 "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$APPDIR/usr/share/virtdeck/THIRD-PARTY-NOTICES.md"

# LGPL corresponding source for SpiceClient (the spice-html5-derived component), shipped inside
# the image so the binary is "accompanied by source", the same obligation the Windows installer
# meets with SpiceClient-src.zip. bin/obj are excluded so no build output leaks in.
tar -czf "$APPDIR/usr/share/virtdeck/SpiceClient-src.tar.gz" \
    -C "$REPO_ROOT" --exclude=bin --exclude=obj SpiceClient

echo "=== [3/5] Writing AppRun ==="
# Deliberately minimal: no LD_LIBRARY_PATH. The self-contained publish resolves its own .NET
# libraries relative to the apphost, and libusb/libpulse must come from the host; prepending a
# bundled library path is how AppImages end up with a libusb that can't see the host's devices.
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export VIRTDECK_APPDIR="$HERE"
exec "$HERE/usr/bin/virtdeck" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

echo "=== [4/5] Fetching appimagetool ==="
if [ ! -x "$APPIMAGETOOL" ]; then
    mkdir -p "$(dirname "$APPIMAGETOOL")"
    curl -fsSL -o "$APPIMAGETOOL" "$APPIMAGETOOL_URL"
    chmod +x "$APPIMAGETOOL"
fi

echo "=== [5/5] Building $OUTPUT ==="
rm -f "$OUTPUT"
# appimagetool is itself an AppImage, so it needs FUSE to run; extract-and-run works on CI
# containers and on hosts without libfuse2 alike.
export APPIMAGE_EXTRACT_AND_RUN=1
ARCH="$ARCH" "$APPIMAGETOOL" "$APPDIR" "$OUTPUT"

echo
echo "=== Done: $OUTPUT ==="
