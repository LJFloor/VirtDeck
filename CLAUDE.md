# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Build

```bash
dotnet build VirtDeck.sln
```

Two-project solution (.NET 8, `net8.0-windows`):
- **SpiceClient** — a native, dependency-light SPICE protocol client library (no WinForms; uses `System.Drawing` for `Bitmap`/JPEG only).
- **VirtDeck** — the WinForms desktop app (`WinExe`, assembly name `virtdeck`, x64). References SpiceClient + SSH.NET.

No tests yet.

## What this is

A WinForms app to manage libvirt/KVM VMs on a remote Linux host over SSH, with a **native C# SPICE console** (no WebView2, no spice-html5, no WebSocket bridge — raw TCP to the SSH-forwarded SPICE port).

It is a fresh rewrite of the older `..\VmManager` app, which rendered SPICE via spice-html5 in WebView2 and broke with "Protocol Error" (the WebSocket↔TCP bridge mangled SPICE's binary framing). The native client deletes that failure mode. The SPICE protocol was ported field-for-field from the bundled spice-html5 source at `..\VmManager\VmManager\WebContent\src\*.js` — that JS is the authoritative wire-format reference.

**Flow:** `LoginForm` → SSH connect → `VmListForm` → double-click/right-click VM → `VmConsoleForm` (native SPICE).

## Architecture

### SpiceClient (protocol engine)
- `Protocol/` — `SpiceConstants` (enums.js), `SpiceReader`/`SpiceWriter` (little-endian span helpers; `U32BE` for LZ/JPEG-alpha sub-headers).
- `Crypto/SpiceTicket` — RSA ticket auth: `RSA.ImportSubjectPublicKeyInfo` + `Encrypt(OaepSHA1)` of `password + "\0"` (server pubkey is a 162-byte DER SubjectPublicKeyInfo). Replaces spice-html5's hand-rolled OAEP.
- `Transport/ChannelSocket` — one blocking `TcpClient` per channel; `ReadExact`; locked `Write`.
- `Channels/SpiceChannel` (base) — per-channel socket + dedicated read thread; link handshake; common-message loop (SET_ACK→ACK_SYNC + ACK flow control, PING→PONG, NOTIFY). Subclasses: `MainChannel`, `DisplayChannel`, `InputsChannel`, `CursorChannel`.
- `Imaging/` — `SpiceFramebuffer` (a `Format32bppArgb` Bitmap over a **pinned** BGRA byte buffer; all access under `SyncRoot`), `ImageDecoders` (BITMAP, JPEG, LZ_RGB; **QUIC/GLZ deferred → return null**), `DecodedImage`.
- `SpiceSession` — facade: orchestrates channel bring-up, owns the framebuffer, raises events (`ResolutionChanged`, `FrameDirty`, `CursorSet/Hidden/Reset`, `Disconnected`, `StatusMessage`). Events fire on channel threads — subscribers must marshal.
- `CursorShape` — decoded ALPHA cursor (BGRA + hotspot).

### VirtDeck (WinForms)
- `Services/` — `SshConnectionManager`, `SshPortForwarder`, `VirshService` (sudo via stdin + marker). Image compression is steered at the protocol level (see below), not via `virt-xml`.
- `Controls/SpiceDisplayControl` — custom `Control`: paints the framebuffer (1:1 `DrawImage`, double-buffered, ~60 Hz dirty-rect repaint timer), forwards mouse, and **owns all cursor assignment** (the exactly-one-cursor state machine).
- `Input/WinFormsKeyMap` — VK → AT set-1 scancode. **Extended keys are `0xE0 | (atCode << 8)`** (e.g. PageUp = `0x49E0`), matching spice-html5 utils.js — NOT `0xE0XX`. Key-up high bit applied in `InputsChannel.SendKey`.
- `Interop/CursorInterop` — builds a native `Cursor` (alpha + hotspot) via `CreateIconIndirect`; caller must `DestroyIcon` the HICON on replace/dispose.
- `Forms/` — `LoginForm`, `VmListForm`, `VmConsoleForm` (low-level WH_KEYBOARD_LL hook → `InputsChannel.SendKey`; Power/Keyboard/Mouse/Display dropdowns; Fit Window).

### Host capability checks

`VirshService` probes the host once at login (`VmListForm.RefreshHostCapabilities`) and caches the results as properties:

| Property | Check | Used by |
|---|---|---|
| `VirtSparseAvailable` | `which virt-sparsify` | Export dialog — disables sparse checkbox |
| `CheckHostCapabilities()` | `/proc/cpuinfo` svm/vmx, `/dev/kvm`, `systemctl is-active libvirtd` | Status-bar indicators in `VmListForm` |

All checks default to `true`/available on SSH error to avoid false negatives, **except `VirtSparseAvailable`** which defaults to `false` (safe — prevents a silent no-op export).

### Export VM (`Forms/ExportVmDialog.cs`)

Right-click → Export VM → saves a `.tar` containing `domain.xml` + `disks/` subdirectory.

**Streaming pipeline** (no temp file for known-size disks):
```
SSH (sudo dd) → Pipe.Writer → KnownLengthStream → TarWriter → FileStream
```
`TarWriter` (PAX format) requires the entry size in the header before data flows. `KnownLengthStream` wraps the pipe reader and reports a pre-measured `Length` while reading sequentially — satisfying TarWriter without buffering the whole file locally.

**Sparse before export** — optional checkbox (requires `libguestfs-tools` on host, VM must be off):
- Runs `virt-sparsify --in-place` on each selected disk before downloading; reclaims unused qcow2 clusters, shrinking the on-disk file size
- Progress output from virt-sparsify streams live to the status label via `SshConnectionManager.RunSudoCommandStreaming`
- Size is measured **after** sparsify so the tar entry header reflects the shrunken size
- Checkbox disabled (with tooltip + hint label) when the VM is running or when virt-sparsify is not installed

**SaveFileDialog** is shown first (in `Load`), while VM config fetch and virt-sparsify availability check run in the background — both SSH calls complete while the user is browsing for a save location.

## Cursor rule (the key requirement)

Exactly ONE cursor must be visible over the display — never zero, never two. `SpiceDisplayControl.ApplyCursor()` is the single chokepoint and just sets `Control.Cursor` (the OS never stacks cursors):
- ShowHostCursor policy → `Cursors.Default`;
- before any cursor message → `Cursors.Default` (no missing cursor at startup);
- guest hid the cursor → a transparent blank cursor;
- otherwise → the built SPICE cursor (or default if RESET).

The "Show host cursor" toggle (per-VM, in `HKCU\SOFTWARE\VirtDeck`) is the user's accepted fallback.

## Image compression / QUIC

QUIC and GLZ are not decoded. The client decodes BITMAP + LZ_RGB + JPEG, and steers the server **away** from
QUIC/GLZ at runtime: `DisplayChannel` advertises `DISPLAY_CAP_PREF_COMPRESSION` and sends
`MSGC_DISPLAY_PREFERRED_COMPRESSION` = **LZ** right after `DISPLAY_INIT` (GLZ is also disabled via
`glz_dictionary_window_size=0`). So unmodified VMs (even on the `auto_glz` default) render with **no per-VM
`<image compression>` change or restart**. The console's **Display ▸ Low bandwidth (LZ) / Raw** items send this
message live (`SpiceSession.SetPreferredCompression`). Porting QUIC/GLZ is only needed to ride the server's
native `auto_glz` for better bandwidth — not required for correctness.

## Removable media (ISO / floppy)

Optical (`.iso`, `device='cdrom'`) and floppy (`.vfd`, `device='floppy'` on the `fdc` bus, target `fda`) media
share one pipeline. Each can be a **file on the server** or **streamed from this PC**: the
local file is served by `Services/NbdServer` (a native C# NBD fixed-newstyle server — file-agnostic, used for
both `.iso` and `.vfd`) over an SSH reverse-forward, and QEMU pulls it over its built-in **NBD client** from a
`<disk type='network' protocol='nbd'>` element (`VirshService.BuildNetworkMediaXml`). NBD is always compiled
into QEMU, so streaming needs **no host package** (the old curl driver / `qemu-block-extra` dependency and its
`QemuCurlAvailable` gate are gone). ISO is exported read-only (`<readonly/>`); **floppy is exported read-write,
so guest writes persist back to the local file**. Change/eject reuse `virsh change-media`/`--eject` (generic by
target). The streaming surfaces are: the Create-VM wizard (a single **install-media** picker — `.iso` →
CD-ROM, `.vfd` → floppy, auto-detected by extension in `BuildInstallMediaOp` and added to the boot order),
the editor's disk context menu (Change ISO/floppy ▸ server/local, Eject), and the console's **CD/DVD** and
**Floppy** toolbar dropdowns. The console's **Floppy** button is hidden unless the VM has a floppy drive, and
the editor's boot-order list includes **Floppy** (`<boot dev='fd'/>`) so a manually-added floppy is bootable.
Caveat: the `fdc` controller is native on `i440fx` (the BIOS-only XP F6-driver-floppy case) but may be
unavailable on `q35`/UEFI.

## USB redirection

The console can redirect a physical USB device on the Windows client into the guest (the SPICE
**usbredir** channel, type 9). It is independent of the guest agent — the guest only needs a USB
controller + the device's normal driver.

- **Native stack (not a C# port):** `SpiceClient` P/Invokes `usbredirhost` + `usbredirparser` +
  `libusb-1.0` (UsbDk backend). These x64 DLLs are loaded by bare name and must sit next to
  `VirtDeck.exe`; stage them in `native\win-x64\` (see its `VERSIONS.txt`) — the csproj copies
  them to output. **The whole process is x64** (`PlatformTarget`) because the DLLs are 64-bit.
- **Channel:** `Channels/UsbredirChannel` is a spicevmc tunnel — it shuttles opaque
  `MSG/MSGC_SPICEVMC_DATA` (101) bytes, which are the raw usbredir wire protocol. On link it creates
  one persistent `Usb/UsbredirHostInstance` (a managed wrapper over `usbredirhost`) with **no device**
  so the usb_redir hello negotiates immediately; a device is attached/detached later via
  `usbredirhost_set_device` (no second hello). One device per channel.
- **usbredirhost owns the device handle** — `set_device(NULL)`/`close` call `libusb_close` themselves;
  never close a handed-off handle from managed code. Callback delegates are rooted for the host
  lifetime (collected-delegate crash otherwise). Disposal order is strict: close hosts (channels)
  **before** `libusb_exit` (`Usb/LibUsbContext`, one shared context + one event thread).
- **Manager/UI:** `Usb/UsbDeviceManager` (on `SpiceSession.Usb`, created when the host advertises a
  usbredir channel) enumerates devices, filters out HID/hubs, and binds a device to a free channel.
  `Forms/UsbDeviceDialog` is the picker (device names come from Windows via `Interop/UsbNames`,
  SetupAPI — non-invasive); the console's **USB** toolbar button opens it.
- **Mass storage:** UsbDk captures a device with a USB *reset*, which blocks/fails (libusb
  `LIBUSB_ERROR_OTHER`) while a drive's volume is mounted and in use. Before binding, the picker calls
  `Interop/UsbStorageDismount` to map the device (VID/PID) → its Windows drive letters (SetupAPI +
  cfgmgr32 parent-walk) and `FSCTL_LOCK_VOLUME`+`FSCTL_DISMOUNT_VOLUME` them (held until capture; flushes
  the FS so there's no surprise-removal corruption — spice-gtk/virt-viewer skip this). On release UsbDk's
  reset makes Windows re-enumerate and auto-remount. If a volume can't be locked (open files), the user
  is told to close them.
- **Host provisioning:** `Services/UsbProvisioning` auto-ensures the domain has a USB controller +
  4 `<redirdev type='spicevmc'>` channels. redirdevs hot-plug (`attach-device --live --config`) when a
  controller exists (then the console reconnects to see them); adding a controller is persistent-only
  and needs a power-cycle.
- **Client driver:** the **UsbDk** kernel driver is required (installed by `installer\` — Inno Setup).
  Without it (or the DLLs), channels still link but redirection is dormant and the picker says why.
  Reliability: bulk/HID/mass-storage solid over the tunnel; isochronous (webcams/audio) is a known
  weak spot. Pin a known-good `libusb-1.0.dll` (virt-viewer 10.x; v11's regressed redirection).

## Conventions

- Forms use manual layout in `*.Designer.cs` (no VS designer/resx), keeping the old WinForms style (`ListView`/`ToolStrip`/`StatusStrip`).
- All wire structs follow the spice-html5 source byte-for-byte; when changing protocol code, check the matching `*.js` in `..\VmManager\VmManager\WebContent\src`.
- Pixels are BGRA end-to-end (matches `Format32bppArgb`) — do NOT copy spice-html5's BGRA→RGBA canvas swap.
- UI updates from channel threads go through `BeginInvoke`; framebuffer access is under `SpiceFramebuffer.SyncRoot`.
