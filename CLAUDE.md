# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Build

```bash
dotnet build VirtDeck.sln            # everywhere: every project is net10.0 now
```

Four projects (.NET 10), all `net10.0` and all buildable on either OS:
- **SpiceClient**: cross-platform SPICE protocol client library. No UI toolkit: it produces raw BGRA buffers. Uses SkiaSharp for JPEG decode and Concentus for Opus.
- **VirtDeck.Core**: cross-platform services and models (`Services/`, `Models/`, `Unattend/`, `Diagnostics/`, `Imaging/PpmImage`). SSH.NET + `virsh` over SSH; no UI dependency. Namespaces are `VirtDeck.*`.
- **VirtDeck.Avalonia**: the UI (Windows + Linux). Assembly name `virtdeck`.
- **third-party/unattend-generator**: vendored MIT source, not ours. Writes `autounattend.xml`; referenced by `VirtDeck.Core`. Do not edit it; see its `VENDORED.md`.

A clean solution build is the no-regression gate, but it only proves compilation: Windows runtime behaviour (winmm audio, UsbDk capture) still needs a real Windows run, and the reverse for PulseAudio/udisks.

No tests yet. `dotnet run --project VirtDeck.Avalonia` is the fastest smoke test.

## Packaging

Two release artifacts, both self-contained (the .NET runtime is bundled; libusb/usbredir/libpulse deliberately are not; see `packaging/README.md`):

```bash
packaging/build-appimage.sh 1.2.3   # Linux   -> publish/VirtDeck-1.2.3-x86_64.AppImage
publish.bat                         # Windows -> installer\output\VirtDeckSetup-*.exe
```

