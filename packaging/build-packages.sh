#!/usr/bin/env bash
# Build the Linux .deb and .rpm, self-contained like the AppImage.
#
#   packaging/build-packages.sh [version]
#
# Output: publish/virtdeck_<version>-1_amd64.deb and publish/virtdeck-<version>-1.x86_64.rpm
#
# The reason to have these beside the AppImage is the udev rule: a package installs as root, so it
# can put 70-virtdeck-usb.rules in place and reload udev, which an AppImage cannot. Both formats
# come from one nfpm config (packaging/nfpm.yaml), so no rpmbuild or dpkg tooling is needed.
set -euo pipefail

VERSION="${1:-1.0.0}"
RID="linux-x64"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$REPO_ROOT/publish/linuxpkg"
PKGROOT="$BUILD_DIR/root"
# Pinned and checksummed so a new upstream release can't silently change the artifact; bump deliberately.
NFPM_VERSION="2.47.0"
NFPM_SHA256="0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783"
NFPM_URL="https://github.com/goreleaser/nfpm/releases/download/v$NFPM_VERSION/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz"
NFPM="${NFPM:-$BUILD_DIR/nfpm-$NFPM_VERSION/nfpm}"

echo "=== [1/3] Publishing VirtDeck.Avalonia ($RID, self-contained, $VERSION) ==="
rm -rf "$PKGROOT"
dotnet publish "$REPO_ROOT/VirtDeck.Avalonia/VirtDeck.Avalonia.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" \
    -o "$PKGROOT/usr/lib/virtdeck"
find "$PKGROOT/usr/lib/virtdeck" -name '*.pdb' -delete

# LGPL corresponding source for SpiceClient, as in the AppImage and the Windows installer.
tar -czf "$PKGROOT/SpiceClient-src.tar.gz" \
    -C "$REPO_ROOT" --exclude=bin --exclude=obj SpiceClient

echo "=== [2/3] Fetching nfpm $NFPM_VERSION ==="
if [ ! -x "$NFPM" ]; then
    mkdir -p "$(dirname "$NFPM")"
    curl -fsSL -o "$BUILD_DIR/nfpm.tar.gz" "$NFPM_URL"
    echo "$NFPM_SHA256  $BUILD_DIR/nfpm.tar.gz" | sha256sum -c -
    tar -xzf "$BUILD_DIR/nfpm.tar.gz" -C "$(dirname "$NFPM")" nfpm
    rm "$BUILD_DIR/nfpm.tar.gz"
fi

echo "=== [3/3] Building the .deb and .rpm ==="
cd "$REPO_ROOT"
export VERSION
for fmt in deb rpm; do
    "$NFPM" package --config packaging/nfpm.yaml --packager "$fmt" --target publish/
done

echo
echo "=== Done: ==="
ls -1 publish/virtdeck*"$VERSION"*.{deb,rpm}
