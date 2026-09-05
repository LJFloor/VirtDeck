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

`.github/workflows/publish.yml` builds both on every push to `main`, and that is the whole of CI: it **uploads nothing**, so it is the no-regression gate and not a source of downloads. The repo being private is why: build artifacts count against the account's Actions storage allowance, one push to `main` stored about 107 MB for the pair, and once the allowance is full *every* upload in the repo fails, so a green build reports as a failed run. Both halves are still built, because ISCC and appimagetool are part of what can regress. There is no tag-triggered release workflow any more; a release is built locally with the two commands above.

`packaging/icons/make-icons.py` is the single source for the app icon: it rasterises the hicolor PNGs, the scalable SVG and the Windows `.ico` from one set of numbers, so no build machine needs an SVG renderer.

