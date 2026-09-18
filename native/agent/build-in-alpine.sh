#!/bin/sh
# Runs inside an x86_64 Alpine .NET SDK container (see build.sh) and writes both
# /work/agent-x86_64.tar.gz and /work/agent-aarch64.tar.gz: the remote control agent, published
# with NativeAOT as one fully static musl binary per architecture.
#
# Both are built here, on one x86_64 machine, with no emulation: ILC cross-compiles aarch64 by
# itself and lld links it, so the only thing missing is an aarch64 musl sysroot, which apk installs
# without running a single aarch64 instruction.
set -eu

BUILD=/tmp/build
SYSROOT=/tmp/sysroot-aarch64
PROJECT=/work/VirtDeck.Agent/VirtDeck.Agent.csproj

log() { printf '\n==== %s\n' "$1"; }

log "packages"
# clang and lld do the native link; llvm carries llvm-objcopy, which strips either architecture.
apk add --no-cache clang lld llvm build-base file tar gzip

log "aarch64 sysroot"
# apk-tools 3 ignores a repositories file copied into the new root, so the repositories have to be
# named with -X. Without gcc there are no crtbeginS.o and crtendS.o for clang to link against.
ALPINE=$(cut -d. -f1,2 /etc/alpine-release)
apk --root "$SYSROOT" --arch aarch64 --initdb --allow-untrusted \
	-X "https://dl-cdn.alpinelinux.org/alpine/v$ALPINE/main" \
	-X "https://dl-cdn.alpinelinux.org/alpine/v$ALPINE/community" \
	add musl-dev gcc zlib-static

publish() {
	rid=$1
	arch=$2
	shift 2
	log "$arch"
	rm -rf "/work/VirtDeck.Agent/bin/Release/net10.0/$rid"
	dotnet publish "$PROJECT" -c Release -r "$rid" \
		-p:PublishAot=true -p:StaticExecutable=true -p:StripSymbols=true \
		-p:DebugType=none -p:GenerateDocumentationFile=false \
		"$@" >/dev/null

	binary="/work/VirtDeck.Agent/bin/Release/net10.0/$rid/publish/virtdeck-agent"
	desc=$(file -b "$binary")
	echo "$arch: $desc"
	case $desc in
		*"statically linked"* | *"static-pie linked"*) ;;
		*) echo "error: the $arch agent is not static" >&2; exit 1 ;;
	esac
	case $desc in
		*x86-64* | *aarch64*) ;;
		*) echo "error: the $arch agent is not for $arch" >&2; exit 1 ;;
	esac

	rm -rf "$BUILD/pkg"
	mkdir -p "$BUILD/pkg"
	install -m 755 "$binary" "$BUILD/pkg/virtdeck-agent"
	# What the Remote Control module names in the status bar.
	grep -o 'Version = "[^"]*"' /work/VirtDeck.Agent/Program.cs | head -n 1 |
		sed 's/Version = "/virtdeck-agent /; s/"//' > "$BUILD/pkg/version"
	chmod 644 "$BUILD/pkg/version"
	cat "$BUILD/pkg/version"

	# Normalised so the same inputs give the same bytes, and so the same hash on the host.
	tar -C "$BUILD/pkg" --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner \
		-cf - version virtdeck-agent | gzip -n -9 > "/work/agent-$arch.tar.gz"
	echo "wrote /work/agent-$arch.tar.gz ($(wc -c < "/work/agent-$arch.tar.gz") bytes)"
}

publish linux-musl-x64 x86_64
publish linux-musl-arm64 aarch64 \
	-p:SysRoot="$SYSROOT" \
	-p:CppCompilerAndLinker=clang \
	-p:LinkerFlavor=lld \
	-p:ObjCopyName=llvm-objcopy

# Docker runs the container as root; rootless podman already maps root to the invoking user.
if [ -n "${HOST_UID:-}" ]; then
	chown -R "$HOST_UID:${HOST_GID:-$HOST_UID}" /work/agent-x86_64.tar.gz /work/agent-aarch64.tar.gz
	chown -R "$HOST_UID:${HOST_GID:-$HOST_UID}" /work/VirtDeck.Agent/bin /work/VirtDeck.Agent/obj 2>/dev/null || true
fi
