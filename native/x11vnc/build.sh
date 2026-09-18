#!/usr/bin/env bash
# Builds the Remote Control module's host agent: a fully static x11vnc plus vdrelay, one tarball
# per architecture, in Alpine containers. The results (agent-x86_64.tar.gz, agent-aarch64.tar.gz)
# are committed and embedded in VirtDeck.Core, so this runs when a version changes, not per build.
#
#   native/x11vnc/build.sh            # both architectures
#   ARCHS=amd64 native/x11vnc/build.sh
#
# Needs docker or podman. The arm64 build on an x86_64 machine runs under QEMU user emulation,
# which on Debian and Ubuntu is `sudo apt install qemu-user-binfmt` (qemu-user-static on releases
# before QEMU 10 made that a virtual package).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
image=alpine:3.24.2
archs=${ARCHS:-"amd64 arm64"}

engine=${CONTAINER_ENGINE:-}
if [ -z "$engine" ]; then
	for e in docker podman; do
		if command -v "$e" >/dev/null 2>&1; then engine=$e; break; fi
	done
fi
if [ -z "$engine" ]; then
	echo "Needs docker or podman." >&2
	exit 1
fi
if ! "$engine" info >/dev/null 2>&1; then
	echo "$engine is installed but $(id -un) cannot use it." >&2
	[ "$engine" = docker ] && echo "Run: sudo usermod -aG docker $(id -un), then log in again (or: newgrp docker)." >&2
	exit 1
fi

host=$(uname -m)
for a in $archs; do
	case "$a:$host" in
		arm64:x86_64)
			# Registered, and with the F flag: the kernel then keeps the emulator open itself, which
			# is what lets it run inside a container whose filesystem does not have it.
			if ! grep -q '^flags:.*F' /proc/sys/fs/binfmt_misc/qemu-aarch64 2>/dev/null; then
				echo "Building for arm64 on x86_64 needs QEMU user emulation registered with the F flag." >&2
				echo "Run: sudo apt install qemu-user-binfmt   (qemu-user-static on older releases)" >&2
				echo " or: docker run --privileged --rm tonistiigi/binfmt --install arm64   (until reboot)" >&2
				echo "Or build x86_64 alone with ARCHS=amd64." >&2
				exit 1
			fi
			;;
	esac
done

(cd "$here/src" && sha256sum -c --quiet ../SOURCES.sha256)

# Docker runs the container as root and would leave root-owned files behind; rootless podman
# already maps the container's root to this user, where a chown would do the opposite.
owner=()
if [ "$engine" = docker ]; then
	owner=(-e "HOST_UID=$(id -u)" -e "HOST_GID=$(id -g)")
fi

rm -rf "$here/licenses-found"
for a in $archs; do
	echo "==== building for linux/$a"
	"$engine" run --rm --platform "linux/$a" "${owner[@]}" \
		-v "$here:/work:Z" "$image" sh /work/build-in-alpine.sh
done

(cd "$here" && sha256sum agent-*.tar.gz > SHA256SUMS && cat SHA256SUMS)
echo "Review licenses-found/ against licenses/, then delete it."