`.github/workflows/publish.yml` builds both on every push to `main`; `release.yml` does the same on a `v*` tag and attaches both to a GitHub Release (the release job needs *both* jobs green; a half-published release looks finished but isn't). `packaging/icons/make-icons.py` is the single source for the app icon: it rasterises the hicolor PNGs, the scalable SVG and the Windows `.ico` from one set of numbers, so no build machine needs an SVG renderer.

## What this is

An app to manage libvirt/KVM VMs on a remote Linux host over SSH, with a **native C# SPICE console** (no WebView2, no spice-html5, no WebSocket bridge; raw TCP to the SSH-forwarded SPICE port).

**Cross-platform status:** done. Engine, services, the whole management UI, guest audio and USB redirection run on Linux and Windows from the single Avalonia front-end. The WinForms app that this was ported from is deleted (it is in git history if a WinForms detail ever needs checking).

It is a fresh rewrite of the older `..\VmManager` app, which rendered SPICE via spice-html5 in WebView2 and broke with "Protocol Error" (the WebSocket↔TCP bridge mangled SPICE's binary framing). The native client deletes that failure mode. The SPICE protocol was ported field-for-field from the bundled spice-html5 source at `..\VmManager\VmManager\WebContent\src\*.js`; that JS is the authoritative wire-format reference.

**Flow:** `LoginWindow` → SSH connect → `VmListWindow` → double-click VM → `ConsoleWindow` (native SPICE).

## Architecture

### SpiceClient (protocol engine)
- `Protocol/`: `SpiceConstants` (enums.js), `SpiceReader`/`SpiceWriter` (little-endian span helpers; `U32BE` for LZ/JPEG-alpha sub-headers).
- `Crypto/SpiceTicket`: RSA ticket auth via `RSA.ImportSubjectPublicKeyInfo` + `Encrypt(OaepSHA1)` of `password + "\0"` (server pubkey is a 162-byte DER SubjectPublicKeyInfo). Replaces spice-html5's hand-rolled OAEP.
- `Transport/ChannelSocket`: one blocking `TcpClient` per channel; `ReadExact`; locked `Write`.
- `Channels/SpiceChannel` (base): per-channel socket + dedicated read thread; link handshake; common-message loop (SET_ACK→ACK_SYNC + ACK flow control, PING→PONG, NOTIFY). Subclasses: `MainChannel`, `DisplayChannel`, `InputsChannel`, `CursorChannel`.
- `Imaging/`: `SpiceFramebuffer` (a **pinned** top-down BGRA byte buffer exposed as `Pixels`/`Scan0`; all access under `SyncRoot`; toolkit-agnostic; the UI wraps or copies it), `ImageDecoders` (BITMAP, JPEG via Skia, LZ_RGB; **QUIC/GLZ deferred → return null**), `DecodedImage`, `BgraImage.EncodePng`.
- `Audio/`: `IAudioSink` + `AudioSinks.Create()` picks the backend by platform: `WaveOutPlayer` (winmm) on Windows, `PulseAudioSink` (`libpulse-simple`, which PipeWire also provides) on Linux, `NullAudioSink` elsewhere or when libpulse is missing. The simple API is **synchronous** (`pa_simple_write` blocks once the server buffer is full), so `PulseAudioSink` hands chunks to a writer thread through a bounded queue (dropping when full) rather than blocking the playback channel's read thread. That writer thread is the **sole owner** of the `pa_simple*` handle: configure/flush/dispose are posted to it as pending state, so `pa_simple_free` can never race a write in flight. Mute gates the writes; volume is applied in software (the simple API has none, and touching the app's sink volume would outlive the session in the user's mixer).
  **`prebuf` is the anti-crackle setting.** The guest produces audio at exactly 1×, so the server-side buffer level never climbs back on its own; whatever cushion exists at the moment playback starts is the entire jitter budget for the rest of the stream. `prebuf=0` starts playback on the first bytes and leaves the buffer hovering at empty, so every scheduling hiccup underruns: measured ~40 ms and falling, and audibly crackly. `prebuf` = 80 ms of a 200 ms `tlength` measures ~90-120 ms steady on PipeWire's Pulse emulation. `MSG_PLAYBACK_STOP` therefore **drains rather than flushes** (spice-gtk corks for the same reason). Flushing would chop up to `tlength` off the tail of every sound; only mute flushes, because there the user wants silence now.
- `Interop/NativeLibraryResolver`: maps the bare DllImport names to real filenames per platform (`libusb-1.0.dll` vs `libusb-1.0.so.0`, `libpulse-simple.so.0`). There can be only **one** resolver per assembly, so every native dependency (USB and audio) is registered here. `LIBUSB_OPTION_USE_USBDK` is **Windows-only**; setting it elsewhere fails and would silently disable redirection, so `LibUsbContext` gates it (`CaptureAvailable`).
- `SpiceSession`: the facade. Orchestrates channel bring-up, owns the framebuffer, raises events (`ResolutionChanged`, `FrameDirty`, `CursorSet/Hidden/Reset`, `Disconnected`, `StatusMessage`). Events fire on channel threads; subscribers must marshal.
- `CursorShape`: decoded ALPHA cursor (BGRA + hotspot).

### VirtDeck.Core (shared, cross-platform)
- `Services/`: `SshConnectionManager`, `SshPortForwarder`, `VirshService` (sudo via stdin + marker), `NbdServer`, `UsbProvisioning`, `OsLabelCatalog`. Image compression is steered at the protocol level (see below), not via `virt-xml`.
- **SSH authentication**: `SshConnectionManager.ConnectWithPassword` / `ConnectWithKey` (host + **port**, no longer hardwired to 22). Both build a plain `ConnectionInfo` (the base class is not `IDisposable`, unlike `PasswordConnectionInfo`), so the manager owns the credential lifetime: the auth method and, for key auth, the `PrivateKeyFile` are **fields disposed after the client**, never `using`-scoped in the connect call, because `DownloadFileAsync` and `RunSudoCommandStreaming` each open a second client from `_client.ConnectionInfo` and would otherwise authenticate with disposed key material.
  **The sudo password is a separate secret** (`_sudoPassword`): with key auth there is no login password, and a NOPASSWD account has no sudo password either, which `FeedSudoPassword` already tolerated (sudo simply never reads stdin). `CheckSudo()` runs `sudo -S -p '' true` once from the login window and returns a message instead of throwing, so a wrong or missing sudo password is reported on the login screen rather than as a VM list that fails to load.
  `Services/SshKeyDiscovery` scans `~/.ssh` (`%USERPROFILE%\.ssh` on Windows) for the key dropdown: a file counts as a key when it has a `.pub` sibling **or** its first line says `PRIVATE KEY`, stock names sorted strongest first. `NeedsPassphrase` probes with `new PrivateKeyFile(path)` and treats only `SshPassPhraseNullOrEmptyException` as "encrypted"; anything unreadable answers false on purpose, so the connect attempt reports the real error instead of the UI asking for a passphrase that would not help. SSH.NET 2024.2.0 has **no ssh-agent support**, so keys are always read from disk. Settings persist the mode, key path and port, never the passphrase or the sudo password.
- `Services/AppSettings`: JSON settings at `%APPDATA%`/`~/.config` + `VirtDeck/settings.json`. **Replaces the old `HKCU\SOFTWARE\VirtDeck` registry storage**; `LegacyRegistryImport` migrates it once on Windows.
- `Imaging/PpmImage`: `virsh screenshot` P6 decoder → raw BGRA.
- `Unattend/`: the answer-file boundary, and the only code that references the vendored generator. `UnattendConfig` is the flat serialisable model (one property per tab group, each declared in its own file under `Unattend/Model/` and named after the tab that edits it), `UnattendConfigMapper` projects it onto the library's `Configuration`, `UnattendXml.Build` is the single entry point (throws only `UnattendBuildException`), `UnattendCatalog` owns the one `UnattendGenerator` instance and exposes its lookup tables as `UnattendOption` lists, `UnattendPreset` is the JSON save/load, and `UnattendMedia` wraps the result in the answer disc. See "Unattended Windows setup" below.

### VirtDeck.Avalonia (cross-platform UI)
- `Controls/SpiceDisplay`: Avalonia `Control`. A 16 ms pump copies only the **dirty rows** from `SpiceFramebuffer` into a `WriteableBitmap` under `SyncRoot`, then `InvalidateVisual`; `Render` does a 1:1 `DrawImage` with interpolation `None`. Owns the exactly-one-cursor state machine, built from `Cursor(Bitmap, PixelPoint)` and `StandardCursorType.None`.
- `Styles/JetBrainsClassic.axaml`: JetBrains Classic UI (Darcula / IntelliJ Light) trim, and the app's single source of visual scale. Replacement `ControlTheme`s for `Button`/`JbToolButton`, `CheckBox`, `RadioButton`, `TabControl`/`TabItem` (top-placed; `JbSideTabControl`/`JbSideTabItem` are the left-placed pair, keyed, for the Windows-setup customization window), `JbTableRow`, and the menu family (`Menu`/`JbTopLevelMenuItem` for the console toolbar strip, `MenuItem`/`Separator`/`ContextMenu`/`MenuFlyoutPresenter` for the popups), plus overrides of Fluent's metric keys (`TextControl*`, `ComboBox*`, `ListBoxItemPadding`, `ToolTip*`). `JbErrorForeground` is the brush for inline validation text (a message about something the user must change, never an ordinary hint; those are 0.7 opacity), themed for both light and dark, so no view hardcodes a red. `JbGroupBox` is a `ControlTheme` for `HeaderedContentControl` carrying the app's titled-frame idiom, and `JbGroupBoxBorder`/`JbGroupBoxHeader` are its two brushes, deliberately outside the theme dictionaries because both are grey with an alpha channel and so read correctly on either face. Merged into `Application.Resources`, **not** `Application.Styles`, because resource lookup reaches `Application.Resources` before the `FluentTheme`'s own dictionaries; that is what makes those overrides win without `/template/` selectors per state.
  **One font baseline: 12.** A bare `TextBlock` defaults to 12 while every Fluent `ControlTheme` sets its own size from `ControlContentThemeFontSize` (14), so labels and the fields beside them rendered two points apart; each retemplated control here drops that setter and falls back to the inherited 12, which widened the gap further. The dictionary pulls the key down to 12, and every metric in it (24px fields and buttons, 22px tool buttons, a 14px check/radio box, `12,4` table rows) is sized for that. **Fields and buttons share one height (24)** (same 1px border, same 3px vertical inset), so a text box, dropdown or spinner lines up with the button next to it; `App.axaml` pins `ButtonSpinner`/`NumericUpDown` to the same key because Fluent sizes those outside `TextControl*`. Views should not set a local `FontSize` or `Height` to line controls up; fix the baseline instead. Headings (wizard title 15, login 16) are the deliberate exceptions.
- `Input/PhysicalKeyMap`: Avalonia `PhysicalKey` → AT set-1 scancode. `PhysicalKey` is positional (W3C `code`), so it is layout-independent, more correct than a VK table, which reads through the host layout. **Extended keys are `0xE0 | (atCode << 8)`** (e.g. PageUp = `0x49E0`), matching spice-html5 utils.js, NOT `0xE0XX`. The key-up high bit is applied in `InputsChannel.SendKey`.
- `Views/`: `LoginWindow`, `VmListWindow` (+ `VmDetailsView`), `ConsoleWindow`, `CreateVmWizard`, `VmEditWindow`, `ExportVmDialog`, `RemoteFileBrowserDialog`, `UsbDeviceDialog`, `Unattend/UnattendWindow` (+ its per-tab `UserControl`s), the small device dialogs, `LogWindow`, and `MessageDialog` (Avalonia has no `MessageBox`). Row view-models live beside their window (`VmRow`, `NetworkRow`, `DiskEditRow`, `UsbDeviceRow`, …); lists are `ListBox` + `DataTemplate`, not `ListView`.
- `Services/FileDialogs`: the one place the WinForms filter string (`"ISO images (*.iso)|*.iso"`) is translated, into `IStorageProvider` picker types (XDG portal on Linux). Only local paths are accepted; everything downstream needs a real `FileStream`.
- `Services/DropFiles`: the one place the drag-and-drop dialect is translated, the same way `FileDialogs` handles the filter string. Avalonia 12 replaced `IDataObject`/`DataFormats.FileDrop` with `IDataTransfer`/`DataFormat.File`, so Avalonia 11 snippets do not apply; `LocalFiles` filters a drop down to real local files (directories and portal handles are dropped) through the same `FileDialogs.LocalPathOf` the pickers use. **X11 drag-and-drop needs Avalonia 12.1+**: 12.0.x has no XDND in its X11 backend at all and raises no drop events on Linux, which is why the csproj pins 12.1 as the floor.
  Drop targets: the console window (below), the Create-VM wizard's General page (an ISO or floppy image fills the install media and switches to "stream from this PC"), a removable-drive row in `VmEditWindow` (staged as that drive's media; the extension must match the drive kind), and `LoginWindow`'s key panel (adds and selects a private key). Not `RemotePathBox`: its path is on the *server*, so a local path there would be meaningless.
- Console drops: any file is sent into the guest with `SpiceSession.SendFile` (the vdagent file-transfer channel; the guest agent picks where it lands), and the status bar shows one shared progress bar and Cancel for the whole drop, refcounted in `_activeXfers` and driven **only** by the session's file events, because `SendFile` returns silently for a missing file or a departed agent. A single ISO or floppy image dropped on a VM that has the matching drive **always asks** (`MessageDialog.Choose`) whether to insert it or send it; with no guest agent the send button is disabled with a reason rather than the drop silently mounting. Nothing is ever mounted unconfirmed, and a shut-off VM is a valid drop target (the drive targets are detected on the powered-off paths too, and the media change goes straight to the saved config).
- `Controls/RemotePathBox`: textbox + "…" opening `RemoteFileBrowserDialog`. That browser badges files by extension instead of asking the OS for an icon (`ShellIcons`/`SHGetFileInfo` is deleted): these are the *server's* files, so a client-side association would be misleading anyway.
- `Input/AsciiScancodes`: char → AT set-1 scancode + shift, for "Type clipboard". Replaces `VkKeyScan`, which read the *host* layout; scancodes are positional, so the guest's layout decides; a fixed US table is exactly as correct and equally approximate elsewhere.
- Clipboard: **text and images, both ways**; see "Clipboard sharing" below for the whole story. `Services/HostClipboard` is the one place the Avalonia clipboard dialect is translated, the way `DropFiles` is for drag-and-drop.
- `Input/IKeyboardGrab`: `X11KeyboardGrab` (`XGrabKeyboard`) on Linux so Alt+Tab/Super reach the guest; no-op elsewhere. **Best-effort by design**; the console must work without it. The grab **must** be issued on Avalonia's own X display connection (dug out of `Window.PlatformImpl` by reflection): X reports key events during an active grab only to the grabbing *client*, and a client is a connection; a grab on a private `XOpenDisplay` takes every key away from Avalonia and the console goes deaf while grabbed. If the display can't be resolved, report unsupported (no grab) rather than falling back to a private connection.

### Host capability checks

`VirshService` probes the host once at login (`VmListWindow.LoadHostCapabilitiesAsync`) and caches the results as properties:

| Property | Check | Used by |
|---|---|---|
| `VirtSparseAvailable` | `which virt-sparsify` | Export dialog: disables sparse checkbox |
| `CheckHostCapabilities()` | `/proc/cpuinfo` svm/vmx, `/dev/kvm`, `systemctl is-active libvirtd` | Status-bar indicators in `VmListWindow` |

All checks default to `true`/available on SSH error to avoid false negatives, **except `VirtSparseAvailable`** which defaults to `false` (safe: prevents a silent no-op export).

### Install media identification and device defaults

The Create-VM wizard identifies the install media and picks the guest's disk bus and NIC model from it.

- **Reading the ISO:** `Services/IsoIdentifier` reads the ISO 9660 volume descriptors, which ECMA-119
  fixes at **sector 16 (byte offset 32768)**, so identifying an image is a 16 KiB read at a known offset
  and never a download. Local files seek; server files get one
  `dd if=… bs=2048 skip=16 count=8 | base64` through `RunSudoCommand` (sudo + base64'd path, the same
  idiom as the rest of the host file access, so root-owned images under `/var/lib/libvirt/images` are
  readable). It scans a few descriptors rather than assuming the Primary is first, because an El Torito
  boot record can precede it. UDF-only images and floppy images have no PVD and answer **null**, which
  callers must treat as unknown rather than as any particular OS.
- **Matching:** `Services/GuestOsProfile.Match` runs the hand-curated `mediaPatterns` table from
  `Data/osinfo-labels.json` against the volume id, first match wins (the table is ordered specific-first).
  Same data file, same idea as libosinfo: osinfo-db's media entries are largely volume-id regexes too.
- **Windows media does not carry its version, and the files that would say are unreachable.** Windows 10
  and 11 both ship `CCCOMA_X64FRE_EN-US_DV9` and friends. `sources/install.wim` holds the build number in
  its XML, but a retail Windows ISO exposes **only `README.TXT`** through ISO 9660 and puts the real tree
  on the UDF side (verified against a 25H2 image: the ISO 9660 root has three entries), so reading it
  would mean implementing UDF. Modern Windows therefore preselects **`win10`** and marks the pattern
  `osAssumed: true`, which is what lets the **file name** break the tie: Microsoft's download tool names
  its images `Win11_24H2_English_x64.iso`, so `fileNamePatterns` refines `win10` to `win11` (or the
  Server releases) when the name says so. That refinement **only ever refines**: the volume id must have
  identified the family first, and a file-name pattern is ignored unless its family agrees, because a
  name is user-controlled and survives renaming while the descriptor is written at mastering time. An
  exact volume-id match is never overridden. Only the legacy releases, whose volume ids *are* version-specific (`WXPVOL_EN`,
  `WIN98`, …), preselect an exact OS. Patterns carry an `os` short id where the regex pins one (Ubuntu
  and Debian capture their version into `ubuntu$1` / `debian$1`); the rest are family-only, which still
  gets the devices right because the family is all the device choice needs.
- **The policy** is `Services/GuestDevices`: Linux gets `virtio` (paravirtual, datapath stays in the host
  kernel), BIOS-only guests get `ide` + `rtl8139`, and **everything else including unknown gets
  `sata` + `e1000e`**, which every installer has drivers for. Unknown deliberately lands on the emulated
  devices: a VM that installs slowly beats one that cannot see its disk or NIC at all. `OsFamily.Unknown`
  is a real answer, not a missing one.
- **The name box folds illegal characters as you type:** a domain name is `[a-zA-Z0-9_.-]+`, so
  `CreateVmWizard.SanitizeName` replaces anything else with `_` on every `TextChanged` (space becomes
  `_`, so "Windows 11" types itself into "Windows_11"). One character for one, so the caret keeps its
  place; it is restored explicitly because assigning `Text` would otherwise jump it to the end mid-word,
  and the assignment is reentrancy-guarded because it raises `TextChanged` again. `ValidateGeneralAsync`
  keeps the regex check as the backstop that owns the rule, but only the empty name can now reach it.
- **Media pickers remember where you were:** `Services/MediaLocations` is the single entry point for
  every ISO/floppy picker (wizard, editor, console) and the one place the directory is stored.
  `AppSettings.LastLocalMediaDir` and `LastServerMediaDir` are kept **separate**, because a path on this
  PC means nothing to the host browser and the other way round; one shared value would send every other
  picker somewhere useless. Only a confirmed pick is remembered, never a half-typed path: that is why
  `RemotePathBox` raises `Browsed` (the browser returned a file) as well as `PathChanged` (per keystroke),
  and why it takes a `StartDirectory` used only while the box is empty. A caller with something better
  than history (the drive's current medium in `VmEditWindow`) passes it as the initial path and the
  remembered directory is the fallback; the server fallback is `/var/lib/libvirt/images`.
- **Page order follows the dependency:** the General page puts the install media directly under the name,
  *above* vCPUs/memory/OS type/firmware, because identifying the media fills the OS type in, so reading
  the page top down is the order the fields feed each other. The mode defaults to **stream from this PC**
  (the local file is the common case; the server picker is the second option), and both media inputs use a
  full "Browse…" button: `RemotePathBox.BrowseText` swaps its square "…" for a label and hands width and
  padding back to the Button theme, so the two rows match. Other `RemotePathBox` users keep the "…".
- **The last page is a summary**, built fresh on every entry (`BuildSummary`) by reading the pages
  themselves, not the fields `FinishAsync` captures, so going Back and changing something is reflected.
  Its **"Start the VM after creation"** checkbox is on by default and is the only thing that decides
  whether Finish calls `StartVmAsync`; `VmStarted` reports what happened, and `VmListWindow` opens the
  console only when it is true, because a defined-but-shut-off VM has no console to connect to.
- **The OS dropdown is the source of truth**, not the ISO: `CreateVmWizard.GuestProfile()` reads the
  selection and only falls back to the detected family while it still says "generic". Detection
  preselects a profile **only until the user picks one themselves** (`_osUserPicked`; the programmatic
  writes are fenced with `_osSelectionIsOurs` so they do not count as a pick). The seeded NIC and boot
  disk stay adjustable until the user edits those lists (`_nicsTouched` / `_disksTouched`), so changing
  the OS afterwards still moves the defaults and an explicit choice is never undone.

### Export VM (`Views/ExportVmDialog.axaml.cs`)

Right-click → Export VM → saves a `.tar` containing `domain.xml` + `disks/` subdirectory.

**Streaming pipeline** (no temp file for known-size disks):
```
SSH (sudo dd) → Pipe.Writer → KnownLengthStream → TarWriter → FileStream
```
`TarWriter` (PAX format) requires the entry size in the header before data flows. `KnownLengthStream` wraps the pipe reader and reports a pre-measured `Length` while reading sequentially, satisfying TarWriter without buffering the whole file locally.

**Sparse before export**: optional checkbox (requires `libguestfs-tools` on host, VM must be off):
- Runs `virt-sparsify --in-place` on each selected disk before downloading; reclaims unused qcow2 clusters, shrinking the on-disk file size
- Progress output from virt-sparsify streams live to the status label via `SshConnectionManager.RunSudoCommandStreaming`
- Size is measured **after** sparsify so the tar entry header reflects the shrunken size
- Checkbox disabled (with tooltip + hint label) when the VM is running or when virt-sparsify is not installed

The **save picker** is shown first (from `Opened`), while the VM config fetch and the virt-sparsify availability check run in the background; both SSH calls complete while the user is browsing for a save location.

## Clipboard sharing

Text and images are mirrored both ways through the guest agent. **Files are not clipboard content
here**: they go into the guest by dropping them on the console (see "Console drops" above), over
`VD_AGENT_FILE_XFER`.

**Typed, not text-only.** `MainChannel.GrabClipboard(types)` announces what the host offers,
`SendClipboardData(type, bytes)` answers the guest's request, and `SpiceSession` raises
`ClipboardTextFromGuest` / `ClipboardImageFromGuest(type, bytes)` / `ClipboardRequestedByGuest(type)`.
The client advertises **PNG + BMP** and always sends PNG: the Windows agent maps `CF_DIB` to either,
the Linux agent maps all four, and PNG is the one every agent implements. Incoming PNG/BMP/JPEG all
decode through `Avalonia.Media.Imaging.Bitmap`, so there is no format switch on the receive side.

**A request that cannot be served must be answered with `VD_AGENT_CLIPBOARD_NONE`**, never with
silence: the Windows agent blocks its paste for a full three seconds waiting for a reply.

**Large payloads changed two invariants in `MainChannel`.** `SendClipboardData` hands the encode and
enqueue to a pool thread, because `EnqueueAgentMessage` blocks on the 8-deep send queue and the
caller is the UI thread. `HandleIncomingAgentData` caps a reassembled message at
`MaxAgentMessageBytes` (32 MiB); before images nothing on that path was ever bigger than a few KiB
and the `u32` size field was simply trusted.

**The host poll is two-tier** (`ConsoleWindow.PollHostClipboardAsync`). Every 500 ms it asks only
which formats are on offer (`GetDataFormatsAsync`: a TARGETS round trip on X11,
`EnumClipboardFormats` on Windows). It pulls the actual bytes only when that set changed or when
something set `_clipboardReadPending`. Reading a multi-megabyte bitmap out of the X11 selection
twice a second is not an option. The cost: **two images copied in a row from the same app offer an
identical format set and are not noticed**, which is why `Activated` forces a re-read; alt-tabbing to
the console before pasting is the workflow people actually have.

**The guest→host→guest loop is broken by a re-read, not by the bytes sent** (`SeedAfterGuestWriteAsync`).
The baseline has to be whatever the *next poll* will read, and that is not the bytes handed to
`SetImageAsync`: the image goes onto the clipboard as a decoded `Bitmap` and comes back PNG-encoded by
us. Hashing what the guest sent would not match, the poll would call it a new host copy, and the guest
would get its own image back.

**A `Bitmap` on the clipboard must outlive the call, and one read back is not yours to dispose.**
Both halves of this crashed the app (`ObjectDisposedException` on `Ref<IBitmapImpl>` inside
`Avalonia.X11.Selections.SelectionDataProvider`, on the X11 event loop, where no `catch` of ours can
reach it), and a clipboard manager makes it immediate: those request the contents on every ownership
change.

- Clipboard ownership is **lazy**: the bytes are produced when something asks for the selection, and
  Avalonia produces them by calling `Bitmap.Save` on the instance it was given, then. So
  `HostClipboard.SetImageAsync` parks it in `_offered` and retires it only when a later set replaces
  it, instead of the obvious `using`. Retiring is safe because selection requests are served on the
  UI thread, the same thread that sets; it is deliberately **not** released when a console closes,
  since the clipboard outlives the window and `_offered` is shared across consoles.
- A read while we still own the clipboard is **not a round trip**:
  `X11ClipboardImpl.TryGetDataAsync` returns the stored `IAsyncDataTransfer` unchanged when the
  selection owner is itself, so `TryGetBitmapAsync` hands back the very instance we put up, still
  live. `ReadImageAsync` therefore disposes only a bitmap that is not `_offered`; those the clipboard
  decodes for a foreign owner are created per read and are ours to free.

**Why files are not pasted.** The vdagent type that would carry them,
`VD_AGENT_CLIPBOARD_FILE_LIST`, carries **paths, not bytes**: the guest resolves them against a
WebDAV share the client itself would have to host (`dav://localhost:9843` via gvfs on Linux,
`\\localhost@9843\DavWWWRoot` via the mini-redirector on Windows), which also needs an
`org.spice-space.webdav.0` spiceport device on the domain and `spice-webdavd` running in the guest.
That whole route was implemented and then removed; drag-and-drop covers the same need with nothing
to install. So the client never announces `FILE_LIST`, and a host file copy is announced as whatever
text comes with it, or as nothing at all. Guest→host file copy does not exist in this protocol
either (`vdagent x11.c`: "we don't support file copying in this direction yet").

**Sharing is unconditional, so the console has no Clipboard menu at all.** There was a per-VM
"Share images" toggle; it gated only the last step (announce, serve, mirror) while the costly parts
ran regardless, because `HostClipboard.ReadAsync` decodes before the switch and `MainChannel`'s grab
handler requests image types from `PreferredTypes` with no UI gate. So it bought nothing and is
gone, and with it the menu that held it. `AppSettings.VmSettings.ClipboardImagesOff` stays in the
model, written by nobody, so old settings files remain valid and re-exposing the opt-out is a menu
item rather than a migration. The keystroke fallback **"Type clipboard"** (`Input/AsciiScancodes`)
lives under **Keyboard** and is **always there**, never hidden or re-homed on agent state: a command
that comes and goes with something the user cannot see reads as a bug, not as a hint.

## Cursor rule (the key requirement)

Exactly ONE cursor must be visible over the display: never zero, never two. `SpiceDisplay.ApplyCursor()` is the single chokepoint and just sets `Control.Cursor` (the OS never stacks cursors):
- ShowHostCursor policy → the default arrow;
- before any cursor message → the default arrow (no missing cursor at startup);
- guest hid the cursor → `StandardCursorType.None`;
- otherwise → the built SPICE cursor (or default if RESET).

The "Show host cursor" toggle (per-VM, in `AppSettings`) is the user's accepted fallback.

## Image compression / QUIC

QUIC and GLZ are not decoded. The client decodes BITMAP + LZ_RGB + JPEG, and steers the server **away** from
QUIC/GLZ at runtime: `DisplayChannel` advertises `DISPLAY_CAP_PREF_COMPRESSION` and sends
`MSGC_DISPLAY_PREFERRED_COMPRESSION` = **LZ** right after `DISPLAY_INIT` (GLZ is also disabled via
`glz_dictionary_window_size=0`). So unmodified VMs (even on the `auto_glz` default) render with **no per-VM
`<image compression>` change or restart**. `SpiceSession.SetPreferredCompression` can send the message live at
any time, but nothing in the UI calls it any more: the console's old **Display ▸ Low bandwidth (LZ) / Raw**
radio pair is gone, so LZ on link is the only setting. Porting QUIC/GLZ is only needed to ride the server's
native `auto_glz` for better bandwidth, not required for correctness.

## Removable media (ISO / floppy)

Optical (`.iso`, `device='cdrom'`) and floppy (`device='floppy'` on the `fdc` bus, target `fda`) media
share one pipeline. Each can be a **file on the server** or **streamed from this PC**: the
local file is served by `Services/NbdServer` (a native C# NBD fixed-newstyle server, file-agnostic, used for
both kinds) over an SSH reverse-forward, and QEMU pulls it over its built-in **NBD client** from a
`<disk type='network' protocol='nbd'>` element (`VirshService.BuildNetworkMediaXml`). NBD is always compiled
into QEMU, so streaming needs **no host package** (the old curl driver / `qemu-block-extra` dependency and its
`QemuCurlAvailable` gate are gone). ISO is exported read-only (`<readonly/>`); **floppy is exported read-write,
so guest writes persist back to the local file**. Change/eject reuse `virsh change-media`/`--eject` (generic by
target). The streaming surfaces are: the Create-VM wizard (a single **install-media** picker: ISO →
CD-ROM, floppy image → floppy, classified by `Services/FloppyImage` in `BuildInstallMediaOpAsync` and added
to the boot order),
the editor's disk context menu (Change ISO/floppy ▸ server/local, Eject), and the console's **CD/DVD** and
**Floppy** toolbar dropdowns. The console's **Floppy** button is hidden unless the VM has a floppy drive, and
the editor's boot-order list includes **Floppy** (`<boot dev='fd'/>`) so a manually-added floppy is bootable.
Caveat: the `fdc` controller is native on `i440fx` (the BIOS-only XP F6-driver-floppy case) but may be
unavailable on `q35`/UEFI.

**A floppy is identified by size, not by extension** (`VirtDeck.Core/Services/FloppyImage`). QEMU never reads
the extension (libvirt attaches the image as `<driver type='raw'/>`), so `.vfd`, `.ima` and `.flp` are the same
bytes under different conventions and all three name a floppy and nothing else. **`.img` cannot be read off the
name**: it is equally the convention for raw hard-disk images and hybrid ISOs, yet FreeDOS and friends ship
their install sets as `.img`, so neither accepting nor refusing the extension outright is right. The tie-break
is the file size against the standard PC geometries (160K through 2.88M), which is how QEMU itself picks the
geometry to emulate (`fd_formats` in `hw/block/fdc.c`), so the client agrees with the thing that has to accept
the image. An `.img` of any other size stays **unclassified** rather than guessed at: it is not a valid drop
onto a drive (on the console it is simply sent to the guest), and the "All files" filter is the deliberate
escape hatch for an exotic geometry. Because settling an `.img` on the server costs a `stat` over SSH,
`CreateVmWizard` resolves it in the background detect pass and caches the verdict per path; drops are always
local files, so `DropFiles` can stat inline.

## Unattended Windows setup (autounattend.xml)

**VirtDeck does not write the answer file; it fills in a form and hands it to
[cschneegans/unattend-generator](https://github.com/cschneegans/unattend-generator)**, the MIT C#
library behind schneegans.de's online generator, vendored at `third-party/unattend-generator/`. That
library is the reference this window was modelled on in the first place, and it already owns the
unattend schema, its element ordering, the registry tweak behind each "disable X" toggle, and the
PowerShell payloads for the settings that have no unattend element at all. Adopting it turns "add a
section" into a UI page plus a mapper arm, instead of implementing a piece of Windows Setup.
`third-party/unattend-generator/VENDORED.md` has the pinned commit, the three-line local delta and
the update procedure; **read it before touching that directory**, because the assembly name is baked
into `Bloatware.json`'s `$type` discriminators and the repo's no-em-dash rule does not apply there.

`Views/Unattend/UnattendWindow` is sections on the left, page on the right, ordered by where the
reference tool puts the first section each page covers. **Seventeen pages cover its twenty-seven
sections**: where two or three belong together they are group boxes on one page, each keeping the web
tool's exact heading so the mapping back to schneegans.de stays legible. **The window never names
its pages**: it walks its `TabControl`'s items and calls `IUnattendTab.Load`/`Apply` on whichever
implement it, so adding a section is a `TabItem` in the markup plus the page, with no line to maintain
anywhere else. Both methods take the whole `UnattendConfig` rather than the group the page owns,
because most pages render more than one section. Two invariants go with that: every page is
constructed with the window (they are literal elements, not template output), so `Apply` on a page the
user never opened has to be correct; and pages own disjoint parts of the config, so `Apply` order must
never matter. Where two sections constrain each other, the resolution belongs in
`UnattendConfigMapper`.

**A page is written in one vocabulary, and none of it is local to the page.** The frame is
`HeaderedContentControl` with `Theme="{StaticResource JbGroupBox}"`, which is the app's long-standing
group-box markup (`#33808080` frame, `#11808080` header strip) turned into a `ControlTheme` because
this window alone has a few dozen of them. `Styles/UnattendPage.axaml` holds the class vocabulary,
merged into `Application.Styles`: `.page` and `.options` for the two levels of spacing, `.note` and
`.subnote` for dimmed prose (the second indented 21px to sit under the option it belongs to),
`.under` for the subtree one option owns and greys out in a single `IsEnabled` assignment,
`.columnheader`, `.error` for a message about something that must change before the file can be
generated, and `.script` for a monospaced box holding a script or a fragment of markup. A page that
declares its own `UserControl.Styles` is a page that will drift from the other sixteen.

**`RadioButton.GroupName` is scoped to the window, not to the page.** All seventeen pages are
constructed at once and live in one `TabControl`, so two pages using the same group name would form a
single group and silently uncheck each other. Every group name is therefore prefixed with its page
(`SetupExpress`, `ComputerName`, `ExplorerHideFiles`, `AccessLockKeys`, ...).

**Four pages render a tickable list out of one of the generator's tables** (bloatware, desktop icons,
folders on Start, visual effects). They share `CheckRow` and a single `DataTemplate` in
`App.axaml`'s `Application.DataTemplates`, and differ only in the catalog they fill from and the
`WrapPanel.ItemWidth` they flow into. `CheckRow.From`/`CheckedIds` convert between the rows and the
`List<string>` of ids the model stores. In all four the tick list is the **whole** setting rather than
a set of overrides: the mapper expands it to on/off pairs across the entire table, because Windows
replaces its own list rather than merging.

**Two pages are add/remove lists instead** (`ScriptRow`, `ComponentXmlRow`), because neither has a
natural number of entries and each entry needs a text box rather than a cell. Both rows notify for the
same reason: **one of their own cells narrows another**. A script that edits the default user's
registry hive can only be `.reg`, `.cmd` or `.ps1`, and a component only has settings in some passes,
so choosing the first cell shortens the second cell's list. The row coerces its selection *before*
announcing either change, or the `ComboBox` sees a moment where its selection is not in its items and
clears itself. The narrowing is a courtesy; the rule lives in the generator
(`ScriptExtensions.GetAllowedTypes`, `Component.Passes`) and the mapper lets it refuse, because
quietly running somebody's VBScript through a different interpreter is worse than saying no.

**The pickers are `ComboBox`es filled through `Views/Unattend/OptionBox`**, which selects and reads
back by id so no page repeats the find-by-id pair. `UnattendOption.ToString` is what lets them work
with no `DataTemplate`. An id the catalog does not have clears the box rather than falling back to its
first entry, so an unanswered picker is visibly unanswered. `UserLocales` is 686 entries and
`GeoLocations` 267; a plain `ComboBox` copes but scrolls poorly, and swapping in `AutoCompleteBox` is
a known refinement, not a correctness problem.

**The Windows PE page's destructive option is not its default, deliberately.** Under
`WindowsPeMode.Generate` the generator replaces `setup.exe` with a .cmd script that runs diskpart and
dism, which wipes the target disk with no confirmation. That is exactly right for the blank virtual
disk the wizard has just created and exactly wrong for a VM later pointed at existing storage, and the
same window is reachable from both, so the page keeps upstream's `DefaultPESettings` and spells the
wipe out beside the opt-in. Two things follow from that page. The **Windows 11 requirements bypass**
is a property of `setup.exe`'s own check, so upstream models it as part of `DefaultPESettings` and it
ceases to exist once Setup is replaced; VirtDeck keeps the checkbox on the **Setup** page, where
somebody installing Windows 11 in a VM without a vTPM looks for it, and `UnattendConfigMapper.PESettings`
puts the two halves back together rather than the two pages ordering their `Apply` calls. And the
generated disk assertions **deviate from upstream's record defaults on purpose**: the 100/4000 GiB
size bounds exist to stop a script wiping somebody's external drive, and a virtual disk is routinely
smaller than the lower one, so leaving them on would halt Setup on a machine that is perfectly fine.
The empty-disk check is on instead, and the page says why. This does not touch the byte-identity
tripwire below, which only ever exercises `DefaultPESettings`.

**How the machine gets its first account is one radio group of three, not a table plus two
checkboxes** (`AccountCreationMode`): OOBE asks for a Microsoft account (the default), or OOBE asks
for a local one, or the answer file names the accounts itself. This is one place VirtDeck's UI is a
better fit than the web form's checkbox pair, and the library agrees: `IAccountSettings` has exactly
these three implementations, so the mapper is a three-way switch. The accounts table, the nested
**First logon** box and the Base64 checkbox all hang off one panel under the third option and grey out
together. The generator ignores the table outright in the other two modes, but the UI keeps what was
typed there, so flipping between options does not empty it.

A row's group defaults to **Users**, and the generator **refuses** a table with no administrator in it
unless the built-in Administrator is the one being activated: a machine whose only accounts are
standard users is unmanageable. `NoAdminNote` therefore sits with the table, in
`JbErrorForeground`, and says the rule rather than warning about one option's consequences.
`LocalAccountRow` raises change notification (the only row view model in the app that is written to
rather than rendered from) so the note can appear as the group column is edited.

**The answer file reaches Setup on a second CD-ROM, not inside the install ISO.** Windows Setup runs
an implicit answer-file search at the start of every configuration pass, and one entry in it is the
root of removable read-only media, so a separate disc is consumed with no command line and no
keystroke. Editing the install ISO is not an alternative: a retail Windows image serves its real tree
from **UDF**, and the ISO 9660 side of a 25H2 image holds three entries, so a file added there is
invisible to Setup. `Services/Iso9660Builder` therefore writes a small single-file image, 25 fixed
sectors plus the file itself, so around 57 KB for a default answer file (Level 2 for
the 18-character `AUTOUNATTEND.XML;1`, plus Joliet because `mkisofs -J`/`oscdimg` is what every
answer disc in the wild is made with, and **no El Torito record** so the firmware falls through it to
the install medium). It is the inverse of `IsoIdentifier`, which parses the same descriptors.

**The disc is written to the host, never streamed over NBD.** An unattended install spans several
reboots, and libvirt refuses `startupPolicy` on network sources (see `AttachNetworkCdrom`), so a disc
that died with the VirtDeck session would leave a domain that will not start at all. `VirshService.WriteFile`
uploads it with the same base64 idiom as the rest of the host file access, chunked because the payload
rides on a command line. It lands at `/var/lib/libvirt/images/<vm>-unattend.iso`; `UnattendMedia` owns
that naming rule, and `VmListWindow`'s delete path uses `UnattendMedia.IsAnswerIso` to offer it for
deletion alongside the disk images. It is the one CD-ROM `DeleteVmDialog` will delete, because it is
the one VirtDeck generated.

**The model is flat and mutable; the library's is neither, and `Unattend/UnattendConfigMapper` is the
only place they meet.** `Configuration` is an immutable eighty-parameter record whose settings groups
are sum types, and VirtDeck cannot hold one directly for four independent reasons: a sum type has
nowhere to keep the value typed under the option the user is not currently on, which is the rule the
accounts table already lives by; the settings records validate in their constructors, and a model
edited keystroke by keystroke has to be allowed to be invalid; `System.Text.Json` cannot round-trip
interface-typed members without discriminators, and adding them would mean editing vendored source;
and building one needs a live `UnattendGenerator`, which `CreateVmWizard._unattend` must not drag
around. So the model is flat, and assembling and validating both happen once, at generate time.

Two typing rules follow, and the line between them is **open set or closed set**, not what the
library happens to have called it.

An **open** set is stored as **ids**: a lookup-table entry (locale, keyboard, geo, edition, time
zone, bloatware, desktop icon, start folder, component) keeps its `IKeyed.Id` string, and so does an
enum nobody renders as a fixed row of controls. `ComponentXml.Pass` is the clearest case of the
second: which passes are valid is per-component data the catalog answers (`UnattendCatalog.PassesOf`),
not something VirtDeck reasons about, and `specialize` is exactly the word the generated XML uses, so
the picker shows the name and the model stores it. A preset then stays readable, and an entry that
upstream drops degrades to "skipped" instead of a load failure. Never hand an unchecked id to
`Lookup<T>`, which throws about internal types: check the table first. Where the id is one required
value rather than a member of a set, an unknown one is an **error naming it**, not a quiet fallback,
because substituting a different answer for the user's is worse than refusing (see
`UnattendConfigMapper.TimeZoneSettings`).

Where an id is one **required** value rather than a member of a set, an unknown one is refused by
name; where it is a member of a set, it is dropped. `UnattendConfigMapper.Lookup` and
`UnattendConfigMapper.Known` are the two halves of that, and the difference is that losing one
bloatware removal from a preset is recoverable while silently installing a different locale is not.

A **closed** set gets a VirtDeck type mirroring it: an enum named `<Thing>Mode` for a radio group,
plain bools for a fixed row of check boxes. That covers both the choice of *which* implementation of
an `IXxxSettings` interface is selected, which has no upstream name at all (`AccountCreationMode`,
`FirstLogonMode`, `LockoutMode`), and small upstream enums that do (`ExpressMode`, `HideFilesMode`,
`LockKeyState`, `LockKeyAction`, `StickyKeysMode`). Both are the same shape to the user, a handful of
mutually exclusive options, and it is an implementation detail of the library that one is an
interface and the other an enum, so the model should not make the UI care. Mirroring also puts the
breakage in the right place: if upstream renames a member, the **mapper** stops compiling, which is
found immediately, while presets keep loading because they hold VirtDeck's name. Where a name would
collide across the two namespaces the mapper imports, VirtDeck's is the one that changes
(`ExpressMode` for `ExpressSettingsMode`, `LockKeyState` for `LockKeyInitial`, `ScriptStage` and
`ScriptKind` for `ScriptPhase` and `ScriptType`). `ScriptStage`/`ScriptKind` are mirrored despite being
per-row dropdowns rather than a radio group, because the rule tying them together is VirtDeck's to
express in the UI and magic strings would be the alternative.

**Anything not yet exposed keeps `Configuration.Default`'s value**, so the answer file only ever
differs from the reference generator's by what the user actually set. There is one deliberate
exception, and it is the tripwire the update procedure checks: the accounts page defaults password
expiry to **Never** where upstream leaves Windows' 42 days. Neutralise that one setting and VirtDeck's
default output is byte-identical to `Configuration.Default`'s.

**An invalid config is now reachable, so OK validates.** The hand-written writer this replaced could
not fail, so no caller had a failure path; the library validates its input in the settings
constructors and its output against the unattend schema, and either can say no (no administrator
account, a reserved user name, a lockout window longer than the lockout duration). `UnattendXml.Build`
is the boundary: it is the only thing above Core that touches the generator, and the only exception
that escapes it is `UnattendBuildException`, carrying the library's own wording, which is written for
humans. The window builds once on OK, off the UI thread, and **stays open** with a dialog on failure,
because the alternative is a wizard that defines the VM, creates its disks, and only then reports that
the answer disc could not be written. `CreateVm`'s `Try("Answer disc", ...)` stays as the backstop.

Two constraints are resolved in the mapper rather than by ordering `Apply` calls. The library refuses
`KeepSensitiveFiles = false` together with "do not logon", because deleting the answer file is a
first-logon command and there would be nobody to run it; `UnattendConfigMapper.KeepSensitiveFiles`
keeps the files in that combination instead of letting the accounts page produce a config that cannot
generate. And it refuses disk assertions together with hand partitioning, because the checks exist to
decide whether the script may go ahead unattended and there is nothing to decide with somebody at the
console; `DiskAssertionSettings` drops them, and the page greys the group out with the reason so OK is
not where the user first hears it. Note also that the library runs the account policies in the
**specialize** pass, not in `FirstLogonCommands`, so unlike the old writer they apply even under "do
not logon".

What the mapper deliberately does **not** resolve is raw `windowsPE` markup on the Advanced page
combined with a generated or custom PE script: that script replaces Windows Setup and with it
everything the answer file would have told Setup to do, so upstream refuses the pair by name and its
message points at `$WinPEDriver$` and `drvload.exe`. Dropping either side would be silently discarding
what the user typed, so the refusal is passed through and both pages say so.

**The three file buttons are Load preset / Save preset / Export XML, and they are not one pair over
one format.** All three exchange a file with *this PC* through `FileDialogs`, never through
`RemotePathBox`: an install ISO lives on the server because the VM reads it, but this is authored here
and reaches the host only as the generated disc. **Export XML** writes exactly what the disc carries
(`UnattendXml.Build` of what the pages say, so OK and Export read the same config), and it is one-way.
**There is no XML import.** The online tool has one, but that is server-side code in its web front
end, not part of the library, and writing one here would mean maintaining a parser for everything
twenty-odd tabs can emit, against a library that changes. `Unattend/UnattendPreset` is the answer
instead: VirtDeck's own JSON, a full-fidelity round trip, and it can hold what the answer file cannot,
namely the values sitting under the radio options the user is not on. It writes enums **by name**,
unlike `AppSettings`, which writes them numerically so a bad settings file can never stop the app
launching; a preset failure is reportable in a dialog and the file is one somebody may hand-edit. It
also insists on a `Version` property, because deserializing straight away would accept any JSON object
and answer with pure defaults, which would look like a successful import that quietly blanked every
page. `UnattendConfig.Clone()` goes through the same serialiser: one implementation that cannot forget
a field, and it self-tests, since anything Clone loses Save and Load lose too.

**The button is disabled with a reason, never hidden**: not Windows, or an OS that predates
`autounattend.xml` (XP and earlier are scripted with `winnt.sif`, a different file in a different
format). Windows-ness comes from `GuestProfile()`, so the OS dropdown stays the source of truth and
detection only fills in while it says generic, exactly as the device defaults do. Anything already
configured **survives an OS change** and is simply not written, which the status line beside the
button says out loud; discarding the user's work on a dropdown change would be worse than carrying it.

## USB redirection

The console can redirect a physical USB device on the client into the guest (the SPICE
**usbredir** channel, type 9), on **Windows and Linux**. It is independent of the guest agent; the
guest only needs a USB controller + the device's normal driver.

- **Native stack (not a C# port):** `SpiceClient` P/Invokes `usbredirhost` + `usbredirparser` +
  `libusb-1.0`, loaded by bare name through `Interop/NativeLibraryResolver`. On Windows the x64
  DLLs must sit next to `virtdeck.exe`; stage them in `native\win-x64\` (see its `VERSIONS.txt`);
  the csproj copies them to output. On Linux they come from the distro (`libusb-1.0.so.0`,
  `libusbredirhost.so.1`), so there is nothing to stage.
- **Never link a channel you can't drive.** `Usb/UsbSupport.IsAvailable` gates channel creation in
  `SpiceSession.CreateUsbChannel`: with libusb or usbredirhost missing, the usbredir channels are
  **not connected at all**. A channel that links and then never speaks usb_redir gets the *whole*
  SPICE connection closed by the server about a second later; the console reconnects, links it
  again, and dies again, which presents as a **flickering console** on every VM that has
  `<redirdev>` elements. The picker and the console's USB entry check the same helper, so
  provisioning never adds redirdevs a client can't use.
- **Channel:** `Channels/UsbredirChannel` is a spicevmc tunnel; it shuttles opaque
  `MSG/MSGC_SPICEVMC_DATA` (101) bytes, which are the raw usbredir wire protocol. On link it creates
  one persistent `Usb/UsbredirHostInstance` (a managed wrapper over `usbredirhost`) with **no device**
  so the usb_redir hello negotiates immediately; a device is attached/detached later via
  `usbredirhost_set_device` (no second hello). One device per channel.
- **usbredirhost owns the device handle**: `set_device(NULL)`/`close` call `libusb_close` themselves;
  never close a handed-off handle from managed code. Callback delegates are rooted for the host
  lifetime (collected-delegate crash otherwise). Disposal order is strict: close hosts (channels)
  **before** `libusb_exit` (`Usb/LibUsbContext`, one shared context + one event thread).
- **Manager/UI:** `Usb/UsbDeviceManager` (on `SpiceSession.Usb`, created when the host advertises a
  usbredir channel) enumerates devices, filters out hubs only (HID devices are redirectable and
  are listed), and binds a device to a free channel.
  `Enumerate` labels each device from `Usb/UsbNameTable`: sysfs `manufacturer`/`product` keyed by
  bus+devnum on Linux, SetupAPI keyed by VID:PID on Windows. Both read what the OS cached at
  enumeration: the picker must never *open* a device just to name it. The picker is
  `VirtDeck.Avalonia/Views/UsbDeviceDialog`, opened from the console's **USB** toolbar entry.
- **Mass storage** (`Usb/UsbStoragePrep` + the `WindowsUsbStorage`/`LinuxUsbStorage` halves): the
  picker takes the device's filesystems offline before binding, for a different reason per platform:
  UsbDk captures with a USB *reset* that blocks/fails (libusb `LIBUSB_ERROR_OTHER`) while a volume is
  mounted, and libusb on Linux detaches `usb-storage` out from under a mounted filesystem. Windows
  maps VID/PID → drive letters (SetupAPI + cfgmgr32 parent-walk) and `FSCTL_LOCK_VOLUME` +
  `FSCTL_DISMOUNT_VOLUME`, holding the lock until capture; Linux walks the device's own sysfs subtree
  to `…/block/sdX`, matches `/proc/self/mounts`, and unmounts via `udisksctl` (same service as the
  udisks2 D-Bus API, so desktop-mounted media needs no polkit prompt) falling back to `umount`.
  `Complete()` on a successful bind means "the device left this PC, don't restore"; disposing without
  it puts the volumes back. spice-gtk/virt-viewer skip this step entirely. A volume that can't be
  taken offline (open files) is reported and the redirect is not attempted.
- **Device permissions (Linux):** libusb needs rw on `/dev/bus/usb/…`. `packaging/70-virtdeck-usb.rules`
  (`TAG+="uaccess"`) grants it to the seat user; without it `libusb_open` returns
  `LIBUSB_ERROR_ACCESS` and `UsbDeviceManager.DescribeOpenFailure` tells the user to install the rule
  and replug.
- **Host provisioning:** `Services/UsbProvisioning` auto-ensures the domain has a USB controller +
  4 `<redirdev type='spicevmc'>` channels. redirdevs hot-plug (`attach-device --live --config`) when a
  controller exists (then the console reconnects to see them); adding a controller is persistent-only
  and needs a power-cycle.
- **Capture backend:** on Windows the **UsbDk** kernel driver is required (installed by `installer\`,
  Inno Setup) and requested via `LIBUSB_OPTION_USE_USBDK`; on Linux libusb's native backend captures
  directly, so the option is **not** set there (it would fail and silently disable redirection) and
  `libusb_set_auto_detach_kernel_driver` kicks the in-kernel driver off the interface instead.
  Without the backend (or the libraries), channels still link but redirection is dormant and the
  picker says why. Reliability: bulk/HID/mass-storage solid over the tunnel; isochronous
  (webcams/audio) is a known weak spot on both platforms. Pin a known-good `libusb-1.0.dll`
  (virt-viewer 10.x; v11's regressed redirection).

## Conventions

- All wire structs follow the spice-html5 source byte-for-byte; when changing protocol code, check the matching `*.js` in `..\VmManager\VmManager\WebContent\src`.
- Pixels are BGRA end-to-end; do NOT copy spice-html5's BGRA→RGBA canvas swap.
- UI updates from channel threads go through `Dispatcher.UIThread`; framebuffer access is under `SpiceFramebuffer.SyncRoot`.
- Do not add a local `FontSize`/`Height` in a view to line controls up; fix the baseline in `Styles/JetBrainsClassic.axaml` instead (see above).
