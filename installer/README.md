# SpiceVmManager installer

Builds a Windows installer that lays down the app, the bundled native USB DLLs, and the
UsbDk kernel driver (required for client-side USB redirection).

## Prerequisites
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (`iscc` on PATH)
- The native x64 DLLs staged in `..\native\win-x64\` (see that folder's `VERSIONS.txt`)
- `redist\UsbDk_1.0.22_x64.msi` (see `redist\README.txt`)
- (optional) a code-signing cert for the produced setup EXE

## Build (from the repo root)
```cmd
# 1. Publish the app (framework-dependent, x64)
dotnet publish VmManager.App\VmManager.App.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64

# 2. Confirm the native DLLs landed next to VmManager.exe
#    libusb-1.0.dll, usbredirhost.dll, usbredirparser.dll (+ any MinGW runtime DLLs)
#    e.g.  dumpbin /dependents publish\win-x64\usbredirhost.dll

# 3. Compile the installer
"C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\SpiceVmManager.iss
#    -> installer\output\SpiceVmManagerSetup-1.0.0.exe
```

## Notes
- The app is framework-dependent: the installer checks for the **.NET 8 Desktop Runtime (x64)**
  and points the user to the download if it is missing. To remove that prerequisite, publish
  with `--self-contained true` instead (larger output).
- The installer requires admin (it installs a kernel driver) and is x64-only.
- On uninstall, UsbDk is intentionally left installed — it is a shared machine-wide driver that
  other SPICE clients (virt-viewer/remote-viewer) may rely on.
- Sign `SpiceVmManagerSetup-*.exe` (and `VmManager.exe`) to avoid SmartScreen warnings, since the
  installer elevates and installs a driver. Configure Inno's `SignTool` directive in CI.
