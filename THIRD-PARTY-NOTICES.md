# Third-Party Notices

SpiceVmManager redistributes the third-party components below. Their full license texts
live in `licenses/` (and are installed to `<install dir>\licenses\` by the setup).

## Native USB-redirection libraries (DLLs shipped next to VmManager.exe)

| File(s) | Project | Version | License | Text |
|---|---|---|---|---|
| `libusb-1.0.dll` | libusb | 1.0.28 | LGPL-2.1-or-later | `licenses/LGPL-2.1.txt` |
| `libusbredirhost-1.dll`, `libusbredirparser-1.dll` | usbredir | 0.15.0 | LGPL-2.1-or-later | `licenses/LGPL-2.1.txt` |
| `libgcc_s_seh-1.dll`, `libssp-0.dll` | GCC runtime (libgcc / libssp), mingw-w64 build | — | GPL-3.0 WITH GCC-Runtime-Library-Exception-3.1 | `licenses/GPL-3.0.txt` + `licenses/GCC-Runtime-Library-Exception-3.1.txt` |
| `libwinpthread-1.dll` | mingw-w64 winpthreads | — | MIT (permissive) | `licenses/winpthreads-COPYING.txt` |

Upstream sources:
- libusb — https://github.com/libusb/libusb
- usbredir — https://gitlab.freedesktop.org/spice/usbredir
- mingw-w64 (libgcc/libssp/winpthreads runtime) — https://www.mingw-w64.org/

Notes:
- **LGPL (libusb, usbredir):** shipped as separate DLLs loaded by name, so a recipient can
  drop in their own build — satisfying the LGPL relink right. Corresponding source is the
  upstream releases above (matching the versions listed).
- **GCC runtime (libgcc, libssp):** distributed under the GCC Runtime Library Exception,
  which explicitly permits use in closed-source programs.

## UsbDk kernel driver (installed by the setup from its MSI)

- Project: UsbDk — https://github.com/daynix/UsbDk
- Version: 1.00-22 (`UsbDk_1.0.22_x64.msi`)
- License: Apache-2.0 — `licenses/UsbDk-LICENSE-Apache-2.0.txt`

## SpiceClient (SPICE protocol client — component of this product)

The `SpiceClient` library's protocol code was ported from **spice-html5** (LGPL), making it
a derivative work under the **LGPL**. Its corresponding source ships alongside the app as
`SpiceClient-src.zip`, and it is a separate DLL (relinkable).
- spice-html5 — https://gitlab.freedesktop.org/spice/spice-html5

The remainder of SpiceVmManager (the VmManager application) is proprietary.
