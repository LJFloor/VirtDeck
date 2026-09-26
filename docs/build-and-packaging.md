# Build and packaging

## Build

```bash
dotnet build VirtDeck.sln
```

Four projects, all `net10.0`, all buildable on either OS:
- **SpiceClient**: cross-platform SPICE protocol client library. No UI toolkit: it produces raw BGRA buffers. SkiaSharp for JPEG decode, Concentus for Opus.
- **VirtDeck.Core**: cross-platform services and models (`Services/`, `Models/`, `Unattend/`, `Terminal/`, `Diagnostics/`, `Secrets/`, `Imaging/PpmImage`). SSH.NET + `virsh` over SSH; no UI dependency. Namespaces are `VirtDeck.*`.
- **VirtDeck.Avalonia**: the UI (Windows + Linux). Assembly name `virtdeck`.
- **third-party/unattend-generator**: vendored MIT source, not ours. Writes `autounattend.xml`; referenced by `VirtDeck.Core`. Do not edit it; see its `VENDORED.md`.

A clean solution build is the no-regression gate, but it only proves compilation: Windows runtime behaviour (winmm audio, UsbDk capture) still needs a real Windows run, and the reverse for PulseAudio/udisks.

No tests yet. `dotnet run --project VirtDeck.Avalonia` is the fastest smoke test.

## Packaging

Two self-contained release artifacts (the .NET runtime is bundled; libusb/usbredir/libpulse deliberately are not, see `packaging/README.md`):

```bash
packaging/build-appimage.sh 1.2.3   # Linux   -> publish/VirtDeck-1.2.3-x86_64.AppImage
publish.bat                         # Windows -> installer\output\VirtDeckSetup-*.exe
```

`.github/workflows/publish.yml` builds both on every push to `main` and uploads nothing: that is the no-regression gate, and both halves are built because ISCC and appimagetool are part of what can regress. Pushing a `v*` tag runs the same builds at the tag's version and attaches the installer and the AppImage to a GitHub release:

```bash
git tag v1.2.3 && git push origin v1.2.3
```

## The Remote Control host agent

`native/agent/build.sh` builds `virtdeck-agent`, the one static binary the Remote Control module
uploads to hosts (see "Remote control"), one tarball per host architecture, in an Alpine .NET SDK
container:

```bash
native/agent/build.sh               # docker or podman, and the network
```

**One container builds both architectures.** The agent is NativeAOT C#, and ILC cross-compiles
aarch64 on an x86_64 machine by itself, so the only thing the container has to fetch is an aarch64
musl sysroot, which `apk --arch aarch64 --root` installs without running a single aarch64
instruction. There is no QEMU and no binfmt to register, and the whole build takes seconds rather
than the minutes the emulated x11vnc build took. Two things in `build-in-alpine.sh` are not obvious
and are commented there: apk-tools 3 ignores a `repositories` file copied into the new root, so the
repositories are named with `-X`; and `LinkerFlavor=lld` is required, because the `ld.bfd` that
image ships only knows x86.

It runs when the agent changes, never as part of a build: the results (`agent-x86_64.tar.gz`,
`agent-aarch64.tar.gz`, with `SHA256SUMS`) are committed, like the Windows DLLs in
`native/win-x64`, and `VirtDeck.Core.csproj` embeds them on every platform. **A missing tarball is
not a build error**: the module then says it has no agent for that host's architecture.

`native/agent/VirtDeck.Agent` **is in the solution**, so an ordinary `dotnet build VirtDeck.sln`
compiles it as IL and the no-regression gate covers it; only `build.sh` runs the AOT publish, so
nobody needs the AOT toolchain to build the app. It is never referenced by the app and never
publishes with it: it ships as bytes. The agent is ours, so unlike the x11vnc it replaced there is
no corresponding-source archive in either release artifact.

`packaging/icons/make-icons.py` is the single source for the app icon: it rasterises the hicolor PNGs, the scalable SVG and the Windows `.ico` from one set of numbers, so no build machine needs an SVG renderer.

