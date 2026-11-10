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

NativeAOT release artifacts (one native executable, so there is no .NET runtime to bundle; libusb/usbredir/libpulse deliberately are not bundled either, see `packaging/README.md`):

```bash
packaging/build-appimage.sh 1.2.3   # Linux   -> publish/VirtDeck-1.2.3-x86_64.AppImage
packaging/build-packages.sh 1.2.3   # Linux   -> publish/virtdeck_1.2.3-1_amd64.deb, virtdeck-1.2.3-1.x86_64.rpm
publish.bat                         # Windows -> installer\output\VirtDeckSetup-*.exe
```

`.github/workflows/publish.yml` builds all of them on every push to `main` and uploads nothing: that is the no-regression gate, and every format is built because ISCC, appimagetool and nfpm are part of what can regress. Pushing a `v*` tag runs the same builds at the tag's version and attaches the installer, the AppImage, the .deb and the .rpm to a GitHub release:

```bash
git tag v1.2.3 && git push origin v1.2.3
```

## NativeAOT

Every release artifact above is published with `-p:PublishAot=true`; `dotnet build` and
`dotnet run` are unaffected and stay JIT. Measured on Linux at the host
manager with software rendering: **86 MB private and about 75 ms to the first window**, against
110 MB and about 600 ms for the JIT build (187 MB with GLX, see "VirtDeck.Avalonia (cross-platform UI)").

- **The analyzers run on every build** (`IsAotCompatible` on the two libraries, the trim and AOT
  analyzers on the app), so whatever would break the AOT publish is a warning in the ordinary
  build. The publish itself still warns about the vendored unattend-generator's ten
  `JsonConvert.DeserializeObject` calls and about Newtonsoft.Json and Svg.Model as assemblies:
  the first two are handled below, and Svg.Model was measured instead (forty theme icons render
  pixel-identical to the JIT build).
- **Both JSON files are source-generated** (`AppSettingsJson`, `UnattendPresetJson`), because AOT
  turns reflection-based System.Text.Json off and the settings would load empty on every launch.
  The output is byte-identical to the reflection serialiser's, measured on a real settings file
  and on a preset with all 175 of its values changed.
- **The unattend-generator is reached by reflection the trimmer cannot follow.** It loads its
  tables through Newtonsoft.Json, which finds constructors and builds immutable collections by
  name, so the csproj keeps `UnattendGenerator`, `Newtonsoft.Json` and
  `System.Collections.Immutable` whole. That is not enough for a collection of a **value type**,
  which AOT cannot share code for: `UnattendCatalog` compiles `ImmutableSortedSet<Pass>` itself,
  the one such instantiation the tables need. A new one in an upstream update fails only in the AOT
  build and only at run time, as "is missing native code" naming the method, and the fix is one more
  line beside that one. The answer file is byte-identical to the JIT build's.
- `Avalonia.X11` is kept whole for `Input/X11KeyboardGrab`, which finds Avalonia's X display by
  walking private fields. `BuiltInComInteropSupport` stays on for the JIT build only: AOT has no
  built-in COM, and nothing here uses it.
- **The linker binds glibc symbols to the build machine's own versions.** Linked on Ubuntu 26.04
  the binary needs glibc 2.38 (for `fmod`), where the JIT build needs 2.27, and anything linked
  after the libpthread merge needs 2.34 for `dlopen` and the pthread calls. A Linux AOT release
  therefore has to be linked on, or against a sysroot of, the oldest glibc it supports, and **CI
  links it on Ubuntu 22.04**, the oldest distro `packaging/README.md` lists as tested. The binary it
  produces needs glibc 2.34 (measured on v0.4.0: `dlopen` and the pthread calls), so RHEL 9 still
  runs it and Ubuntu 20.04, which the JIT build ran on, does not. When GitHub retires that runner
  image, the way to keep the floor is a build against an old sysroot, such as the cross-build
  containers Microsoft builds .NET itself in. ILC links
  with clang; where there is none, the csproj falls back to gcc, which links this app as well.
- Windows needs the Visual Studio C++ build tools to link, and neither OS can link for the other.
- **Not yet verified:** any of it on Windows, and the keyboard grab in a console window.

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

