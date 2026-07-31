# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Build

```bash
dotnet build VirtDeck.sln                                # on Windows
dotnet build VirtDeck.sln -p:EnableWindowsTargeting=true # on Linux
```

Four projects (.NET 10):
- **SpiceClient** (`net10.0`) — cross-platform SPICE protocol client library. No UI toolkit: it produces raw BGRA buffers. Uses SkiaSharp for JPEG decode and Concentus for Opus.
- **VirtDeck.Core** (`net10.0`) — cross-platform services and models (`Services/`, `Models/`, `Diagnostics/`, `Imaging/PpmImage`). SSH.NET + `virsh` over SSH; no UI dependency. Namespaces are `VirtDeck.*` so both front-ends consume it without any `using` changes.
- **VirtDeck.Avalonia** (`net10.0`) — the cross-platform UI (Windows + Linux). Assembly name `virtdeck`.
- **VirtDeck** (`net10.0-windows`) — the original WinForms app, Windows-only. Being retired once Avalonia reaches feature parity.

`EnableWindowsTargeting=true` lets the **whole** solution, WinForms included, compile-check on Linux — use it as the no-regression gate when changing shared code. It only proves compilation; Windows runtime behaviour (winmm audio, UsbDk) still needs a real Windows run.

No tests yet. `dotnet run --project VirtDeck.Avalonia` is the fastest smoke test.

## What this is

An app to manage libvirt/KVM VMs on a remote Linux host over SSH, with a **native C# SPICE console** (no WebView2, no spice-html5, no WebSocket bridge — raw TCP to the SSH-forwarded SPICE port).

**Cross-platform status:** the engine, services, and the whole management UI (VM list + details sidebar, Networks, create wizard, editor, export, removable media, clipboard, remote file browser, log viewer) run on Linux and Windows, as does guest audio output. Not yet ported to Avalonia (WinForms only): the USB picker. See `Phases 5-7` in the porting plan.

