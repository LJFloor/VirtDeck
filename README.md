# SpiceVmManager

A Windows desktop app to manage libvirt/KVM virtual machines on a remote Linux host over SSH, with a **native C# SPICE console** — no browser, no WebView2, no WebSocket bridge.

## Requirements

### Host (Linux server)
- libvirt + QEMU/KVM (`qemu-kvm libvirt-daemon-system`)
- SSH access with sudo (password-based)
- Optional: `libguestfs-tools` for sparse export (`virt-sparsify`)

### Client (Windows)
- Windows 10/11 x64
- .NET 8 runtime (or use the self-contained publish)
- Optional: **UsbDk** kernel driver for USB redirection (installed by the provided Inno Setup installer)

## Features

### VM management
- List VMs with state (running / shut off / paused), vCPU count, RAM, and live uptime counter
- Start, stop (graceful), force-stop, reboot
- Create VM wizard — RAM, CPU, disk size, OS type, UEFI/BIOS, host CPU passthrough, network, ISO (local file, HTTP URL, or host path)
- Delete VM with optional disk file removal
- Edit VM XML config (read-only while running)
- Network list with activate/deactivate

### SPICE console
Native C# SPICE client — raw TCP over SSH port-forward. No plugin required on the guest.

- Full keyboard input (low-level WH_KEYBOARD_LL hook, AT set-1 scancodes)
- Mouse input with cursor tracking
- Alpha cursor support (guest-drawn cursors rendered natively via `CreateIconIndirect`)
- "Show host cursor" fallback toggle (saved per-VM in the registry)
- **CD/DVD toolbar menu** — eject, insert ISO (local / HTTP stream / host path), change media live
- **Display toolbar** — Low bandwidth (LZ) / Raw compression toggle, Fit Window
- **Power toolbar** — ACPI shutdown, force off, reboot, reset
- **Keyboard toolbar** — Ctrl+Alt+Del, other special combos
- **USB toolbar** — redirect a physical USB device from the Windows client into the guest (see below)

### Image compression
Steered at the SPICE protocol level, not via VM config:
- Connects in **LZ** mode by default — works with any unmodified VM (even `auto_glz` default)
- Display ▸ Raw switches to uncompressed BITMAP for zero-latency on a fast LAN
- Display ▸ Low bandwidth (LZ) to re-enable LZ on the fly
- QUIC/GLZ not decoded (server never sends them due to the capability advertisement)

### Export VM
Right-click → Export VM → saves a `.tar` containing `domain.xml` + all selected disk images.

- **Streaming pipeline**: disks stream directly from SSH into the tar file — no temp copy, no double the disk space needed
- **Sparse before export**: optional `virt-sparsify --in-place` pass before downloading — reclaims unused qcow2 clusters, shrinking the on-disk file (requires `libguestfs-tools` on host, VM must be off)
  - Checkbox disabled with tooltip + hint when the VM is running or virt-sparsify is not installed
  - virt-sparsify availability check runs in parallel while the Save dialog is open — no added wait
  - Streaming progress output shown line-by-line in the status label while sparsifying

### USB redirection
Redirect a physical USB device from the Windows client into the running guest via the SPICE usbredir channel.

- Guest needs only a USB controller and the device's normal driver — no guest agent required
- Requires UsbDk kernel driver on the Windows client (installed by the Inno Setup installer in `installer/`)
- Picker filters out HID/hubs; shows friendly device names (Windows SetupAPI)
- Mass-storage devices: automatically dismounts Windows volumes before capture (prevents surprise-removal corruption) and re-enumerates on release
- Auto-provisions USB controller + 4 redirect channels on the guest VM if not already present
- Reliability: bulk/HID/mass-storage solid; isochronous (webcams, audio) is a known weak spot

### Status bar
Login-time host checks reported in the status bar:
- CPU virtualisation extensions (svm/vmx in `/proc/cpuinfo`)
- BIOS virt enabled (`/dev/kvm` present)
- libvirtd running

## Building

```bash
dotnet build SpiceVmManager.sln
```

Self-contained publish (produces a single-folder deployment):

```bat
publish.bat
```

The publish output lands in `publish/`. The csproj copies the `native/win-x64/` DLLs (usbredirhost, usbredirparser, libusb-1.0) next to the exe automatically.

## Project layout

```
SpiceClient/          SPICE protocol library (no WinForms dependency)
  Channels/           MainChannel, DisplayChannel, InputsChannel, CursorChannel, UsbredirChannel
  Imaging/            Framebuffer, image decoders (BITMAP, LZ_RGB, JPEG)
  Usb/                UsbredirHostInstance, LibUsbContext, UsbDeviceManager
  Interop/            P/Invoke bindings for usbredirhost + libusb-1.0

VmManager.App/        WinForms desktop application
  Forms/              All UI forms and dialogs
  Services/           SshConnectionManager, VirshService, SshPortForwarder, UsbProvisioning
  Controls/           SpiceDisplayControl (framebuffer painting + input forwarding)
  Input/              WinFormsKeyMap (VK → AT scancode)
  Interop/            CursorInterop, UsbNames (SetupAPI), UsbStorageDismount

native/win-x64/       Pre-built x64 DLLs for USB redirection
installer/            Inno Setup script (installs app + UsbDk)
```
