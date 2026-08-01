# VirtDeck Windows installer

Builds a Windows installer that lays down the app, the bundled native USB DLLs, and the
UsbDk kernel driver (required for client-side USB redirection).

## Prerequisites
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (`iscc` on PATH)
- The native x64 DLLs staged in `..\native\win-x64\` (see that folder's `VERSIONS.txt`)
- `redist\UsbDk_1.0.22_x64.msi` (see `redist\README.txt`)
- (optional) a code-signing cert for the produced setup EXE

## Build (from the repo root)
```cmd
publish.bat
```

That runs the three steps by hand below, in order:

```cmd
:: 1. Publish the app (self-contained, x64) -> publish\win-x64\virtdeck.exe
dotnet publish VirtDeck.Avalonia\VirtDeck.Avalonia.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64

:: 2. Zip the SpiceClient source to publish\SpiceClient-src.zip
::    (LGPL corresponding source, shipped beside the binary; ISCC fails without it)

:: 3. Compile the installer
"C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\VirtDeck.iss
::    -> installer\output\VirtDeckSetup-1.0.0.exe
```

Confirm the native DLLs landed next to `virtdeck.exe`: `libusb-1.0.dll`, `libusbredirhost-1.dll`,
`libusbredirparser-1.dll` and the MinGW runtime DLLs beside them. The csproj copies them from
`native\win-x64\`, and without them USB redirection is dormant (the console says so instead of
failing obscurely). `dumpbin /dependents publish\win-x64\libusbredirhost-1.dll` lists what each one
still needs.

## Notes
- The app is published **self-contained** (`--self-contained true`), so the .NET 10 runtime is
  bundled and the installer has no runtime prerequisite to check.
- The installer requires admin (it installs a kernel driver) and is x64-only.
- On uninstall, UsbDk is intentionally left installed; it is a shared machine-wide driver that
  other SPICE clients (virt-viewer/remote-viewer) may rely on.
- Sign `VirtDeckSetup-*.exe` (and `virtdeck.exe`) to avoid SmartScreen warnings, since the
  installer elevates and installs a driver. Configure Inno's `SignTool` directive in CI.
- The Linux side of the same release is an AppImage; see `..\packaging\`.