It is a fresh rewrite of the older `..\VmManager` app, which rendered SPICE via spice-html5 in WebView2 and broke with "Protocol Error" (the WebSocket↔TCP bridge mangled SPICE's binary framing). The native client deletes that failure mode. The SPICE protocol was ported field-for-field from the bundled spice-html5 source at `..\VmManager\VmManager\WebContent\src\*.js` — that JS is the authoritative wire-format reference.

**Flow (Avalonia):** `LoginWindow` → SSH connect → `VmListWindow` → double-click VM → `ConsoleWindow` (native SPICE).
**Flow (WinForms):** `LoginForm` → SSH connect → `VmListForm` → double-click/right-click VM → `VmConsoleForm`.

## Architecture

### SpiceClient (protocol engine)
- `Protocol/` — `SpiceConstants` (enums.js), `SpiceReader`/`SpiceWriter` (little-endian span helpers; `U32BE` for LZ/JPEG-alpha sub-headers).
- `Crypto/SpiceTicket` — RSA ticket auth: `RSA.ImportSubjectPublicKeyInfo` + `Encrypt(OaepSHA1)` of `password + "\0"` (server pubkey is a 162-byte DER SubjectPublicKeyInfo). Replaces spice-html5's hand-rolled OAEP.
- `Transport/ChannelSocket` — one blocking `TcpClient` per channel; `ReadExact`; locked `Write`.
- `Channels/SpiceChannel` (base) — per-channel socket + dedicated read thread; link handshake; common-message loop (SET_ACK→ACK_SYNC + ACK flow control, PING→PONG, NOTIFY). Subclasses: `MainChannel`, `DisplayChannel`, `InputsChannel`, `CursorChannel`.
- `Imaging/` — `SpiceFramebuffer` (a **pinned** top-down BGRA byte buffer exposed as `Pixels`/`Scan0`; all access under `SyncRoot`; toolkit-agnostic — the UI wraps or copies it), `ImageDecoders` (BITMAP, JPEG via Skia, LZ_RGB; **QUIC/GLZ deferred → return null**), `DecodedImage`, `BgraImage.EncodePng`.
- `Audio/` — `IAudioSink` + `AudioSinks.Create()` picks the backend by platform: `WaveOutPlayer` (winmm) on Windows, `PulseAudioSink` (`libpulse-simple`, which PipeWire also provides) on Linux, `NullAudioSink` elsewhere or when libpulse is missing. The simple API is **synchronous** — `pa_simple_write` blocks once the server buffer is full — so `PulseAudioSink` hands chunks to a writer thread through a bounded queue (dropping when full) rather than blocking the playback channel's read thread. That writer thread is the **sole owner** of the `pa_simple*` handle: configure/flush/dispose are posted to it as pending state, so `pa_simple_free` can never race a write in flight. Mute gates the writes; volume is applied in software (the simple API has none, and touching the app's sink volume would outlive the session in the user's mixer).
  **`prebuf` is the anti-crackle setting.** The guest produces audio at exactly 1×, so the server-side buffer level never climbs back on its own — whatever cushion exists at the moment playback starts is the entire jitter budget for the rest of the stream. `prebuf=0` starts playback on the first bytes and leaves the buffer hovering at empty, so every scheduling hiccup underruns: measured ~40 ms and falling, and audibly crackly. `prebuf` = 80 ms of a 200 ms `tlength` measures ~90–120 ms steady on PipeWire's Pulse emulation. `MSG_PLAYBACK_STOP` therefore **drains rather than flushes** (spice-gtk corks for the same reason) — flushing would chop up to `tlength` off the tail of every sound; only mute flushes, because there the user wants silence now.
- `Interop/NativeLibraryResolver` — maps the bare DllImport names to real filenames per platform (`libusb-1.0.dll` vs `libusb-1.0.so.0`, `libpulse-simple.so.0`). There can be only **one** resolver per assembly, so every native dependency — USB and audio — is registered here. `LIBUSB_OPTION_USE_USBDK` is **Windows-only**; setting it elsewhere fails and would silently disable redirection, so `LibUsbContext` gates it (`CaptureAvailable`).
- `SpiceSession` — facade: orchestrates channel bring-up, owns the framebuffer, raises events (`ResolutionChanged`, `FrameDirty`, `CursorSet/Hidden/Reset`, `Disconnected`, `StatusMessage`). Events fire on channel threads — subscribers must marshal.
- `CursorShape` — decoded ALPHA cursor (BGRA + hotspot).

### VirtDeck.Core (shared, cross-platform)
- `Services/` — `SshConnectionManager`, `SshPortForwarder`, `VirshService` (sudo via stdin + marker), `NbdServer`, `UsbProvisioning`, `OsLabelCatalog`. Image compression is steered at the protocol level (see below), not via `virt-xml`.
- `Services/AppSettings` — JSON settings at `%APPDATA%`/`~/.config` + `VirtDeck/settings.json`. **Replaces the old `HKCU\SOFTWARE\VirtDeck` registry storage**; `LegacyRegistryImport` migrates it once on Windows.
- `Imaging/PpmImage` — `virsh screenshot` P6 decoder → raw BGRA.

### VirtDeck.Avalonia (cross-platform UI)
- `Controls/SpiceDisplay` — Avalonia `Control`. A 16 ms pump copies only the **dirty rows** from `SpiceFramebuffer` into a `WriteableBitmap` under `SyncRoot`, then `InvalidateVisual`; `Render` does a 1:1 `DrawImage` with interpolation `None`. Owns the exactly-one-cursor state machine. Avalonia's `Cursor(Bitmap, PixelPoint)` and `StandardCursorType.None` replace the WinForms `CursorInterop` HICON juggling entirely.
- `Styles/JetBrainsClassic.axaml` — JetBrains Classic UI (Darcula / IntelliJ Light) trim, and the app's single source of visual scale. Replacement `ControlTheme`s for `Button`/`JbToolButton`, `CheckBox`, `RadioButton`, `TabControl`/`TabItem` and `JbTableRow`, plus overrides of Fluent's metric keys (`TextControl*`, `ComboBox*`, `ListBoxItemPadding`, `MenuFlyout*`, `ToolTip*`). Merged into `Application.Resources`, **not** `Application.Styles`, because resource lookup reaches `Application.Resources` before the `FluentTheme`'s own dictionaries — that is what makes those overrides win without `/template/` selectors per state.
  **One font baseline: 12.** A bare `TextBlock` defaults to 12 while every Fluent `ControlTheme` sets its own size from `ControlContentThemeFontSize` (14), so labels and the fields beside them rendered two points apart; each retemplated control here drops that setter and falls back to the inherited 12, which widened the gap further. The dictionary pulls the key down to 12, and every metric in it (24px fields and buttons, 22px tool buttons, a 14px check/radio box, `12,4` table rows) is sized for that. **Fields and buttons share one height (24)** — same 1px border, same 3px vertical inset — so a text box, dropdown or spinner lines up with the button next to it; `App.axaml` pins `ButtonSpinner`/`NumericUpDown` to the same key because Fluent sizes those outside `TextControl*`. Views should not set a local `FontSize` or `Height` to line controls up — fix the baseline instead. Headings (wizard title 15, login 16) are the deliberate exceptions.
- `Input/PhysicalKeyMap` — Avalonia `PhysicalKey` → AT set-1 scancode. `PhysicalKey` is positional (W3C `code`), so it is layout-independent — more correct than the WinForms VK table, which reads through the host layout.
- `Views/` — `LoginWindow`, `VmListWindow` (+ `VmDetailsView`), `ConsoleWindow`, `CreateVmWizard`, `VmEditWindow`, `ExportVmDialog`, `RemoteFileBrowserDialog`, the small device dialogs, `LogWindow`, and `MessageDialog` (Avalonia has no `MessageBox`). Row view-models live beside their window (`VmRow`, `NetworkRow`, `DiskEditRow`, …); lists are `ListBox` + `DataTemplate`, not `ListView`.
- `Services/FileDialogs` — the one place the WinForms filter string (`"ISO images (*.iso)|*.iso"`) is translated, into `IStorageProvider` picker types (XDG portal on Linux). Only local paths are accepted — everything downstream needs a real `FileStream`.
- `Controls/RemotePathBox` — textbox + "…" opening `RemoteFileBrowserDialog`. That browser badges files by extension instead of asking the OS for an icon (`ShellIcons`/`SHGetFileInfo` is deleted): these are the *server's* files, so a client-side association would be misleading anyway.
- `Input/AsciiScancodes` — char → AT set-1 scancode + shift, for "Type clipboard". Replaces `VkKeyScan`, which read the *host* layout; scancodes are positional, so the guest's layout decides — a fixed US table is exactly as correct and equally approximate elsewhere.
- Clipboard: Avalonia has no clipboard-change event, so `ConsoleWindow` polls the host text every 500 ms **while focused** and announces a SPICE grab only on change; that same comparison breaks the guest→host→guest loop. Screenshot saves a PNG rather than going to the clipboard (image clipboard transfer is unreliable through Avalonia on X11).
- `Input/IKeyboardGrab` — `X11KeyboardGrab` (`XGrabKeyboard`) on Linux so Alt+Tab/Super reach the guest; no-op elsewhere. **Best-effort by design** — the console must work without it. The grab **must** be issued on Avalonia's own X display connection (dug out of `Window.PlatformImpl` by reflection): X reports key events during an active grab only to the grabbing *client*, and a client is a connection — a grab on a private `XOpenDisplay` takes every key away from Avalonia and the console goes deaf while grabbed. If the display can't be resolved, report unsupported (no grab) rather than falling back to a private connection.

### VirtDeck (WinForms, Windows-only, being retired)
- `Imaging/GdiBgra` — wraps the shared BGRA buffers back into GDI+ bitmaps. The only place this front-end knows about pixel layout.
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
