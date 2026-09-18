#!/bin/sh
# Runs inside an Alpine container for one architecture (see build.sh) and writes
# /work/agent-<arch>.tar.gz: a fully static x11vnc plus vdrelay, both linked against musl.
#
# Inputs are the pinned tarballs in /work/src (checked against /work/SOURCES.sha256 by build.sh)
# and Alpine's own packages. Alpine ships static archives for most of the X libraries x11vnc
# needs; the four it does not (libXau, libXdamage, libXrandr, libXinerama) are built here into a
# private prefix that comes first on every search path.
set -eu

ARCH=$(uname -m)
PREFIX=/opt/vd
BUILD=/tmp/build
JOBS=$(nproc)
OUT=/work/agent-$ARCH.tar.gz

log() { printf '\n==== %s (%s)\n' "$1" "$ARCH"; }

log "packages"
apk add --no-cache \
	build-base autoconf automake libtool pkgconf cmake linux-headers file xz tar gzip \
	xorgproto util-macros \
	libx11-dev libx11-static libxcb-dev libxcb-static libxdmcp-dev libxau-dev \
	libxext-dev libxext-static libxtst-dev libxtst-static libxfixes-dev libxrender-dev \
	libxi-dev libxi-static \
	zlib-dev zlib-static libjpeg-turbo-dev libjpeg-turbo-static

mkdir -p "$BUILD" "$PREFIX"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig:$PREFIX/lib64/pkgconfig"
export CFLAGS="-O2 -fPIC"
export CPPFLAGS="-I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib -L$PREFIX/lib64"

# ---- the X libraries Alpine ships no static archive for -------------------------------------
for lib in libXau-1.0.12 libXdamage-1.1.7 libXrandr-1.5.5 libXinerama-1.1.6; do
	log "$lib"
	tar -C "$BUILD" -xJf "/work/src/$lib.tar.xz"
	(
		cd "$BUILD/$lib"
		./configure --prefix="$PREFIX" --enable-static --disable-shared >/dev/null
		make -j"$JOBS" >/dev/null
		make install >/dev/null
	)
done

# ---- libvncserver ------------------------------------------------------------------------------
# Only what x11vnc needs to talk RFB over a socket it is handed: zlib and JPEG for the Tight
# encoding, threads. No TLS, no crypto backend (SSH is the transport), no websockets, no examples.
log "libvncserver"
tar -C "$BUILD" -xzf /work/src/LibVNCServer-0.9.15.tar.gz
# LibVNCServer 0.9.15 declares cmake_minimum_required(VERSION 3.4), which CMake 4 no longer
# accepts; CMAKE_POLICY_VERSION_MINIMUM is CMake's own documented way past that.
cmake -S "$BUILD/libvncserver-LibVNCServer-0.9.15" -B "$BUILD/vnc" \
	-DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
	-DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_INSTALL_LIBDIR=lib \
	-DBUILD_SHARED_LIBS=OFF \
	-DWITH_ZLIB=ON -DWITH_JPEG=ON -DWITH_THREADS=ON -DWITH_24BPP=ON -DWITH_IPv6=ON \
	-DWITH_LZO=OFF -DWITH_PNG=OFF -DWITH_SDL=OFF -DWITH_GTK=OFF -DWITH_QT=OFF \
	-DWITH_LIBSSHTUNNEL=OFF -DWITH_GNUTLS=OFF -DWITH_OPENSSL=OFF -DWITH_GCRYPT=OFF \
	-DWITH_SYSTEMD=OFF -DWITH_FFMPEG=OFF -DWITH_WEBSOCKETS=OFF -DWITH_SASL=OFF -DWITH_XCB=OFF \
	-DWITH_EXAMPLES=OFF -DWITH_TESTS=OFF >/dev/null
cmake --build "$BUILD/vnc" -j"$JOBS" >/dev/null
cmake --install "$BUILD/vnc" >/dev/null

