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
| Avalonia (+ Desktop, Themes.Fluent, Fonts.Inter) | 12.1.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Newtonsoft.Json (a dependency of UnattendGenerator, below) | 13.0.4 | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| Inter typeface (in Avalonia.Fonts.Inter) | n/a | SIL OFL 1.1 | https://github.com/rsms/inter |
| SkiaSharp (+ NativeAssets.Linux) | 3.119.4 | MIT (wrapper), BSD-3-Clause (Skia itself) | https://github.com/mono/SkiaSharp |
| Svg.Skia (+ Svg.Model, Svg.Custom, Svg.SceneGraph, Svg.Animation, ShimSkiaSharp) | 5.1.1 | MIT | https://github.com/wieslawsoltes/Svg.Skia |
| ExCSS (a dependency of Svg.Skia, above) | 4.3.1 | MIT | https://github.com/TylerBrinks/ExCSS |
| Concentus (Opus decoder) | 2.2.2 | BSD-3-Clause (the Opus license) | https://github.com/lostromb/concentus |
| SSH.NET | 2026.0.0 | MIT | https://github.com/sshnet/SSH.NET |

## UnattendGenerator (vendored source, compiled into the app)

VirtDeck's "Customize Windows setup" window writes its `autounattend.xml` with Christoph
Schneegans' generator, the library behind <https://schneegans.de/windows/unattend-generator/>. It is
not on NuGet, so its source is vendored at `third-party/unattend-generator/` and built as part of the
solution; `third-party/unattend-generator/VENDORED.md` records the exact upstream commit and how to
update it.

- Project: unattend-generator (https://github.com/cschneegans/unattend-generator)
- Version: commit `427480c7be47e8bd1301b89747baab38271b3da3` (2026-08-07)
- License: MIT, reproduced below and at `third-party/unattend-generator/LICENSE.txt`

```
MIT License

Copyright (c) 2024-2025 Christoph Schneegans

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SpiceClient (SPICE protocol client, component of this product)

The `SpiceClient` library's protocol code was ported from **spice-html5** (LGPL), making it
a derivative work under the **LGPL**. Its corresponding source ships alongside the app
(`SpiceClient-src.zip` next to `virtdeck.exe` on Windows, and
`usr/share/virtdeck/SpiceClient-src.tar.gz` inside the AppImage), and it is a separate DLL
(relinkable).
- spice-html5: https://gitlab.freedesktop.org/spice/spice-html5

The remainder of VirtDeck (the `virtdeck` application, `VirtDeck.Core`) is proprietary; see
`LICENSE.txt`.
