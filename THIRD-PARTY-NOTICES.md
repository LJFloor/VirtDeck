# Third-Party Notices

VirtDeck redistributes the third-party components below. The native libraries' full license
texts live in `licenses/` (installed to `<install dir>\licenses\` by the Windows setup).

## Native USB-redirection libraries (Windows only, DLLs shipped next to virtdeck.exe)

| File(s) | Project | Version | License | Text |
|---|---|---|---|---|
| `libusb-1.0.dll` | libusb | 1.0.28 | LGPL-2.1-or-later | `licenses/LGPL-2.1.txt` |
| `libusbredirhost-1.dll`, `libusbredirparser-1.dll` | usbredir | 0.15.0 | LGPL-2.1-or-later | `licenses/LGPL-2.1.txt` |
| `libgcc_s_seh-1.dll`, `libssp-0.dll` | GCC runtime (libgcc / libssp), mingw-w64 build | n/a | GPL-3.0 WITH GCC-Runtime-Library-Exception-3.1 | `licenses/GPL-3.0.txt` + `licenses/GCC-Runtime-Library-Exception-3.1.txt` |
| `libwinpthread-1.dll` | mingw-w64 winpthreads | n/a | MIT (permissive) | `licenses/winpthreads-COPYING.txt` |

On Linux these are **not** redistributed: libusb and usbredirhost come from the distro's own
packages, so nothing in this list applies to the AppImage.

Upstream sources:
- libusb: https://github.com/libusb/libusb
- usbredir: https://gitlab.freedesktop.org/spice/usbredir
- mingw-w64 (libgcc/libssp/winpthreads runtime): https://www.mingw-w64.org/

Notes:
- **LGPL (libusb, usbredir):** shipped as separate DLLs loaded by name, so a recipient can
  drop in their own build, satisfying the LGPL relink right. Corresponding source is the
  upstream releases above (matching the versions listed).
- **GCC runtime (libgcc, libssp):** distributed under the GCC Runtime Library Exception,
  which explicitly permits use in closed-source programs.

## UsbDk kernel driver (Windows only, installed by the setup from its MSI)

- Project: UsbDk (https://github.com/daynix/UsbDk)
- Version: 1.00-22 (`UsbDk_1.0.22_x64.msi`)
- License: Apache-2.0 (`licenses/UsbDk-LICENSE-Apache-2.0.txt`)

## Bundled .NET components

Both artifacts are published self-contained, so these ship inside them:

| Component | Version | License | Upstream |
|---|---|---|---|
| .NET runtime and libraries | 10 | MIT | https://github.com/dotnet/runtime |
| Avalonia (+ Desktop, Themes.Fluent, Fonts.Inter) | 12.0.5 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Inter typeface (in Avalonia.Fonts.Inter) | n/a | SIL OFL 1.1 | https://github.com/rsms/inter |
| SkiaSharp (+ NativeAssets.Linux) | 3.119.4 | MIT (wrapper), BSD-3-Clause (Skia itself) | https://github.com/mono/SkiaSharp |
| Concentus (Opus decoder) | 2.2.2 | BSD-3-Clause (the Opus license) | https://github.com/lostromb/concentus |
| SSH.NET | 2024.2.0 | MIT | https://github.com/sshnet/SSH.NET |

## SpiceClient (SPICE protocol client, component of this product)

The `SpiceClient` library's protocol code was ported from **spice-html5** (LGPL), making it
a derivative work under the **LGPL**. Its corresponding source ships alongside the app
(`SpiceClient-src.zip` next to `virtdeck.exe` on Windows, and
`usr/share/virtdeck/SpiceClient-src.tar.gz` inside the AppImage), and it is a separate DLL
(relinkable).
- spice-html5: https://gitlab.freedesktop.org/spice/spice-html5

The remainder of VirtDeck (the `virtdeck` application, `VirtDeck.Core`) is proprietary; see
`LICENSE.txt`.