# ---- x11vnc ------------------------------------------------------------------------------------
# Every configure link test runs with the whole static X chain in LIBS, because a static -lX11
# resolves nothing on its own: it needs xcb, Xau and Xdmcp after it, in that order.
log "x11vnc"
tar -C "$BUILD" -xzf /work/src/x11vnc-0.9.17.tar.gz
XLIBS=$(pkg-config --static --libs xtst xext xfixes xdamage xrandr xrender xinerama xi x11)
(
	cd "$BUILD/x11vnc-0.9.17"
	autoreconf -fi >/dev/null 2>&1
	PKG_CONFIG="pkg-config --static" LDFLAGS="$LDFLAGS -static" LIBS="$XLIBS" \
	./configure --prefix="$PREFIX" \
		--without-ssl --without-crypto --without-crypt --without-avahi --without-drm \
		--without-xcomposite --without-xtrap --without-xrecord --without-fbpm --without-dpms \
		--without-v4l --without-fbdev --without-uinput --without-macosx-native \
		--without-colormultipointer >/dev/null
	make -j"$JOBS" >/dev/null
)
cp "$BUILD/x11vnc-0.9.17/src/x11vnc" "$BUILD/x11vnc"

# ---- vdrelay -----------------------------------------------------------------------------------
log "vdrelay"
gcc -static -O2 -Wall -Wextra -o "$BUILD/vdrelay" /work/vdrelay.c

# ---- check and pack ----------------------------------------------------------------------------
log "pack"
strip "$BUILD/x11vnc" "$BUILD/vdrelay"
for b in x11vnc vdrelay; do
	desc=$(file -b "$BUILD/$b")
	echo "$b: $desc"
	case $desc in
		*"statically linked"* | *"static-pie linked"*) ;;
		*) echo "error: $b is not static" >&2; exit 1 ;;
	esac
done
"$BUILD/x11vnc" -version

mkdir -p "$BUILD/pkg"
install -m 755 "$BUILD/x11vnc" "$BUILD/vdrelay" "$BUILD/pkg/"
# What the Remote Control module names in the status bar ("x11vnc: 0.9.17 lastmod: ...").
"$BUILD/x11vnc" -version 2>/dev/null | head -n 1 > "$BUILD/pkg/version"
chmod 644 "$BUILD/pkg/version"
# Normalised so the same inputs give the same bytes, and so the same hash on the host.
tar -C "$BUILD/pkg" --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner \
	-cf - version vdrelay x11vnc | gzip -n -9 > "$OUT"

# The license texts of everything linked in, for licenses/ (reviewed by hand before committing).
LIC=/work/licenses-found/$ARCH
mkdir -p "$LIC"
for lib in libXau-1.0.12 libXdamage-1.1.7 libXrandr-1.5.5 libXinerama-1.1.6; do
	cp "$BUILD/$lib/COPYING" "$LIC/$lib-COPYING" 2>/dev/null || true
done
cp "$BUILD/libvncserver-LibVNCServer-0.9.15/COPYING" "$LIC/libvncserver-COPYING" 2>/dev/null || true
cp "$BUILD/x11vnc-0.9.17/COPYING" "$LIC/x11vnc-COPYING" 2>/dev/null || true
for p in libx11 libxcb libxdmcp libxext libxtst libxfixes libxrender libxi zlib libjpeg-turbo musl; do
	apk add --no-cache "$p-doc" >/dev/null 2>&1 || true
done
cp -r /usr/share/licenses/. "$LIC/" 2>/dev/null || true

# Which Alpine packages went into the static link, at which versions, for VERSIONS.txt.
PKGS=/work/packages-$ARCH.txt
{
	echo "# Alpine packages linked into agent-$ARCH.tar.gz ($(cat /etc/alpine-release))"
	apk info -v 2>/dev/null | grep -E '^(musl|libx11|libxcb|libxdmcp|libxext|libxtst|libxfixes|libxrender|libxi|zlib|libjpeg-turbo)-[0-9]' | sort
} > "$PKGS"

if [ -n "${HOST_UID:-}" ]; then
	chown -R "$HOST_UID:${HOST_GID:-$HOST_UID}" "$OUT" "$PKGS" /work/licenses-found
fi
echo "wrote $OUT"
