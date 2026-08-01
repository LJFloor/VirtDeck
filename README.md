# VirtDeck

A desktop app to manage libvirt/KVM virtual machines on a remote Linux host over SSH, with a
**native C# SPICE console**: no browser, no WebView2, no WebSocket bridge. Raw TCP to the
SSH-forwarded SPICE port.

Runs on **Windows and Linux** from one Avalonia UI.

## Requirements

### Host (Linux server)
- libvirt + QEMU/KVM (`qemu-kvm libvirt-daemon-system`)
- SSH access with sudo (password-based)
- Optional: `libguestfs-tools` for sparse export (`virt-sparsify`)

Nothing else is installed on the host: media streamed from the client goes over QEMU's built-in
NBD client, which is always compiled in.

### Client (Windows)
- Windows 10/11 x64
- Optional: **UsbDk** kernel driver for USB redirection (installed by the Inno Setup installer)

### Client (Linux)
- x86-64, X11 (Avalonia runs under XWayland on Wayland sessions)
- Optional, from the distro; each degrades gracefully if absent:
  `libusb-1.0-0` + `libusbredirhost1t64` (USB redirection), `udisks2` (handing a mounted USB
  drive over), `libpulse0` (guest audio; PipeWire's Pulse layer counts)
- USB redirection also needs the udev rule in `packaging/`; see that folder's README

The .NET runtime is bundled in both artifacts; there is no separate runtime to install.

## Features

### VM management
- List VMs with state (running / shut off / paused), vCPU count, RAM, and live uptime counter
- Start, stop (graceful), force-stop, reboot
- Create VM wizard: RAM, CPU, disk size, OS type, UEFI/BIOS, host CPU passthrough, network, and
  install media from a local file, an HTTP URL or a host path
- Delete VM with optional disk file removal
- Edit VM config: disks, NICs, boot order, firmware, plus raw XML (read-only while running)
- Network list with activate/deactivate
- Remote file browser for picking paths on the server

### SPICE console
Native C# SPICE client: raw TCP over an SSH port-forward. No plugin required on the guest.

- Full keyboard input (AT set-1 scancodes; positional, so the guest's layout decides)
- Mouse input with cursor tracking, and alpha cursors drawn by the guest
- Keyboard grab on Linux (`XGrabKeyboard`) so Alt+Tab and Super reach the guest; best-effort,
  the console works without it
- "Show host cursor" fallback toggle, saved per VM
- **Guest audio**: the SPICE playback channel, via winmm on Windows and PulseAudio/PipeWire on
  Linux, with a mute toggle saved per VM
- **CD/DVD and Floppy menus**: eject, or insert an image from the server or streamed live from
  this PC
- **USB menu**: redirect a physical USB device from this PC into the guest
- **Display menu**: Low bandwidth (LZ) / Raw compression toggle, Fit Window
- **Power menu**: ACPI shutdown, force off, reboot, reset
- **Keyboard menu**: Ctrl+Alt+Del and other combos the host would otherwise swallow
- Clipboard sharing both ways, "Type clipboard" for guests without an agent, and screenshots

### Removable media: from the server or from this PC
Optical (`.iso`) and floppy (`.vfd`) images can live on the server, or be streamed from the client:
a built-in C# **NBD server** exports the local file over an SSH reverse-forward and QEMU mounts it
with its own NBD client. No host package needed. ISO is exported read-only; **floppy is read-write,
so guest writes land back in the local file**.

### Image compression
Steered at the SPICE protocol level, not via VM config:
- Connects in **LZ** mode by default, works with any unmodified VM (even the `auto_glz` default)
- Display ▸ Raw switches to uncompressed BITMAP for zero-latency on a fast LAN
- QUIC/GLZ are not decoded; the capability advertisement keeps the server from sending them

### Export VM
Right-click → Export VM → saves a `.tar` containing `domain.xml` + all selected disk images.

- **Streaming pipeline**: disks stream from SSH straight into the tar; no temp copy, so exporting
  needs no extra disk space
- **Sparse before export**: optional `virt-sparsify --in-place` pass first, reclaiming unused
  qcow2 clusters (requires `libguestfs-tools` on the host, VM must be off). Progress streams into
  the status label; availability is probed while the Save dialog is still open

### USB redirection
Redirect a physical USB device from the client into the running guest over the SPICE usbredir
channel, on **both Windows and Linux**.

- Guest needs only a USB controller and the device's normal driver, no guest agent
- Windows needs the **UsbDk** driver (in the installer); Linux uses libusb's native backend plus
  the udev rule from `packaging/`
- Picker filters out HID and hubs, and names devices from what the OS already cached (SetupAPI on
  Windows, sysfs on Linux); it never opens a device just to label it
- Mass storage is taken offline before hand-over (dismount on Windows, `udisksctl` unmount on
  Linux) and restored if the redirect doesn't happen
- Auto-provisions a USB controller + 4 redirect channels on the guest if they aren't there
- Reliability: bulk/HID/mass-storage solid; isochronous (webcams, audio) is a known weak spot

### Status bar
Login-time host checks reported in the status bar: CPU virtualisation extensions (svm/vmx in
`/proc/cpuinfo`), `/dev/kvm` present, and libvirtd running.

## Building

```bash
dotnet build VirtDeck.sln
dotnet run --project VirtDeck.Avalonia    # fastest smoke test
```

Release artifacts:

```bash
packaging/build-appimage.sh 1.2.3   # Linux  -> publish/VirtDeck-1.2.3-x86_64.AppImage
publish.bat                         # Windows -> installer\output\VirtDeckSetup-*.exe
```

CI builds both on every push to `main`; pushing a `v*` tag publishes them as a GitHub Release.
See `packaging/README.md` and `installer/README.md`.

## Project layout

```
SpiceClient/          SPICE protocol library, no UI toolkit, produces raw BGRA
  Channels/           Main, Display, Inputs, Cursor, Playback, Usbredir
  Imaging/            Framebuffer, image decoders (BITMAP, LZ_RGB, JPEG)
  Audio/              IAudioSink + winmm / PulseAudio / null backends
  Usb/                UsbredirHostInstance, LibUsbContext, UsbDeviceManager, storage hand-over
  Interop/            P/Invoke for usbredirhost, libusb-1.0, libpulse-simple

VirtDeck.Core/        Cross-platform services and models, SSH.NET + virsh, no UI
  Services/           SshConnectionManager, VirshService, SshPortForwarder, NbdServer,
                      UsbProvisioning, AppSettings
  Models/             VM, disk, network and device models

VirtDeck.Avalonia/    The application (Windows + Linux); assembly name "virtdeck"
  Views/              Login, VM list, console, create wizard, editor, dialogs
  Controls/           SpiceDisplay (framebuffer blitting + input forwarding)
  Input/              Scancode maps, X11 keyboard grab
  Styles/             JetBrains Classic UI trim
  Assets/             App icon

native/win-x64/       Pre-built x64 DLLs for USB redirection (Windows only)
installer/            Inno Setup script (installs app + UsbDk)
packaging/            .desktop file, icons, udev rule, AppImage build script
```
