#!/usr/bin/env bash
# Builds the Remote Control module's host agent: one fully static NativeAOT binary per host
# architecture, in an Alpine .NET SDK container. The results (agent-x86_64.tar.gz,
# agent-aarch64.tar.gz) are committed and embedded in VirtDeck.Core, so this runs when the agent
# changes, not on every build.
#
#   native/agent/build.sh
#
# Needs docker or podman, and the network (the SDK image, the ILCompiler package and the aarch64
# sysroot). One container, one architecture of machine: aarch64 is cross-compiled, so unlike the
# x11vnc agent this replaces, there is no QEMU and no binfmt to register.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
image=mcr.microsoft.com/dotnet/sdk:10.0-alpine

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

# The agent links one file from VirtDeck.Core (Rfb/RfbProtocol.cs), so the repository root is what
# goes into the container, with /work pointing at the agent's own directory inside it.
owner=()
if [ "$engine" = docker ]; then
	owner=(-e "HOST_UID=$(id -u)" -e "HOST_GID=$(id -g)")
fi

"$engine" run --rm --platform linux/amd64 "${owner[@]}" \
	-v "$repo:/repo:Z" -w /repo \
	-e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
	"$image" sh -c 'ln -sfn /repo/native/agent /work && sh /work/build-in-alpine.sh'

(cd "$here" && sha256sum agent-*.tar.gz > SHA256SUMS && cat SHA256SUMS)
