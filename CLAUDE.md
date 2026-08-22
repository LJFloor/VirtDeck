# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Build

```bash
dotnet build VirtDeck.sln            # everywhere: every project is net10.0 now
```

Four projects (.NET 10), all `net10.0` and all buildable on either OS:
- **SpiceClient**: cross-platform SPICE protocol client library. No UI toolkit: it produces raw BGRA buffers. Uses SkiaSharp for JPEG decode and Concentus for Opus.
- **VirtDeck.Core**: cross-platform services and models (`Services/`, `Models/`, `Unattend/`, `Diagnostics/`, `Secrets/`, `Imaging/PpmImage`). SSH.NET + `virsh` over SSH; no UI dependency. Namespaces are `VirtDeck.*`.
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

`.github/workflows/publish.yml` builds both on every push to `main`; `release.yml` does the same on a `v*` tag and attaches both to a GitHub Release (the release job needs *both* jobs green; a half-published release looks finished but isn't). **Both set `retention-days`, and the repo being private is why**: an artifact here counts against the account's Actions storage allowance, one push to `main` stores about 107 MB the pair, and once that allowance is full *every* upload in the repo fails, so a green build still reports as a failed run. `publish.yml` keeps a week, because there its artifacts are the deliverable; `release.yml` keeps one day, because there they are only a hand-off to the release job and the permanent copy is the one attached to the Release, which is separate storage. `packaging/icons/make-icons.py` is the single source for the app icon: it rasterises the hicolor PNGs, the scalable SVG and the Windows `.ico` from one set of numbers, so no build machine needs an SVG renderer.

## What this is

An app to manage a remote Linux host over SSH. Its first and largest subject is libvirt/KVM VMs, with a **native C# SPICE console** (no WebView2, no spice-html5, no WebSocket bridge; raw TCP to the SSH-forwarded SPICE port); Docker containers are the second. One SSH connection serves all of it, and the main window is a side menu of **modules** over that connection (see "Modules" below).

**Cross-platform status:** done. Engine, services, the whole management UI, guest audio and USB redirection run on Linux and Windows from the single Avalonia front-end. The WinForms app that this was ported from is deleted (it is in git history if a WinForms detail ever needs checking).

It is a fresh rewrite of the older `..\VmManager` app, which rendered SPICE via spice-html5 in WebView2 and broke with "Protocol Error" (the WebSocket↔TCP bridge mangled SPICE's binary framing). The native client deletes that failure mode. The SPICE protocol was ported field-for-field from the bundled spice-html5 source at `..\VmManager\VmManager\WebContent\src\*.js`; that JS is the authoritative wire-format reference.

**Flow:** `LoginWindow` → SSH connect → `MainWindow` (module side menu) → Virtual machines → double-click VM → `ConsoleWindow` (native SPICE).

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
- `Services/`: `SshConnectionManager`, `SshPortForwarder`, `VirshService` (sudo via stdin + marker), `DockerService`, `NbdServer`, `UsbProvisioning`, `OsLabelCatalog`. Image compression is steered at the protocol level (see below), not via `virt-xml`.
- **Interactive commands** go through `SshConnectionManager.OpenSudoPtyAsync` + `Services/SshPtySession`,
  which is the only bidirectional channel in the app; see "Running a command in a container" below.
- **SSH authentication**: `SshConnectionManager.ConnectWithPassword` / `ConnectWithKey` (host + **port**, no longer hardwired to 22). Both build a plain `ConnectionInfo` (the base class is not `IDisposable`, unlike `PasswordConnectionInfo`), so the manager owns the credential lifetime: the auth method and, for key auth, the `PrivateKeyFile` are **fields disposed after the client**, never `using`-scoped in the connect call, because `DownloadFileAsync` and `RunSudoCommandStreaming` each open a second client from `_client.ConnectionInfo` and would otherwise authenticate with disposed key material.
  **The sudo password is a separate secret** (`_sudoPassword`): with key auth there is no login password, and a NOPASSWD account has no sudo password either, which `FeedSudoPassword` already tolerated (sudo simply never reads stdin). `CheckSudo()` runs `sudo -S -p '' true` once from the login window and returns a message instead of throwing, so a wrong or missing sudo password is reported on the login screen rather than as a VM list that fails to load.
  `Services/SshKeyDiscovery` scans `~/.ssh` (`%USERPROFILE%\.ssh` on Windows) for the key dropdown: a file counts as a key when it has a `.pub` sibling **or** its first line says `PRIVATE KEY`, stock names sorted strongest first. `NeedsPassphrase` probes with `new PrivateKeyFile(path)` and treats only `SshPassPhraseNullOrEmptyException` as "encrypted"; anything unreadable answers false on purpose, so the connect attempt reports the real error instead of the UI asking for a passphrase that would not help. SSH.NET 2024.2.0 has **no ssh-agent support**, so keys are always read from disk. `settings.json` persists the mode, key path and port and **never a secret**; the passwords themselves go to the OS secret store when the user opts in, see "Remembering passwords" below.
- `Services/AppSettings`: JSON settings at `%APPDATA%`/`~/.config` + `VirtDeck/settings.json`. **Replaces the old `HKCU\SOFTWARE\VirtDeck` registry storage**; `LegacyRegistryImport` migrates it once on Windows.
- `Secrets/` + `Services/SshCredentialStore`: the OS secret store. See "Remembering passwords" below.
- `Imaging/PpmImage`: `virsh screenshot` P6 decoder → raw BGRA.
- `Unattend/`: the answer-file boundary, and the only code that references the vendored generator. `UnattendConfig` is the flat serialisable model (one property per tab group, each declared in its own file under `Unattend/Model/` and named after the tab that edits it), `UnattendConfigMapper` projects it onto the library's `Configuration`, `UnattendXml.Build` is the single entry point (throws only `UnattendBuildException`), `UnattendCatalog` owns the one `UnattendGenerator` instance and exposes its lookup tables as `UnattendOption` lists, `UnattendPreset` is the JSON save/load, and `UnattendMedia` wraps the result in the answer disc. See "Unattended Windows setup" below.

### VirtDeck.Avalonia (cross-platform UI)
- `Controls/SpiceDisplay`: Avalonia `Control`. A 16 ms pump copies only the **dirty rows** from `SpiceFramebuffer` into a `WriteableBitmap` under `SyncRoot`, then `InvalidateVisual`; `Render` does a 1:1 `DrawImage` with interpolation `None`. Owns the exactly-one-cursor state machine, built from `Cursor(Bitmap, PixelPoint)` and `StandardCursorType.None`.
- `Styles/JetBrainsClassic.axaml`: JetBrains Classic UI (Darcula / IntelliJ Light) trim, and the app's single source of visual scale. Replacement `ControlTheme`s for `Button`/`JbToolButton`, `CheckBox`, `RadioButton`, `TabControl`/`TabItem` (top-placed; `JbSideTabControl`/`JbSideTabItem` are the left-placed pair, keyed, for the Windows-setup customization window, and `JbModuleTabControl`/`JbModuleTabItem` derive from that pair with a taller row for the main window's module side menu), `JbTableRow`, and the menu family (`Menu`/`JbTopLevelMenuItem` for the console toolbar strip, `MenuItem`/`Separator`/`ContextMenu`/`MenuFlyoutPresenter` for the popups; the `MenuItem` template carries a lane for `InputGesture`, because a retemplate that drops Fluent's gesture column makes that property draw nothing anywhere in the app, including in the cut/copy/paste flyout a `TextBox` brings with it, and the alternative is every view spelling "Ctrl+X" into its own `Header`), plus overrides of Fluent's metric keys (`TextControl*`, `ComboBox*`, `ListBoxItemPadding`, `ToolTip*`). `JbErrorForeground` is the brush for inline validation text (a message about something the user must change, never an ordinary hint; those are 0.7 opacity), themed for both light and dark, so no view hardcodes a red. `JbGroupBox` is a `ControlTheme` for `HeaderedContentControl` carrying the app's titled-frame idiom, with its content inset templated from `Padding` (12,10 by default) so a box whose whole content is a table can ask for `Padding="0"` and let the rows run to the frame instead of drawing a second border a few pixels inside the first, and `JbGroupBoxBorder`/`JbGroupBoxHeader` are its two brushes, deliberately outside the theme dictionaries because both are grey with an alpha channel and so read correctly on either face; `JbIconAdd`/`JbIconRemove` sit beside them, outside for the related reason that a green meaning "add" must not change meaning with the theme. Merged into `Application.Resources`, **not** `Application.Styles`, because resource lookup reaches `Application.Resources` before the `FluentTheme`'s own dictionaries; that is what makes those overrides win without `/template/` selectors per state.
  **One font baseline: 12.** A bare `TextBlock` defaults to 12 while every Fluent `ControlTheme` sets its own size from `ControlContentThemeFontSize` (14), so labels and the fields beside them rendered two points apart; each retemplated control here drops that setter and falls back to the inherited 12, which widened the gap further. The dictionary pulls the key down to 12, and every metric in it (24px fields and buttons, 22px tool buttons, a 14px check/radio box, `12,4` table rows) is sized for that. **Fields and buttons share one height (24)** (same 1px border, same 3px vertical inset), so a text box, dropdown or spinner lines up with the button next to it; `App.axaml` pins `ButtonSpinner`/`NumericUpDown` to the same key because Fluent sizes those outside `TextControl*`. Views should not set a local `FontSize` or `Height` to line controls up; fix the baseline instead. Headings (wizard title 15, login 16) are the deliberate exceptions.
- `Controls/TerminalControl` + `VirtDeck.Core/Terminal/`: the container console's terminal, split the
  same way SPICE is. `TerminalScreen` (cell grid, scrollback, alt screen, all under `SyncRoot`) and
  `TerminalParser` (UTF-8 + the escape-sequence state machine) are toolkit-agnostic and live in Core,
  exactly as `SpiceFramebuffer` does; the control draws them and turns keys into bytes. It knows
  nothing about SSH or docker: bytes in through `Receive`, bytes out through `Input`.
- `Input/TerminalKeyMap`: Avalonia `Key` to the bytes an xterm sends. The **logical** key, not the
  physical one, which is the opposite of `PhysicalKeyMap` beside it: a guest OS applies its own
  layout to a scancode, so there the position is the truth, while here the far end wants characters
  and the layout has already been applied.
- `Input/PhysicalKeyMap`: Avalonia `PhysicalKey` → AT set-1 scancode. `PhysicalKey` is positional (W3C `code`), so it is layout-independent, more correct than a VK table, which reads through the host layout. **Extended keys are `0xE0 | (atCode << 8)`** (e.g. PageUp = `0x49E0`), matching spice-html5 utils.js, NOT `0xE0XX`. The key-up high bit is applied in `InputsChannel.SendKey`.
- `Views/`: `LoginWindow`, `MainWindow` (the shell; its modules are `VirtualMachinesModule` + `VmDetailsView` and `ContainersModule`), `ConsoleWindow`, `CreateVmWizard`, `VmEditWindow`, `ExportVmDialog`, `RemoteFileBrowserDialog`, `UsbDeviceDialog`, `Unattend/UnattendWindow` and `Containers/ContainerEditWindow` (each + its per-tab `UserControl`s), `Containers/ContainerLogsWindow`, `Containers/ContainerConsoleDialog` + `Containers/ContainerConsoleWindow`, `TerminalModule`, the small device dialogs, and `MessageDialog` (Avalonia has no `MessageBox`). Row view-models live beside their window or module (`VmRow`, `NetworkRow`, `ContainerRow`, `DiskEditRow`, `UsbDeviceRow`, `Containers/ContainerEditRows`, …); lists are `ListBox` + `DataTemplate`, not `ListView`.
- `Services/FileDialogs`: the one place the WinForms filter string (`"ISO images (*.iso)|*.iso"`) is translated, into `IStorageProvider` picker types (XDG portal on Linux). Only local paths are accepted; everything downstream needs a real `FileStream`.
- `Services/DropFiles`: the one place the drag-and-drop dialect is translated, the same way `FileDialogs` handles the filter string. Avalonia 12 replaced `IDataObject`/`DataFormats.FileDrop` with `IDataTransfer`/`DataFormat.File`, so Avalonia 11 snippets do not apply; `LocalFiles` filters a drop down to real local files (directories and portal handles are dropped) through the same `FileDialogs.LocalPathOf` the pickers use. **X11 drag-and-drop needs Avalonia 12.1+**: 12.0.x has no XDND in its X11 backend at all and raises no drop events on Linux, which is why the csproj pins 12.1 as the floor.
  Drop targets: the console window (below), the Create-VM wizard's General page (an ISO or floppy image fills the install media and switches to "stream from this PC"), a removable-drive row in `VmEditWindow` (staged as that drive's media; the extension must match the drive kind), and `LoginWindow`'s key panel (adds and selects a private key). Not `RemotePathBox`: its path is on the *server*, so a local path there would be meaningless.
- Console drops: any file is sent into the guest with `SpiceSession.SendFile` (the vdagent file-transfer channel; the guest agent picks where it lands), and the status bar shows one shared progress bar and Cancel for the whole drop, refcounted in `_activeXfers` and driven **only** by the session's file events, because `SendFile` returns silently for a missing file or a departed agent. A single ISO or floppy image dropped on a VM that has the matching drive **always asks** (`MessageDialog.Choose`) whether to insert it or send it; with no guest agent the send button is disabled with a reason rather than the drop silently mounting. Nothing is ever mounted unconfirmed, and a shut-off VM is a valid drop target (the drive targets are detected on the powered-off paths too, and the media change goes straight to the saved config).
- `Controls/RemotePathBox`: textbox + "…" opening `RemoteFileBrowserDialog`. That browser draws the
  host desktop's own file-type icons; see "Remote file browser icons" below.
- `Input/AsciiScancodes`: char → AT set-1 scancode + shift, for "Type clipboard". Replaces `VkKeyScan`, which read the *host* layout; scancodes are positional, so the guest's layout decides; a fixed US table is exactly as correct and equally approximate elsewhere.
- Clipboard: **text and images, both ways**; see "Clipboard sharing" below for the whole story. `Services/HostClipboard` is the one place the Avalonia clipboard dialect is translated, the way `DropFiles` is for drag-and-drop.
- `Input/IKeyboardGrab`: `X11KeyboardGrab` (`XGrabKeyboard`) on Linux so Alt+Tab/Super reach the guest; no-op elsewhere. **Best-effort by design**; the console must work without it. The grab **must** be issued on Avalonia's own X display connection (dug out of `Window.PlatformImpl` by reflection): X reports key events during an active grab only to the grabbing *client*, and a client is a connection; a grab on a private `XOpenDisplay` takes every key away from Avalonia and the console goes deaf while grabbed. If the display can't be resolved, report unsupported (no grab) rather than falling back to a private connection.

### Modules

The main window is a **shell**, not a screen. `Views/MainWindow` owns the SSH connection, the status
bar and the process lifetime; everything a user actually manages lives in an `IModule`, and the side
menu is a `TabControl` themed with `JbModuleTabControl` (the settings-window side strip with a taller
row, so a 16px glyph fits beside the label). Modules today: **Virtual machines** (`VirtualMachinesModule`,
which is the whole former `VmListWindow` minus the shell: the VM list, the Networks tab, `VmDetailsView`
and every per-VM command), **Containers** (`ContainersModule`, below), **User accounts**
(`UserAccountsModule`, below: the host's own logins and groups), **File explorer**
(`FileExplorerModule`, below: the host's filesystem as the login user) and **Terminal**
(`TerminalModule`, below: a shell on the host itself).

**The shell never names its modules.** It walks its `TabControl`'s items and calls `IModule` on
whichever contents implement it, exactly as `UnattendWindow` does for its section pages, so adding a
module is a `TabItem` in `MainWindow.axaml` plus one `UserControl` and no line to maintain anywhere
else. Two invariants follow, the same two the answer-file window lives by: every module is
constructed with the shell (they are literal elements, not template output), so a module the user
never opened must still behave; and modules own disjoint state, so activation order never matters.

- **`Attach(ssh)` rather than a constructor parameter.** A `UserControl` declared in XAML needs a
  parameterless constructor, and the modules are built before the connection exists. So the shell
  hands each of them the same `SshConnectionManager` once, before the first `ActivateAsync`, and a
  module builds its own service (`VirshService`, `DockerService`) on top of it. The connection stays
  the shell's to dispose.
- **The status bar is two slots, and a module owns both while it is on screen.** `Status` is the left
  slot (row counts, "Starting win11 (1/2)..."), `HostCapabilities` the right ("KVM ready",
  "docker 27.3.1", "docker not installed"), and one `StatusChanged` event covers them. The shell
  repaints from the *incoming* module's own strings on every switch, because the outgoing module's
  counts say nothing about what is now visible, and it ignores a raise from a module that is not
  active. Throughput is the strip's third element and is the **shell's**: its 1 s timer never stops,
  because `SpiceTraffic.BytesTransferred` and `NbdServer.TotalBytesServed` keep climbing from
  consoles and media streams whichever module is on screen.
- **`Deactivate` exists so a hidden module costs nothing.** It stops every timer and makes the
  module ignore host lifecycle events; `ActivateAsync` refreshes immediately, so nothing is stale by
  the time it is visible. The **event tails themselves keep running**, though (`virsh event --loop`,
  `docker events`): each holds its own SSH connection, and reconnecting one on every module switch
  would cost far more than dropping the events. Host capabilities are probed on the first activation
  only, except where the answer was "not installed" and re-probing is the user's only way forward
  (see Containers). **Terminal takes the same exception further and does nothing at all here**,
  because its session is the feature; see below.
- **`Shutdown` runs on every module, not just the visible one.** A hidden module still owns the
  console windows and NBD media streams it opened while it was on screen, and those outlive a module
  switch by design.

### Remote file browser icons

`RemoteFileBrowserDialog` shows the **host desktop's** icon for each type, through
`Services/FileIcons` and its two halves (`WindowsFileIcons`, `LinuxFileIcons` + `IconThemeIndex`),
the same one-place-per-dialect shape as `FileDialogs` and `DropFiles`. **Everything is keyed by file
name only and nothing is ever opened**, which is what makes it usable at all: the files being listed
are on the *server*. So the icon is the client's opinion of the extension, which is the useful one,
because the person reading the list is at the client, and it is what every SFTP client shows.

- **Windows: the registered application associations.** `SHGetFileInfoW` with
  `SHGFI_USEFILEATTRIBUTES` asks about a *type*, so `".iso"` is a valid argument and the path is
  parsed rather than touched. It resolves through the same layer Explorer uses, so the user's own
  default-program choices show through, and an unregistered extension gets the shell's blank page
  instead of nothing. `FILE_ATTRIBUTE_DIRECTORY` gets the folder icon. Turning the HICON into pixels
  is the fiddly half: `GetIconInfo` + `GetDIBits` reads the colour bitmap as top-down 32bpp BGRA with
  **straight (non-premultiplied) alpha**, the same assumption `Icon.ToBitmap` makes, and where that
  bitmap is under 32bpp or comes back fully transparent the 1bpp **mask** is what decides which
  pixels are drawn (a pre-XP icon has no alpha channel at all).
- **Linux: shared-mime-info plus the current icon theme.** `mime/globs2` gives the MIME type, then
  `mime/icons`, the type with its slash dashed (`application/x-qemu-disk` to
  `application-x-qemu-disk`), `mime/generic-icons`, and the same three for every parent in
  `mime/subclasses`. **Aliases are tried too, which GIO does not do**, for one concrete reason:
  shared-mime-info 2.x renamed `application/x-cd-image` to `application/vnd.efi.iso` and kept the old
  name as an alias, while the themes still ship the art under `application-x-cd-image`, so skipping
  aliases shows a bare disc where the theme has a drawn ISO. `IconThemeIndex` then does the Icon
  Theme Specification's real lookup (`index.theme`'s `Directories` with each one's size rules,
  through the whole `Inherits` chain, hicolor last). **Names are searched before themes**, GTK's
  order rather than the spec pseudo-code's, so the theme the user picked wins with a generic icon
  before its parent wins with a specific one. `@2x` subdirectories are skipped because a HiDPI row is
  served by asking for a larger pixel size instead, and a directory is read once into a name set
  because a miss would otherwise cost two `stat`s in each of ~450 subdirectories per name tried.
- **SVG is not optional.** Papirus, Breeze and Adwaita 46+ ship mimetype icons as SVG only, so
  `Svg.Skia` rasterises them at the size asked for. It needs SkiaSharp >= 3.119.2 and SpiceClient
  already pins 3.119.4, so it changed no version and added no native library. It is dead weight in
  the Windows artifact (3.3 MB of managed DLLs), which beats a RID-conditional compile in a solution
  whose rule is that every project builds on either OS.
- **The desktop is asked once per listing, not once per row.** `FileIcons.Available(size)` decides
  whether a whole listing gets icons or the old colour-coded three-letter badges, so the two never
  appear mixed and the Name column lines up either way; the badge table in `RemoteFileRow` survives
  as that fallback and nothing else. Icons are cached by (type, size) and **shared by every row, so
  no caller may dispose one**. First use reads the theme (and may shell out to `gsettings` for its
  name), and on an SVG theme each new type is a rasterisation, so `NavigateTo` warms every icon the
  listing needs inside the same `Task.Run` as the directory fetch and building the rows is then pure
  cache hits.
- **No VirtDeck-specific overrides.** The extension is handed to the OS as-is, including `.img`,
  which the badges special-case by floppy geometry. A listing shows what the client's file manager
  would show; the VM-aware reading of a file belongs where it changes behaviour (`FloppyImage`,
  `IsoIdentifier`), not in the decoration.

### Remembering passwords

The login screen's three secrets (SSH login password, key passphrase, sudo password) can go to the
OS store behind one **Remember passwords** checkbox: the freedesktop Secret Service on Linux
(gnome-keyring, KWallet, KeePassXC) through `libsecret-1.so.0`, Credential Manager on Windows
through advapi32. `Secrets/ISecretStore` + `SecretStores.Create` is the platform seam, the same
interface-plus-factory-plus-null-fallback shape as `AudioSinks.Create`, and `Services/SshCredentialStore`
is the only thing above it that knows what an SSH secret is. **`settings.json` still never holds a
secret**; it holds `RememberPasswords`, and that flag existing is what lets a user who never opted in
avoid ever seeing a keyring unlock prompt, because nothing reads the store at startup when it is false.

- **Nothing in `Secrets/` throws.** Every path catches and answers falsy, because `Program.Main`
  appends `ex.ToString()` to the crash log and a store that reports failures by throwing is one bad
  message away from writing a password to a file. Same reason `Load` answers `""` rather than null:
  no caller has to tell "no entry" from "could not read".
- **`Save` is clean-slate, not upsert**: forget everything, then write what the form holds. That is
  correct because `AppSettings` remembers exactly one host, one username, one key path, so the store
  *is* the state of that one form, and it makes staleness impossible with no compare-with-previous
  code (host, port, username, key, or a switch between password and key auth all leave nothing
  behind). **An empty secret is never stored**: "no entry" and "an entry that is empty" leave the same
  empty box, which is right for a NOPASSWD host, so the distinction would never be read.
- **The passphrase is keyed by key file path, the passwords by `user@host:port`.** A passphrase
  belongs to the file, so one key used against several hosts keeps one entry. Host lowercased (DNS is
  case-insensitive), username not (POSIX usernames are), key path lowercased on Windows only.
- **libsecret's `secret_password_*_sync` are C variadic** and cannot be called through `DllImport`;
  the `*v_sync` siblings take a `GHashTable` instead. `SecretSchema` is a 592-byte zeroed block whose
  head is blitted in, since C# cannot express its inline `[32]` array without `unsafe`. **Never pass a
  NULL schema**: a named one makes libsecret add `xdg:schema` to the attributes on store *and* search,
  and that is the only thing scoping our lookups and our purpose sweep to VirtDeck's own items.
- **Calls block** (a locked keyring waits on its unlock prompt), so every call site is `Task.Run` plus
  `Task.WhenAny(work, Task.Delay(SshCredentialStore.TimeoutMs))`, and one `Lock` serialises the store
  so two secrets queue behind one prompt. A *slow* failure latches the store off for the session:
  D-Bus spends 25 s activating a name nothing owns, and a clean-slate save would pay that six times.
- **Secrets are written only after `CheckSudo` passes**, so the store never holds a rejected
  credential, and recovery from one changed on the host is the existing error path plus retyping.
- Both stores protect at rest against another **user**, not against another process running as
  **you**: the Secret Service has no per-application isolation. Say that in the docs, not in the UI.

### Host capability checks

`VirshService` probes the host once, on the Virtual machines module's first activation (`VirtualMachinesModule.LoadHostCapabilitiesAsync`), and caches the results as properties. `DockerService` does the same for its own module; each module owns the right-hand status-bar slot while it is on screen.

| Property | Check | Used by |
|---|---|---|
| `VirtSparseAvailable` | `which virt-sparsify` | Export dialog: disables sparse checkbox |
| `CheckHostCapabilities()` | `/proc/cpuinfo` svm/vmx, `/dev/kvm`, `systemctl is-active libvirtd` | Status-bar indicators in `VirtualMachinesModule` |

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
  whether Finish calls `StartVmAsync`; `VmStarted` reports what happened, and `VirtualMachinesModule` opens the
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
that naming rule, and `VirtualMachinesModule`'s delete path uses `UnattendMedia.IsAnswerIso` to offer it for
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

## Containers

`ContainersModule` lists what `docker ps --all` reports on the host, creates and edits containers
through `Views/Containers/ContainerEditWindow`, can start, stop, restart and remove what it lists,
and can read one container's log in `Views/Containers/ContainerLogsWindow` or open a shell in one
in `Views/Containers/ContainerConsoleWindow`. Images, volumes and
networks as objects of their own are not here yet; the module seam and the create path are the point
of these passes, not feature parity with Portainer.

- **`Services/DockerService` is a sibling of `VirshService`, not a layer over it**, and is written in
  the same idioms: a `SshConnectionManager` in the constructor, a cached list plus a `ContainersChanged`
  event, `Task.Run` around the blocking SSH calls, queries that swallow and answer empty while
  mutators throw, and `SpiceLog.Log("[docker] ...")` for diagnostics.
- **Always `sudo docker`.** The login user may or may not be in the `docker` group, VirtDeck already
  holds a sudo password because `virsh` needs one, and `RunSudoCommand` is the same call either way.
  One privileged path beats probing for an unprivileged one and carrying both.
- **The listing is one round-trip, base64'd like `FetchAllVms`.** `docker ps --all --no-trunc --format`
  with a Go template is nothing but braces, dots and quotes, so it goes through
  `echo <b64> | base64 -d | bash` for the same reason the VM listing does. The separators are **real
  tab characters**, not docker's own `\t` escape, so nothing depends on how the CLI expands a format
  string. `{{.State}}` needs docker 20.10 or newer; on anything older the command fails outright and
  the message reaches the status bar, which beats a state column that quietly says nothing.
- **Uptime is ticked client-side, so the script has to fetch a start time `docker ps` will not give
  it.** `{{.Status}}` is a rendered phrase ("Up 3 hours"), not a number, so the same script adds one
  **batched** `docker inspect` over the running containers, never one per row: per-VM round-trips
  were the VM list's original latency problem and this is the same trap. It emits **elapsed seconds**
  rather than the RFC3339 timestamp, again matching the VM script, so host/client clock skew never
  enters the number; `ContainerInfo.StartedAtUtc` and `ContainerRow.TickUptime` are then copies of
  their `VmInfo`/`VmRow` counterparts. Two record kinds therefore come back, tagged `c` and `u` in
  the first field. The uptime half is **fenced off from the exit status**, so a host whose `date`
  cannot parse docker's timestamps still gets its list with the column blank, and docker's own
  phrase survives as the tooltip on the state cell, which is where the exit code of a stopped
  container still reads.
- **The list refreshes itself the way the VM list does**, so the toolbar holds only "New container"
  and there is still no Refresh button: a 30 s poll as the safety net, and `docker events` tailed on its own SSH connection as the fast path,
  debounced 400 ms because one `docker run` fires create, start and more. The filter list is not
  cosmetic: unfiltered, `type=container` also carries `exec_*` for every `docker exec` and a
  `health_status` per health-check interval per container, which on a host running health-checked
  containers would be a refresh treadmill. One deliberate difference from the libvirt tail beside it:
  **no `stdbuf -oL`**. That exists to stop virsh block-buffering through libc stdio, and the docker
  CLI is Go, whose `os.Stdout` is unbuffered; measured, `docker events` already arrives line by line
  through a pipe, so stdbuf would have nothing to act on.
- **Actions address a container by id, never by name.** A name is the user's own string and may hold
  anything; the id is checked against `^[0-9a-fA-F]{12,64}$` before it is interpolated into a command,
  the same guard `VirshService.DefineVmShell` applies to its untrusted tokens. The list also merges by
  id, so a refresh keeps the selection.
- **Absent docker is a stated answer, not an empty list.** `DockerAvailable` defaults **false** (unlike
  the KVM probes, which default available to avoid a false negative), because here the safe default is
  the one that explains itself: the status bar says "docker not installed", the list carries a centred
  message saying so, and every command is disabled rather than hidden. Nothing polls or tails a
  host with no docker on it, and because there is no Refresh button, **every re-entry to the module
  re-probes** while the answer is no, so installing docker mid-session is not a dead end.

### Creating and editing a container

Unlike VMs, where `CreateVmWizard` and `VmEditWindow` are separate screens, containers get **one**
window for both, `Views/Containers/ContainerEditWindow`: pages on the left in the `UnattendWindow`
idiom (General, Volumes, Network, Environment, Devices), and the same tab walk over `IContainerTab`,
so the window never names a page and adding one is a `TabItem` plus a `UserControl`. Both of that
interface's invariants carry over unchanged, and where two pages constrain each other the resolution
belongs in `DockerService.BuildCreateArgv`, not in `Apply` order.

- **One window because a container is not editable.** Docker fixes the image, mounts, network,
  ports, environment and devices at creation; `docker update` reaches only resource limits and the
  restart policy. So editing *is* creating, with an old container in the way, and two screens would
  be two shapes over one operation. `ContainerSpec` is the flat mutable model both directions use,
  for the same reason `UnattendConfig` is flat: it is edited keystroke by keystroke and has to be
  allowed to be invalid.
- **Save replaces, and the order is what makes that safe.** `DockerService.SaveAsync` stops the old
  container, **renames it out of the way**, creates the new one under the wanted name, and only then
  removes the old; a rejected `docker create` renames the old back and restarts it if it was running.
  The obvious order (stop, remove, create) leaves the user with nothing at all when docker refuses
  the new container, which it does for something as ordinary as a host port somebody else already
  holds. `docker create` plus a separate `docker start` rather than `docker run -d`, so the "start
  primary button's "and start" is a real branch. No `-v` on the `docker rm`, so named volumes outlive
  the container that mounted them, and the confirmation says so.
- **The window ends in three buttons, not two.** Cancel, Save, and a primary that also runs the
  container ("Save and start" on a new one, "Save and restart" on an existing one, because saving
  replaced it). Saving without running is the second-most likely thing to want, so it is a button
  rather than a check box somebody has to find on the General page, and which button was pressed is
  the only thing that decides it. The replace confirmation is keyed on there **being** an existing
  container, not on which button was pressed: a plain Save replaces it just as thoroughly, while a
  new container has nothing to replace and so asks nothing.
- **A user string never reaches the shell by interpolation.** Every command above this feature took
  no argument or took an id `IdRegex` had already vetted; `docker create` is the first that has to
  carry a name, an image reference, an environment value and a bind path. `RunSudoCommand` wraps
  whatever it is given in a single-quoted `bash -c`, so building that by interpolation would be one
  apostrophe from a broken command and one `$(...)` from a worse one. `DockerService.ArgvScript`
  therefore builds the argv in C#, joins it **NUL-separated**, base64s the blob and rebuilds a bash
  array on the host: base64 is `[A-Za-z0-9+/=]` and survives the quote rewrap untouched, and NUL is
  the one byte an argv member cannot contain. `read -r -d ''` rather than `mapfile -d ''`, so nothing
  depends on the host's bash being 4.4 or newer.
- **The pull is written out longhand for a reason.** `RunSudoCommandStreaming` neither escapes its
  argument nor wraps it in `bash -c`, unlike `RunSudoCommand`, so `Pull` spells the wrapper out
  itself. It is worth the streaming call: it opens its own SSH connection, so the container list
  keeps refreshing behind the dialog while a large image comes down, and docker's own output is what
  the dialog's status line shows. Pulling only happens when `docker image inspect` says the image is
  absent, so re-saving a container never re-pulls.
- **Reading a container back uses `HostConfig.Binds`, not `.Mounts`.** `Binds` is exactly what `-v`
  put there; `.Mounts` also lists the anonymous volumes the image's own `VOLUME` directive created,
  which the user never asked for and which `docker create` produces again by itself. Same file,
  `InspectAsync`, parses one `docker inspect` with `System.Text.Json` rather than a pile of Go
  templates, because env, mounts, ports and devices are all multi-valued and a tab-separated record
  cannot carry them. It starts parsing at the first `[`, since `RunSudoCommand` merges stderr in.
- **The image field is an `AutoCompleteBox`, not a `ComboBox`.** Avalonia's `ComboBox` has no
  editable mode, and the field has to be both: it lists what the host already has *and* accepts a
  reference that is not there yet. The volume picker is the opposite case and is a plain `ComboBox`:
  a volume that does not exist is not a useful thing to type. Either way the window opens before
  `LoadCatalogAsync` answers, so a picker holds its own value whether or not the catalog ever
  arrives; `MountRow.RebuildVolumes` is what makes that true for the closed one, by keeping whatever
  the mount already says among its items.
- **`DockerCatalog` is what the pickers need, not what docker was asked.** Its device nodes come off
  the host filesystem (`find /dev`, pseudo terminals dropped, capped, in the same round-trip and the
  same tagged-record script as the docker listings), because nothing else can say what is plugged
  into the machine and `/dev/ttyUSB0` typed from memory is how that field goes wrong. The Devices
  page suggests from it rather than binding to it, since a node can appear after the window opened.
  Its permissions are three check boxes rather than a box holding `rwm`, which is the closed-set rule
  the answer-file model already follows and which makes the two ways of writing nonsense (a letter
  that is not one of the three, and the same letter twice) unrepresentable instead of validated.
  Leaving the host box then fills the container path in from it (`DeviceRow.MirrorHostPath`), because
  a device almost always appears inside at the path it has outside; it stops as soon as the container
  path is one the user typed, the same rule the Create-VM wizard follows when it re-seeds a NIC after
  the OS changes.
- **The network decides whether ports exist.** On `host`, `none` or `container:x` there is no
  namespace to publish into and docker refuses the pair, so the Network page takes the mappings off
  screen entirely, and `BuildCreateArgv` drops them as well: a container inspected back with both
  can still be saved. `PortRow` also carries a **host address the page does not show**, because
  docker can bind a published port to one interface and an edit recreates the container from these
  rows; dropping the field rather than carrying it would quietly move a port off loopback onto every
  interface the host has. The mappings themselves are **kept** in the spec under such a network, the way
  the answer-file window keeps what was typed under an unselected option.
- **An editable list is a group box with a plus and a minus over it**, IntelliJ's own shape rather
  than an "Add variable" button on top and a Remove button repeated down every row. Volumes, port
  mappings, environment variables and devices are four of these in one window, so the strip is a
  control (`Views/Containers/RowToolbar`) and the wiring is one call (`RowList.Bind`), which is what
  keeps the glyphs, sizes and disabled rule defined once. `JbIconAdd` and `JbIconRemove` live beside
  `JbGroupBoxBorder` in `JetBrainsClassic.axaml` and outside its theme dictionaries, for the same
  reason: a green that means "add" should not change meaning with the theme.
- **A table is a table wherever it appears.** The heading strip (`#11808080` over a `#22808080`
  rule, `12,3` padding) and the transparent `JbTableRow` body inside these group boxes are the same
  two elements the VM and container lists draw in the main window, copied rather than reinvented, so
  a table in a dialog and a table in the shell read as the same control.
- **Those lists are `ListBox`es, where the answer-file window's tables are `ItemsControl`s**, and the
  difference is the minus button. A row there is only ever typed into, so selection would just fight
  the editors for focus; here one row has to be *pointed at* as well. That brings a trap with it:
  a `ListBox` selects a row on pointer press, but a `TextBox` marks the press handled, so clicking
  into a cell would leave the minus button aimed at whatever was selected before. `RowList.Bind`
  therefore selects a row when **anything inside it takes focus**, which is the honest signal and the
  one thing that makes a selection-based Remove safe on a row full of text boxes.
- **A typed-into table needs `JbFormRow`, not `JbTableRow`.** A selector is one tab stop by design,
  which is right for a list you pick from and wrong for a grid of text boxes, so the derived theme
  stops the row being a navigation boundary (`TabNavigation=Continue`) and stops it being a tab stop
  itself (`IsTabStop=False`), or every row would cost an extra Tab on the way to its first cell. The
  rows are also **not virtualised**: an unrealised row has no cells for Tab to reach, and these lists
  are a handful of rows rather than a listing. Because focus drives selection, tabbing through the
  cells also keeps the minus button pointed at the row being edited.
- **Validation is per page and selects the page.** `IContainerTab.Validate` answers the first thing
  the user has to change, and the window walks `TabItem`s rather than their contents so it can select
  the page that said so. That is what lets a message read as a sentence about a field the reader is
  looking at instead of one naming a tab.

### Reading a container's log

`ContainerLogsWindow` is opened from the list's right-click menu, one window per container, over
`DockerService.TailLogsAsync` and `IsRunningAsync`. It is non-modal and outlives a module
switch, exactly like a `ConsoleWindow`, so `ContainersModule` keeps the handles and closes them in
`Shutdown` (each tail holds an SSH connection of its own, and the shell disposes the shared one
straight after).

- **`2>&1`, inside the inner `bash -c`.** `docker logs` demultiplexes the container's two streams
  back onto the CLI's own stdout and stderr, and `RunSudoCommandStreaming` reads **stdout only**, so
  without the redirect an image that logs to stderr (which is most of them) shows an empty window.
  It goes inside the `bash -c` rather than on the `sudo` command so sudo's own stderr is not merged
  into the log as well. The command is written out longhand for the same reason `Pull` writes one:
  that call neither escapes its argument nor wraps it in a shell. Nothing is base64'd, because the
  only thing interpolated is an id `RequireId` has already vetted.
- **Follow is `--follow` plus the reconnect.** Ticked (the default) streams; unticking cancels the
  tail and leaves the snapshot on screen; either edge, and Reload, restart from the top. The box is
  emptied on restart, because a reload replays the same `--tail 1000` history and appending it would
  show every line twice.
- **A follow stream ends when the container does, so its clean end is not the end of the window's
  job.** `docker logs --follow` returns the moment the container stops and does not resume when it
  starts again, which would make a `docker restart` silently kill the window somebody is reading. So
  the window then **watches**: `DockerService.ContainerEventReceived` (the module's own events tail,
  already running before any log window can be opened) debounced 400 ms, with a 30 s poll behind it
  for a listener that failed to start, and one `IsRunningAsync` probe per look. The reconnect is a
  plain reload rather than an append with `--tail 0` or `--since`, and that is the whole reason it
  can be late without losing anything: docker keeps a container's log **across** a restart, so
  reloading the last 1000 lines shows the shutdown and the new startup in one piece, with no gap and
  nothing shown twice, and no client clock ever has to be compared against the host's. An **error**
  (a logging driver that cannot be read) does not start a watch: the reason it refused is still
  there, and every host event would retry it.
- **Lines are buffered on the read thread and drained by a 120 ms timer**, never posted one by one:
  a chatty container emits hundreds a second and a `Dispatcher.UIThread.Post` per line would swamp
  the UI thread. Each tail's `onLine` closes over its own token, so lines from a tail that has just
  been replaced (a reload, a reconnect) are dropped instead of landing in the new box. The text is
  capped at 1 MB, trimmed from the front on a line boundary, since a `TextBox` lays its whole text
  out with no virtualization.
- **Cancelling happens off the UI thread.** `RunSudoCommandStreaming` registers a callback that
  disconnects its `SshClient` inline on whoever calls `Cancel`, so closing a window would otherwise
  wait on the network. Nothing waits for the reader either; it unwinds on its own.

### Running a command in a container

`ContainerConsoleWindow` is a **real terminal** into `docker exec -it`, opened from the list's
right-click menu after `ContainerConsoleDialog` asks what to run and as whom. Non-modal, one per
container, outliving a module switch and closed by `ContainersModule.Shutdown`, exactly like a log
window. It is the first thing in the app that needs a **bidirectional** channel, and the first that
needs to understand escape sequences, so both had to be built.

- **The menu item is gated on `running`, not on `IsRunning`.** `ContainerRow.IsRunning` deliberately
  includes `restarting`, because Stop and Restart are worth offering on a container coming back up;
  `docker exec` against one simply fails, so `CanExec` is the stricter predicate rather than a
  loosening of the shared one. Logs, by contrast, have no state gate at all: a stopped container's
  output is exactly what somebody comes to read.
- **The image is asked what it has before the dialog opens, not after it is filled in.**
  `DockerService.FindProgramAsync` walks `ShellCandidates` (`bash`, then `dash`, then `sh`) in one
  `docker exec` with `command -v` (a POSIX builtin, so busybox answers it too) and the command box
  starts out holding the full path of whichever it found. So the default is **correct rather than
  merely likely**, and there is no substitution to explain afterwards; the order is what makes it
  right, since bash is what muscle memory expects, dash is a real shell on a Debian image without
  one, and sh is all a busybox image has. The probe **always exits 0**, answering with a path or with
  nothing, because `RunSudoCommand` turns a non-zero exit into an exception and "this image has none
  of them" is an answer rather than a failure; docker's own failures do still throw, and a container
  that stopped between the menu and here says so in its own words. An image with no shell at all is
  refused here, before a window that could not start is opened.
- **Only an edited command is re-checked, and then it is checked rather than corrected.** The default
  came from the host and is known good, so the common path costs no second round-trip; anything the
  user typed themselves gets one, and a refusal leaves the dialog up with the text still in it, the
  way `UnattendWindow` stays open when the answer file will not build.
- **The command is split on whitespace with no quote handling**, and the field says so. That covers
  what the box is for (`/bin/bash`, `bash -l`, `python3 -i`); implementing half of a shell's word
  splitting would be worse, because it would work until it silently did not, and the shell being
  opened is the right place for real quoting. An empty **User** passes no `-u` at all, so the image's
  own user applies, which is a different thing from asking for root.
- **`-e TERM=xterm-256color` is not decoration.** For a `-t` exec the daemon puts a bare
  `TERM=xterm` into the container, and under that a coloured prompt or `htop` falls back to eight
  colours or none. It has to name the terminal this client actually implements, which is also what
  `SshPtySession` asks the host for.

#### The PTY, and why the sudo password is not fed blind

`Services/SshPtySession` is `SshClient.CreateShellStream` on a connection of its own (like
`DownloadFileAsync` and `RunSudoCommandStreaming`, so it never holds `_ioLock`). Nothing existing
could carry it: `RunSudoCommandStreaming` is line-oriented, stdout-only, and `FeedSudoPassword`
writes the password and then **closes stdin**, which is the one thing an interactive session cannot
allow.

- **Nothing is shown until the container is reached.** Getting there means a login shell, so the MOTD,
  the shell's prompt and sudo's password prompt all arrive first, and a console that opened on those
  would be showing the plumbing rather than the container. So the session prints a per-session
  sentinel and **everything ahead of it is buffered and dropped**. The sentinel is printed by a shell
  *inside* sudo (`sudo -S -p '<marker>' bash -c 'printf "%s\n" "$0"; exec "$@"' '<sentinel>' ...`), so
  it lands after sudo has authenticated and immediately before the command replaces that shell;
  printing it before `exec sudo` would be simpler and wrong, because the password prompt would then
  fall on the far side of the gate and be shown. Its **newline is load-bearing**: stdout is a
  terminal and therefore line buffered, so a `printf %s` with nothing after it could still be in the
  buffer when `exec` throws the process image away. The line ending is skipped with the token, in
  whichever spelling the pty's output processing produced.
- **A session that dies before the sentinel reports the buffer as its reason.** That is the only
  account there is of a sudo refusal or a missing shell, and it means the failure path needs no
  second mechanism. The buffer is capped, oldest-first, because only the tail of an error matters.
- **The password is written only in answer to sudo's own prompt.** Feeding it blind is safe for a
  one-shot command, which discards a stdin it is not reading; here stdin is the user's keyboard, so a
  blind write would type the sudo password into their shell. So `sudo -S -p '<marker>'` is used with
  a marker generated per session (`Guid`), which cannot collide with an MOTD or with container
  output and so needs no time limit on the scan. Buffering everything ahead of the sentinel is also
  what removes the need to withhold a partial marker between reads: while the gate is shut a token
  split across two reads is contiguous by the time it is searched for. A **second** sighting means
  sudo rejected the first answer, and repeating it would only spend the remaining attempts, so the
  session ends with `CheckSudo`'s own wording. A NOPASSWD host never prints the marker and never sees
  a byte of the password.
- **The PTY is requested with `ECHO = 0`.** The line typed at the host's login shell and the password
  that may follow it are ours, not the user's. It costs them no echo of their own: `docker exec -it`
  puts the terminal into raw mode itself, and what they see while typing comes from the container's
  PTY.
- **An argv, not a command line.** The same rule `DockerService.ArgvScript` states: a shell path and
  a user name are user text. The vector is rebuilt on the host from a NUL-separated base64 blob and
  the rebuilding script is itself base64'd, so the only syntax the login shell has to understand is
  `"$(...)"`. The outer `exec` replaces that login shell, so when the command ends the channel closes
  rather than dropping the user at a host prompt.
- **Resize is reflection, and fails soft.** SSH.NET 2024.2.0 exposes no way to resize a `ShellStream`
  after it is made: the request lives on the channel and `ShellStream` keeps its channel private. So
  `_channel` is reached by reflection and `SendWindowChangeRequest` invoked on it, under the same
  rule `X11KeyboardGrab` follows when it digs Avalonia's display connection out of
  `Window.PlatformImpl`: best effort, never throws, and the fallback is a **stated outcome** rather
  than an improvisation. Here that outcome is a terminal stuck at the size it opened with, and the
  failure latches so a drag does not retry it per frame. When it works the SIGWINCH path is real, so
  `htop` reflows.
- **Disposal goes to the pool thread**, for the reason `ContainerLogsWindow.StopStream` gives:
  disconnecting an SSH client waits on the network and closing a window must not. The session is also
  disposed when it *ends* on its own, because a window left open on a finished shell would otherwise
  hold a connection for nothing.
- **A clean exit closes the window; a failure keeps it.** Typing `exit` is how somebody says they are
  finished, and leaving a dead terminal to be dismissed separately makes them say it twice. This is
  the opposite of the log window and is deliberate: a log is a fact about the container that resumes
  when it restarts, while a session is something the user started and ended. A session that ends by
  *failing* stays on screen, because the reason is the only thing that explains what happened and a
  window that vanished would take it with it; that is the one case **Reconnect** is for.

#### The emulator

`TerminalScreen` keeps scrollback and screen as **one list**, the screen being its last `Rows`
entries, which is what makes an ordinary line feed at the bottom a plain append. Scrolling inside a
`DECSTBM` region rotates within the region instead and feeds no history, because a program that set a
smaller region is managing its own window and its discarded lines are not history. Mutations bump a
single `Revision` rather than marking lines dirty, because Avalonia redraws a control whole, so
per-line dirt would buy nothing; the control's 16 ms pump compares it and invalidates only when it
moved, which is what keeps a flood of output from posting to the dispatcher thousands of times a
second.

What is implemented is what `vim`, `htop`, `top` and `less` use: the C0 set, `DECSC`/`DECRC`, `RI`,
the cursor and erase and insert/delete families, `DECSTBM`, `SGR` including `38`/`48` in both the
`;5;n` and `;2;r;g;b` spellings **and their colon forms**, `DSR`, and the DEC private modes for
application cursor keys, autowrap, cursor visibility, bracketed paste, the alternate screen and
**mouse reporting** (below). **Everything else is consumed, never printed**, so the failure mode for
something exotic is a missing effect and not a screen full of `[38;5;`. Deliberately absent, each
with a comment saying so: sixel, double-width lines, and character sets beyond consuming the
selector.

#### The mouse

`Terminal/TerminalMouse.cs` turns one pointer event into the bytes a program that asked for the mouse
expects, or into nothing when the mode it asked for does not cover that event. It sits in Core beside
the screen and the parser for the reason they do: which events are reportable and how they are
spelled is the terminal protocol, not a toolkit's idea of a pointer, so the control's whole job is to
turn pixels into a cell and a button and hand them over. Its output is checked against xterm's own
documented sequences rather than against itself.

- **Two settings, not one.** Modes 9/1000/1002/1003 say *what* to report and are one setting between
  them (`MouseTracking`), so turning any of them off is silence; 1005/1006/1015 say how to spell it
  and are a second (`MouseProtocol`). A program sets one of each, and 1002 plus 1006 is what nearly
  everything modern asks for. Resetting an encoding only falls back to X10 when it is the one
  actually in force, because three separate modes must not be read as one.
- **A release cannot say which button it was, outside SGR.** Every other encoding writes button 3 and
  keeps the motion and modifier bits above it, which is also why 1003's bare motion and a release are
  literally the same bytes under the X10 encoding. SGR keeps the real button and says "release" with
  its final letter instead, which is the whole reason it exists.
- **The X10 encoding gives each field one byte biased by 32, so nothing past column 223 fits.** Such
  a report is **dropped rather than clamped**: naming the wrong cell is worse than naming none, and
  anything likely to be run in a window that wide asks for SGR, which has no limit.
- **Shift is the escape hatch, and it is not optional.** Holding it hands the pointer back to this end
  for selecting text. Without it there would be no way to copy anything out of a full-screen program,
  because such a program is exactly the kind that takes the mouse.
- **Motion is reported once per cell, never once per pointer event.** A drag across the window is
  hundreds of moves, and each report is bytes on the wire plus a redraw at the far end.
- **A report names a viewport row**, 1-based, because that is the only frame the two ends share. So a
  press snaps the view to the bottom first: somebody scrolled back into history who clicks is asking
  about what they can see now. It also floors where the selection code rounds, since a report is
  about the cell the pointer is inside and a selection is about the boundary it is nearest.
- **A gesture belongs to whoever it started with.** The release is answered from the button this end
  recorded rather than from the current mode, so a program that turns tracking off between press and
  release still gets the release it is owed; a selection already under way survives tracking being
  turned on midway; and `OnPointerCaptureLost` ends both, because neither gets a release when another
  window steals focus mid-drag and a latched one would make every later hover a phantom drag.
- **A wheel notch is a button** (64 and 65), which is how `less` and `man` scroll at all, and it is
  reported in every tracking mode since a notch is a press. One report per notch: how far that
  scrolls is the far end's decision, where `ScrollWheelLines` is this end's answer for its own
  history.
- **The pointer becomes an arrow while the far end owns it**, because an I-beam over a screen that
  cannot be selected is a lie. The repaint pump reads the mode to decide, since setting it touches no
  cell and so moves no `Revision` there is anything to subscribe to.

Three things in this area were bugs waiting to happen and are written the way they are on purpose:

- **The deferred wrap flag.** Writing to the last column leaves the cursor there and wraps on the
  *next* printable character. Getting it wrong makes a line drawn exactly to the right-hand edge
  scroll a blank line, which is visible in anything that draws a box.
- **A decoded scalar is validated before it reaches a cell.** A truncated or over-long UTF-8 sequence
  can decode to a lone surrogate or past U+10FFFF, and both make `char.ConvertFromUtf32` throw in the
  renderer, on the UI thread, with the whole window between it and any catch of ours.
- **Parser replies leave the screen lock before they hit the network.** `Respond` fires inside
  `Feed`, which runs under `SyncRoot`, and sending goes to a blocking channel write; the control
  queues them and sends after the lock.

Rendering is **runs of cells sharing a style**, not cell by cell, which would mean eighty
`FormattedText` objects per row per frame. Runs of spaces are skipped entirely, and a full screen is
mostly those. The cell size comes from measuring a hundred `M`s and dividing, because a monospace
face still rounds each advance and measuring one drifts a whole cell across an 80-column line. The
sixteen ANSI colours and the two defaults are **themed** in `JetBrainsClassic.axaml` so the terminal
reads on Darcula and IntelliJ Light alike; the other 240 are arithmetic (a 6x6x6 cube and 24 greys)
and are absolute values rather than a matter of taste, so they are not.

The window registers its keyboard handler **tunnelled and `handledEventsToo`**, exactly as
`ConsoleWindow` does for the guest: without it Tab moves focus to the footer buttons and the arrow
keys drive them, so neither ever reaches the container. It forwards only while the terminal has
focus, so the buttons stay usable, and no button on that bar carries `IsDefault` or `IsCancel`
because Enter and Escape belong to the container. `Ctrl+Shift+C`/`V` are the two chords the window
keeps, because Ctrl+C already means interrupt and that is the more important of the two. Printable
text arrives through `TextInput`, so dead keys, compose and any layout work with no table to
maintain; `TextInput` carries no modifiers, so the key press that preceded it is what says whether it
was a chord and was already sent.

## User accounts

`UserAccountsModule` manages the host's own login accounts: a Users table and a Groups table in the
tab pair the VM module uses for its VMs and networks, add and delete for both, one dialog to add or
edit a user, and from it a password, a login shell, a home directory at creation, and supplementary
group membership. `Views/Users/UserEditDialog` and `GroupAddDialog` are the two dialogs;
`VirtDeck.Core/Services/UserAccountService` is the whole of the host vocabulary.

**The tab strip is the top of the module and each page carries its own toolbar**, which is also how
`VirtualMachinesModule` is laid out. A toolbar then only ever shows what applies to the table under
it, and the alternative is visibly wrong here: the system-accounts toggle is a fact about the user
list and would mean nothing sitting above the groups. It also settles which button is accented,
which a shared toolbar could not: that class is for the one command that creates something, so with
one create command per page both New user and New group carry it. Refresh is on both pages and is
one handler, since a single round trip fills both tables. The VM module's Networks page gets no
toolbar at all, because every network command needs a selection and so lives on the right-click
menu; an empty toolbar bar would be worse chrome than none.

**It is modelled on Cockpit's Accounts page wherever that has already settled a question**, because
it is the reference implementation everybody administering a Linux box has seen, and three of its
answers look like omissions until you know why.

- **Group membership is supplementary only**, applied with `gpasswd -a` / `gpasswd -d`. The primary
  group is shown ticked and **disabled with its reason on hover**, so it is visible without being
  changeable: moving an account to a different primary group leaves every file it owns grouped to
  the old one, with no undo and nothing on screen to say so. Creation is the one place `usermod -G`'s
  cousin is used instead (`useradd -G`), because there is no existing membership to diff against;
  everywhere else the delta is per group, since `-G` replaces the whole set and would silently drop
  a group added on the host since the dialog opened.
- **The home directory is set at creation and read only afterwards.** Moving one means
  `usermod -d -m`, which relocates the files and can fail part way across a filesystem boundary or
  on an open file. The box is disabled in edit rather than hidden, with a note saying why.
- **The Groups tab is unfiltered**, where the Users tab hides system accounts behind a toolbar
  toggle. `docker`, `libvirt` and `sudo` are all system groups by GID, so a GID filter would hide
  exactly the rows somebody opens that tab to find.

One divergence, deliberate: Cockpit *also* hides any account whose shell is `nologin` or
`/bin/false`, which hides sftp-only and service accounts that are real accounts somebody has to
administer. The filter here is UID only, and it is **the host's own range, both ends**
(`UID_MIN`..`UID_MAX` out of `/etc/login.defs`, plus root). The upper end is not decoration:
`nobody` is uid 65534 and clears `UID_MIN` by a mile, so a lower bound alone lists it as an ordinary
login account. Cockpit reaches the same place by naming `nobody`; the host's own number covers the
whole convention, `nfsnobody` included.

**The user name and the home directory follow the full name as it is typed**, which is the one place
the model is the Fedora installer rather than Cockpit, since Cockpit fills nothing in. "John Doe"
suggests `jdoe` and "Hans van den Berg" suggests `hvdberg`: the last word whole, prefixed with the
initial of every word before it. Anaconda's own `guess_username`, which is the rule every Fedora and
RHEL install has already taught its user, takes only the **first** word's initial; that is the same
answer for a two-word name and the wrong one for a surname with a tussenvoegsel in front of it,
where it drops "van den" and suggests `hberg`. Keeping every initial gets both right with no word
list to maintain and no language to detect, at the price of a written-out middle name contributing
its initial too ("Anne Marie van der Berg" suggests `amvdberg`), which is the same convention applied
to a name it has nothing else to say about.
`UserAccountService.SuggestUserName` sits beside `IsValidNewUserName` deliberately, because a
suggestion the app then refuses is worse than no suggestion: it folds accents (Jose, not `jos_`),
**drops** what the rule still disallows rather than folding it to an underscore, and trims a leading
character a name may not start with, so "3M Corp" suggests `corp`. Dropping is right there and
folding is right in the name box itself, where the user is typing the name and one character for one
is what keeps their caret in place. Each box stops following the moment the user types in it, the
`_nicsTouched` rule the Create-VM wizard follows when it re-seeds a NIC after the OS changes;
emptying one hands it back but **does not refill it there and then**, because a refill lands a whole
name under a caret at its end, which would turn backspacing a suggestion away into appending to it.
Which text is the user's is decided by **comparing the box against the last suggestion written into
it**, never by a flag held across the write, and that is not a preference: Avalonia's `TextBox`
raises `TextChanging` inline but **posts** `TextChanged` to the dispatcher, so such a flag is back to
false by the time the handler runs and the dialog's own writes all read as the user taking the box
over. Measured, with the flag: "Hans Berg" froze the user name at `h`. A comparison does not care
when the event arrives, and an empty box compares as untouched, which is the same rule saying a box
nobody has answered is one the suggestion may have.
The home directory's base is the **host's own** (`useradd -D`, tag `h`), not a hardcoded `/home`: an
ostree host says `/var/home`, and where the box was left empty `useradd` picked the base itself, so a
box the app fills in has to hold the path the host would have chosen anyway.

- **Always `sudo`, and this is the counterexample to `FileExplorerModule`.** That module is the app's
  one un-elevated caller because somebody browsing wants their own view of the machine. Here every
  mutation needs root whatever happens, and the lock state can only be read from the shadow file, so
  there is one privileged path and none of the "retry as root" machinery the file explorer carries.
- **The listing is one round trip in `DockerService.ListScript`'s shape**: a tag in field 0, real tab
  characters, every best-effort half fenced with `2>/dev/null` so it cannot take the exit status with
  it, and a closing `exit 0`. Only `getent` carries `|| exit $?`, because its failing is the one
  failure that means the listing failed. Seven tags: `u` a passwd line, `g` a group line, `s` a shell
  from `/etc/shells`, `d` a `login.defs` bound, `p` a `passwd -S -a` status, `h` the home base out of
  `useradd -D`, `v` the tools' presence.
- **The passwd and group lines are emitted whole and split on `:` by the client.** No field of either
  file may contain a colon, so that parse is exact and needs no escaping and no delimiter of our own
  choosing. It is the same instinct as the unbounded-field-last rule in `RemoteFileService.Parse`,
  arriving at the same place by a shorter route. Group members then split on `,`, which they equally
  cannot contain.
- **GECOS is stored raw and edited in place.** The field is comma-separated
  (`Full Name,Room,Work Phone,Home Phone,Other`) and the full name is only element 0, so writing it
  back rejoins the tail rather than discarding somebody's room number. `GecosHead`/`GecosTail`/
  `JoinGecos` are public precisely because that is a contract two assemblies share: the dialog
  carries the tail through untouched while the user edits the head.
- **The capability probe reports presence, not a version, and that is not laziness.** Debian and
  Ubuntu's shadow-utils build supports no `--version` on any of its binaries: `useradd`, `passwd` and
  `chage` all answer "unrecognized option" on stderr and print usage. The only version on such a host
  is the package manager's, which is a different question in a different vocabulary per distro. So
  the probe is `command -v useradd`, the slot says `shadow-utils`, and a host that does volunteer a
  version has it shown. Absent tools default to **unavailable**, `DockerService.DockerAvailable`'s
  rule rather than the KVM probes': the safe default is the one that explains itself, so the list
  carries a message and every command is disabled rather than hidden. **Every re-entry re-probes**
  while the answer is no, so installing the package mid-session is not a dead end.
- **No poll and no event tail, so there is a Refresh button.** Nothing on the host announces a new
  account the way `docker events` announces a container, and polling `getent` would buy nothing
  against how often accounts change. That is `FileExplorerModule`'s reasoning and its conclusion, and
  it is why `Deactivate` and `Shutdown` are both empty here: no timer, no poll, no tail, no window,
  and no second SSH connection. `ActivateAsync` still reloads on every return, per the contract that
  nothing is stale by the time it is visible.

### The password path

**A password never touches a command line, and `SetPasswordAsync` is the only thing in this service
that does not go through `ShellScript.Argv`.** `RunSudoCommand` puts whatever it is given on the
host's command line, where `ps` shows it to every local user; base64 would hide it from a glance and
from nobody at all. `SshConnectionManager.RunSudoCommand` already treats the sudo password this way
for exactly this reason.

So it goes over **stdin**, through `SshConnectionManager.RunPipeInAsync`, which was built for the
elevated upload and does precisely what is needed: it writes the sudo password and a per-call
sentinel to stdin, and the remote script skips lines until it has seen the sentinel, so everything
after it is the payload. The payload is `chpasswd`, the body is one `name:password` line, and
disposing the stream is the EOF `chpasswd` waits for. `chpasswd` splits on the **first** colon, so a
password containing one is fine; a line break is not, and is refused in the dialog and again in the
service.

Ordering is load-bearing twice. On create, `useradd` runs first and a failing password step is
reported as **what it is** ("the account was created, but"), because the account exists from that
point and saying the whole thing failed would be a lie. On edit, the password is set **before** the
lock, because writing a new hash clears the `!` that locks an account, so doing it after would
quietly unlock what the user just asked to lock.

Password quality is the host's business. The dialog checks only what it can answer without asking:
that the two boxes agree.

### Names, and the two rules that are not one

`ShellScript` is where the app's four ways of handing a command to bash now live (`Wrap`, `SudoWrap`,
`Argv`, `ArrayFrom`); `DockerService.ArgvScript` and `RemoteFileService.Wrap`/`SudoWrap`/`ArrayFrom`
are one-line forwarders onto it. This module is what made one home worth having: it is the first
whose every mutation carries user text, and a third copy of the one function in the app that stops
shell injection was the wrong answer.

**What VirtDeck will create and what it will address are different questions, and conflating them is
a bug.** `IsValidNewUserName` is strict (`^[a-z_][a-z0-9_-]{0,31}$`, what shadow-utils accepts
without `--badname`) and is only ever asked about a *new* account. Addressing an existing one is
checked by `RequireSafe`, which refuses only an empty name, a line break and a leading hyphen,
because an account already on the host may be called `Debian-snmp` or `systemd-network` and refusing
to lock or delete one because our creation rule dislikes it would be indefensible. Addressing is safe
without a strict rule anyway: `ShellScript.Argv` means a name is never read as syntax, and every
vector carries a literal `--` before it so it cannot be read as an option either.

The user-name box folds illegal characters to `_` as it is typed, reusing `CreateVmWizard.SanitizeName`'s
caret-preserving idiom, so the strict check is a backstop that owns the rule rather than something
anybody meets. What stops that fold looping is the fold being idempotent rather than the flag it
holds across its own write, for the reason above: the `TextChanged` it raises is posted, so it
arrives after the flag has been cleared, and the second pass ends because there is nothing left to
fold.

### Deleting

`MessageDialog.Choose` rather than `Confirm`, because a delete has a second question and asking it in
a follow-up dialog would put it after the point of no return. **Keeping the home directories is the
primary**, which is the accented default button, because the primary is what Enter presses and the
more destructive of two irreversible options must not be the one a reflex chooses; Cockpit defaults
its "delete files" box to unticked for the same reason. Both buttons say "Delete" out loud, since
neither is a way out. The prompt names up to five accounts and falls back to a count past that,
because `MessageDialog` is a fixed 420 wide and sizes to its content, and it says that files owned
elsewhere on the host stay where they are, still owned by a UID that no longer has a name.

A group that is somebody's primary group cannot be deleted; that refusal is `groupdel`'s to make and
is reported in its own words rather than pre-empted, because the host is the thing that has to agree.

### The group tick list

The four answer-file checklist pages and this dialog's group list are the same widget, so they share
`Views/Unattend/CheckRow` and the single `DataTemplate` in `App.axaml`'s `Application.DataTemplates`
rather than growing a fifth near-copy. `CheckRow` gained a plain `(id, label, checked, enabled, hint)`
constructor beside its `UnattendOption` one, plus `IsEnabled` and `Hint`.

**The `Border` the template now wraps its `CheckBox` in is load-bearing, not padding**: a disabled
control is not hit-testable in Avalonia, so the tooltip explaining why the primary group cannot be
unticked has to hang off an enabled parent. `MessageDialog.axaml` already carries the same workaround
for its disabled alternative button. `Hint` is null on all four answer-file pages, so nothing appears
there.

## File explorer

`FileExplorerModule` browses the host's filesystem: a path bar, one multi-select detail list (Name,
Size, Modified, Permissions, Owner, Group), Back/Forward/Up/Home/Refresh and a hidden-files toggle.
It also **moves and copies files about, on the host**: Cut, Copy and Paste, from the context menu
and from Ctrl+X, Ctrl+C and Ctrl+V. It **uploads and downloads**, files and whole directory trees,
by command or by dragging files in from the desktop, and dragging rows onto a folder row moves them
on the host. It **renames** in the row itself (F2) and **deletes**, permanently and with a
confirmation in front of it. There is still no viewer, no tree pane and no directory watch.

- **It lists as the logged-in user, and that is the point.** Every other remote file call in the app
  goes through `RunSudoCommand`, because every one of them is looking for a VM's disks under
  root-owned `/var/lib/libvirt/images`. Somebody browsing wants their own view of the machine, so
  this module is the app's first and only user of `SshConnectionManager.RunCommand`'s non-sudo path
  for real work. That runner is not a drop-in for the sudo one: it neither wraps its argument in
  `bash -c` nor exports a locale nor folds stderr, so the script does all three itself.
- **`Services/RemoteFileService` is the one place a remote directory is listed**, and one `elevated`
  flag covers both callers: this module passes false, `VirshService.ListDirectory` passes true and
  is now a four-line forwarder over it. That is what keeps the `find -printf` format string, with
  its dereferenced-type and unbounded-name rules, from existing twice. `ParentPath` and
  `CombinePath` moved there with it.
- **A failure is a value, not an exception.** `RunCommand` turns a non-zero exit into a throw, and
  "you may not read this directory" is something the module has to draw. So the script always exits
  0 for the states it models and answers with a **tagged record** (`x` an entry, `e` a stated
  failure), the same idiom `DockerService`'s `c`/`u` listing uses, and `DirectoryListing` carries a
  `ListFailure` plus its wording. Genuinely unexpected failures still throw, as everywhere else.
- **The retry as root is offered on any failure, and never latches.** A path inside a directory the
  account cannot search is indistinguishable from a missing one to the shell, so refusing to offer
  the retry on "not found" would strand the user for the wrong reason. What the elevation covers is
  **one listing**: the next navigation reads as the user again, and while an elevated listing is on
  screen the status bar says "listing as root", because the one case where somebody is not seeing
  their own view of the machine is the case worth saying out loud. Latching it would be fewer clicks
  in `/root` and a lie everywhere after.
- **`%y` and `%Y` are both fetched.** `%Y` is the dereferenced type, so a symlink to a directory is
  navigable (which is what the picker already did); `%y` is the entry's own, so a symlink is still
  identifiable, shows its target in the Name column, and is dimmed rather than hidden when `%Y` comes
  back `N` or `?`. **`%f` is last and the parse splits with a cap of ten**, so the name is the
  unbounded remainder and a name containing a tab survives; a tab inside a symlink target would
  mis-split that one row, and names are far likelier to be odd than link targets.
- **This module has a Refresh button where the VM and container lists deliberately do not.** Those
  have an event tail (`virsh event --loop`, `docker events`) telling them when to re-read; nothing on
  the host tells a client that a directory changed, and polling a filesystem over SSH to find out
  would be worse than a button. The same fact is why a paste has to re-list explicitly when it
  finishes, reusing the expression the Refresh button itself runs. For the same reason `Deactivate`
  is empty and `Shutdown` cancels a running paste and nothing else: there is no timer, no poll, no
  tail, no window, and the only `SshClient` of its own this module ever holds is the one a paste
  streams on. `ActivateAsync` still re-lists on every return, per the contract that nothing is stale
  by the time it is visible.
- **Sorting and the hidden-files toggle never touch the host.** Both run `PopulateList` over the
  entries already in hand. Directories come first whatever the sort says and the direction applies
  inside each group; Size orders on the `long`, and **Modified orders on the string**, which is
  correct because it is formatted host-side as `yyyy-MM-dd HH:mm`, fixed width and ISO ordered, so
  lexicographic order already is chronological order. A failure latches `_failed` precisely so a
  sort click cannot replace the reason the list is empty with an empty list.
- **The toolbar is five square icon buttons**, themed `JbIconButton`: the ordinary bordered button
  shrunk to the 24 that every field and button in the app already shares, with its padding dropped,
  so "square" is one metric rather than a size set per view. Their glyphs are the house 16x16
  stroked style, and `IconRefresh` is the app's first arc, because a circular arrow cannot be drawn
  with the straight segments the module icons use. **Each glyph binds its `Stroke` to
  `$parent[Button].Foreground`, not to `JbButtonForeground`**: bound to the brush directly it would
  stay at full strength while the button is disabled, and Back and Forward are disabled most of the
  time. With no label left on them the tooltip is the only name each button has, so all five say
  what they do and name their shortcut.
- **The column headers are `Button`s themed `JbColumnHeader`**, a flat cell derived from
  `JbToolButton` the way `JbStatusBarButton` is, so a table whose columns sort looks identical to one
  whose columns do not until the pointer is over it. Everything else about the strip and the rows is
  the VM and container lists' table, copied rather than reinvented.
- **Icons are `FileIcons`, decided once for the whole module** and warmed inside the same `Task.Run`
  as each listing, exactly as `RemoteFileBrowserDialog` does it, so building rows on the UI thread is
  pure cache hits and an SVG icon theme never rasterises with that thread held. See "Remote file
  browser icons" above for the whole story; the same rule applies here, that the icon is the
  *client's* opinion of the extension because the person reading the list is at the client.
- **The selection follows whatever just happened**, which is what makes a listing of several
  hundred entries usable. Stepping out of a directory selects the one stepped out of; a rename, a
  paste and an upload all select their result and scroll it into view. `_selectNext` is the one
  channel for that, a set rather than a name because a paste moves any number of entries, and it
  holds the names things **landed** under rather than the names they were called: under "keep both"
  those differ, which is why `PasteOutcome.Landed` and `TransferOutcome.Landed` exist at all. The
  paste script had been sending the final basename since it was written and nobody was reading it.
  The navigation half is expressed as "is the place being left below the place being entered?"
  (`ChildOnTheWayTo`) rather than as a rule about the Up button, so Back, a multi-level jump in
  history and a path typed into the box all behave alike, a refresh names nothing, and `/etcfoo` is
  not mistaken for a child of `/etc`. A paste sets it immediately before re-listing the same
  directory, so navigation only fills it in when nothing else has.
- **Cut, Copy and Paste, and the clipboard is the module's own.** It holds absolute host paths
  snapshotted at the gesture (a `RemoteEntry` carries no path, and the directory on screen has
  usually moved by the time Paste is pressed) plus the one bit saying which gesture it was. It
  deliberately **never touches the desktop clipboard**: a path on the server means nothing pasted
  into a local application. "Copy path" is the separate command that does put text on the real
  clipboard, and it is unchanged. A cut entry is **faded whole** (`RemoteFileRow.IsCut`, the
  `DockPanel.cut` style) rather than only in its name the way a broken symlink is: that one is a
  fact about the entry, this is a pending change to it, which is the staged-row idiom `VmEditWindow`
  already draws. The fade is read off the clipboard on every rebuild rather than set once, so it
  survives navigating away and back, a sort click and the hidden-files toggle alike.
- **Every question is asked before a byte moves.** `RemoteFileService.InspectPaste` is one round trip
  that answers whether the destination is still there and writable, whether it sits inside one of the
  sources, and per source whether the name is taken and what kind each side is; the user settles every
  clash against that, and only then does `Paste` run. The alternative, asking as the copy goes, means
  a dialog interrupting a transfer already under way. A refusal is a `PasteBlock` value on the plan
  rather than an exception, exactly as a listing's `ListFailure` is, and for the same reason: the
  module has to draw it.
- **There are two scripts because there are two runners.** The pre-flight is NUL-terminated tagged
  records like the listing's, name last and unbounded, through the ordinary `RunCommand` /
  `RunSudoCommand` pair. The paste itself is **line**-oriented with every payload field base64'd,
  because it streams and the callback is a line at a time; base64 is what stops a name containing a
  newline splitting a record, and each record carries the item's **index**, so a reply is matched to
  its source by position and never by name (two sources from different directories can share a
  basename). Both build their bash arrays from a NUL-separated base64 blob with
  `while IFS= read -r -d ''`, the `DockerService.ArgvScript` idiom, and both `exit 0` for every state
  they model.
- **A paste streams, so it cannot freeze the rest of the app.** `RunCommand` and `RunSudoCommand`
  hold `_ioLock` for their whole call, so a multi-gigabyte `cp` through either would stop the VM
  list, the container list and every other module dead for as long as it ran.
  `SshConnectionManager.RunCommandStreaming` is the new un-elevated sibling of
  `RunSudoCommandStreaming`: a connection of its own, a line at a time, and the same rule that the
  caller spells out its own wrapper because it neither escapes nor wraps what it is given. That is
  also what gives the status bar a live "Copying x (3/5)" and `Shutdown` something to cancel. There
  is no Cancel button in this pass, because a module owns the shell's two status slots and has no
  footer to put one in; the token is there for when there is somewhere to put it.
- **A folder onto a folder merges, and a paste never deletes what the user did not name.** The
  conflict dialog's primary is **Replace** for a file and **Merge** for a folder, because that is
  what actually happens: `cp -a "$s/." "$t/"` puts the contents in, same-named files are overwritten,
  and whatever was only in the target stays. Deleting the target first would be the literal reading
  of "replace" and would destroy files nobody was asked about. Removing something on purpose is the
  Delete command's job, and it names every entry it is about and asks first. The `rm -rf` inside the
  paste script is of a **move's source**, which is what a move is.
- **A copy back into its own directory has no question to ask, so it is not asked.** Replacing would
  mean copying a file over itself, which `cp` refuses, and there is no rename here to offer any other
  name, so it lands beside itself as the first free `name (copy)`, `name (copy 2)`. The free-name
  loop runs on the host and splits an extension only for a name matching `?*.*`, so `.bashrc` is not
  turned into ` (copy)` and a directory keeps its whole name. A *move* into the same directory is
  the no-op it looks like, and simply clears the clipboard.
- **`PasteConflictDialog` is a small window rather than a stretched `MessageDialog`**, the way
  `DeleteVmDialog` is, for two things `MessageDialog.Choose` cannot carry: the "do the same for the
  rest" box, and a disabled **primary**, where `Choose` can only disable its alternative. A file and
  a folder of the same name is exactly that case, since neither can replace or merge into the other:
  the primary is disabled with its reason on hover, never hidden, so the dialog asks the same
  question every time instead of quietly becoming a different one. "Do the same for the rest" still
  skips a kind mismatch, because there is no overwrite there to apply.
- **Writing offers the same one-shot root retry that reading does, and it latches no harder.** A
  destination the account cannot write to, or an item the host refused, gets one "retry as root"; the
  retry re-runs only the items that failed, with the answers already given, and `_shownElevated` is
  untouched, so the next paste is read and written as the user again. Whether root is worth offering
  is decided by matching the tool's own words, which is reliable because the script exports
  `LC_ALL=C`. A cut is **spent only once the move happened**, so a partly refused one stays on the
  clipboard.
- **Keys are split between the list and the top level, and focus is what splits them.** `ConsoleWindow`
  and `TerminalModule` register theirs on the window because the guest and the container need every
  key; here Backspace still belongs to the path box, so Backspace-for-Up, Alt+Left/Right and Enter
  live on the `ListBox` alone. The **commands** cannot: repopulating the list destroys the
  `ListBoxItem` that had keyboard focus, and cutting, deleting and navigating all repopulate, so by
  the time Ctrl+V is wanted the list has usually stopped being focused and a list-scoped handler
  would never see the chord. Ctrl+X, Ctrl+C, Ctrl+V, F2 and Delete therefore hang off the top level,
  added on attach and removed on detach so another module's keys stay its own. They are registered
  **bubbling and deliberately not handled-too**, and that is the entire gate: a focused `TextBox` has
  already consumed all of those by the time the event would arrive, so inside the path box and inside
  the rename editor they keep meaning what they mean in any other text box, with nothing to test for.

### Deleting

One `rm -rf` per entry, permanently, with a single confirmation in front of it. There is no trash:
`gio trash` needs the host's own desktop session and would put files somewhere the person at this
end cannot see, and a VirtDeck-owned trash directory would be a second filesystem to explain.

- **The confirmation is the whole safety mechanism, so it is not optional.** No Shift to skip it, and
  no per-entry prompt either: the question is asked once, about the whole selection, before anything
  runs. It names up to five entries and falls back to a count past that, because `MessageDialog` is a
  fixed 420 wide and sizes to its content, so a selection of three hundred would draw a window taller
  than the screen. A folder is always said to take everything in it, because that is the part
  somebody can be wrong about. It is `MessageDialog.Confirm` rather than a window of its own:
  `PasteConflictDialog` earns a window for the two things `Choose` cannot carry, a "do the same for
  the rest" box and a disabled **primary**, and a delete has neither, so a new window here would be
  `MessageDialog` with one word changed. `DeleteVmDialog` is the same story from the other end and
  collapses to exactly this dialog when the VM has no disk images to offer. Enter confirms, because
  the primary is `IsDefault`, which is what `DeleteVmDialog` does for a strictly larger delete: the
  dialog is modal with nothing to type in it, so Enter is a second deliberate gesture rather than a
  keystroke carried in from the list, and Esc cancels.
- **There is no pre-flight, and that is the difference from paste.** `InspectPaste` exists because a
  paste has questions to settle before a byte moves: is the destination there, is it writable, what
  is already called that. A delete has no destination and no conflict. "Is it still there" is
  answered by `rm -f` itself, which is silent about a path that has already gone and is right to be,
  because the user asked for it not to be there. "May I" is answered by the run, per entry, and that
  answer is what drives the root retry, so asking first would be a round trip and a race for nothing.
- **One script, streamed, in `PasteScript`'s shape.** Line-oriented, index-tagged, payload fields
  base64'd, `exit 0`, over `RunCommandStreaming` / `RunSudoCommandStreaming`. It streams for the
  reason a paste does: `RunCommand` holds `_ioLock` for its whole call, and an `rm -rf` over a large
  tree is not a call the VM list and every other module can wait behind. Streaming is also what gives
  a per-entry counter, a token to cancel and a **message per refused entry**, which is what lets the
  root retry re-run only the entries the host actually refused.
- **Only the first line of `rm`'s stderr is kept, and the host is what trims it.** `rm -rf` prints one
  line per entry it could not remove, so one refused tree can produce thousands, and every line after
  the first is a consequence of it: `rm` descends, fails on the child, then reports the parent as not
  empty. The first line is the reason, and it is the line matched for "Permission denied", which is
  reliable because the script exports `LC_ALL=C`. Measured on a directory holding one unreadable
  child, the reported failure is the child's `Permission denied` rather than the parent's
  `Directory not empty`, which is the message worth showing.
- **The script refuses a path with no basename.** The empty string, `/`, a trailing slash, `.` and
  `..` are all the same test, `${s##*/}`, and every one of them comes back as a per-entry failure
  rather than being run. Every path here is built from the directory on screen and a name out of that
  directory's own listing, so none of them can occur; `rm -rf` is the command where "cannot occur" is
  not a good enough reason not to check.
- **`rm` never follows a symlink**, so deleting a link to a directory loses the link and leaves the
  directory, which is what somebody deleting the row in front of them means. Worth stating because
  `RemoteEntry.IsDir` is the **dereferenced** type (`%Y`), so a link to a directory reads as a folder
  in the list and in the confirmation.
- **The first pass is always as the user, however the listing was read.** An elevated listing covers
  one listing and does not latch, and a delete is the last thing in the app that should quietly
  escalate; being refused is what earns the offer. The retry re-runs only the refused entries and
  `_shownElevated` is untouched, exactly as paste's retry is.
- **Cancelling leaves a partly deleted tree, and there is nothing to undo.** A cancelled transfer
  takes back what it created; that rule cannot apply here, because what a delete created is absence.
  So the service catches the cancellation rather than throwing it on and hands back what it got
  through, the module says so plainly with a count, and the re-list is **not** skipped the way a
  cancelled paste's is: whatever went is gone, and the listing would otherwise still be showing it.
  The Cancel button is there anyway, because the alternative is no way out of a delete of the wrong
  three hundred entries, and the half-deleted state is one the operation passes through whether or
  not anybody can stop it.
- **It uses the transfer strip rather than the status line a paste counts into.** A paste counts into
  the status slot because it had nowhere to put a Cancel button when it was written; this is the one
  command in the module that cannot be undone, so it is the one that most needs the button beside it.
  Progress is over entries rather than bytes, which the bar takes perfectly well, since `XferProgress`
  is a plain 0 to 1000 range and `PaintXfer` is the only byte-shaped part of the strip, which a
  delete does not call.
- **The selection still follows what just happened, and a delete leaves nothing to follow.** So what
  **failed** is what gets selected, since it is still there and is the work left over; a clean delete
  takes the nearest surviving row instead, the first below the block that went or the last above it,
  read off the rows on screen before the re-list. A directory the delete emptied simply says it is
  empty.
- **A deleted path comes off the module's clipboard**, and so does anything that was under a deleted
  directory: the clipboard holds paths and one of them may have just stopped existing, which would
  otherwise fail at paste time saying so. A cut whose every entry is gone stops being a cut rather
  than becoming an empty one. Same rule, and the same reason, as `FollowRenameInClip`.

### Uploading and downloading

`Services/RemoteTransferService` is the only thing in the app that moves a user's files between this
PC and the host. It is a sibling of `RemoteFileService`, not part of it: that one is metadata and
host-to-host operations, this one is bytes crossing the boundary.

- **One mechanism, `tar` over exec, both directions.** It is the only option that covers files *and*
  whole trees, elevated and not, in one shape: the recursion is tar's, and `sudo` is a prefix. SFTP
  runs as the login user only, which would leave this module's one-shot "retry as root" working for
  listing, paste and rename and not for a transfer, and it depends on the host's sftp subsystem.
  **It would not buy speed either, so do not rewrite this for one:** measured at **112 MB/s** to
  another machine over gigabit ethernet, which is about 90% of the 125 MB/s line rate and roughly
  the ceiling once TCP/IP, SSH framing and encryption come off. A transfer here is network-bound, not
  bound by the pipeline, which is the opposite of the NBD media path beside it (that one is
  latency-bound on small reads, see "Removable media"). GNU tar is assumed, as GNU `find -printf`
  already is.
  `SshConnectionManager.RunPipeOutAsync`/`RunPipeInAsync` are the two primitives, each on a
  **connection of its own** for the reason every long call here is: a multi-gigabyte transfer must
  not hold `_ioLock` and stop every other module while it runs. `DownloadFileAsync` is now a wrapper
  over the first of them, so the read loop exists once.
- **`RunPipeInAsync` must not use `RemoteFileService.Wrap`, and that is not a style preference.**
  `Wrap` is `echo <b64> | base64 -d | bash`, which makes **that pipe bash's stdin**; a payload command
  under it inherits the exhausted script pipe instead of the SSH channel and reads nothing, which
  `tar` reports as "This does not look like a tar archive". Every other script in the app can be piped
  in because none of them reads stdin. So `RunPipeInAsync` takes a **script body** rather than a
  command line and wraps it itself, as `bash -c "$(echo <b64> | base64 -d)"` in both the elevated and
  the unelevated case, which hands bash the script as an argument and leaves stdin alone. The download
  side is free to keep `Wrap`, because `tar -c` never reads stdin.
- **The elevated upload cannot use `FeedSudoPassword`, and a sentinel is what replaces it.** That
  helper writes the password and then **closes stdin**, which is exactly what an upload cannot allow,
  since stdin is the payload. Writing the password and trusting `sudo -S` to stop at the newline is
  only half an answer: sudo does read a byte at a time and never over-reads (measured: feed it ten
  lines with a wrong password and lines four onward are still on the stream), but on a **NOPASSWD**
  host, or with credentials cached, it does not read stdin *at all*, and the password line would then
  be the first thing `tar` saw. So the client writes `password`, then a per-call `sentinel`, then the
  payload, and the host skips lines until it has seen the sentinel: both cases converge. Same trick
  and same reason as `SshPtySession`'s per-session marker. A *wrong* password needs no handling: sudo
  spends its three attempts on the next three lines and exits, and the non-zero exit is reported;
  `CheckSudo` has rejected it at the login window long before this anyway.
- **The upload pre-flight is `InspectPaste`'s destination half.** `RemoteFileService.InspectIncoming`
  runs the same `-d`/`-w`/`-x` checks and the same per-name probe, drops the "destination inside a
  source" loop (a local directory cannot contain a remote one), and answers the same `PastePlan`, so
  `PasteConflictDialog` and `ResolveConflictsAsync` settle an upload's conflicts **without changes**.
  It also returns each name's free `name (copy)`, because a transfer has no host-side loop of its own
  to resolve `KeepBoth` in; the extension rule is `PasteScript`'s exactly (a dot after the first
  character only, never for a directory) so a copy is named the same whichever route created it, and
  `FreeLocalName` repeats it on the client for a download. **That field is base64'd on the wire**: it
  sits before the entry name so the name stays the unbounded last field, and a name holding a tab
  would otherwise split the record in the middle.
- **Extraction finishing is not proof a download worked.** A `tar` that cannot open a member still
  writes the end-of-archive blocks and exits non-zero, so its stdout is a perfectly valid *empty*
  archive and the client side sees nothing wrong. The pump task's exception is therefore kept and
  awaited separately after extraction rather than swallowed; without that, a refused download
  reported success with no files in it and an empty error message.
- **A cancelled transfer takes back what it created.** Cutting `tar` off mid-entry leaves whatever
  it was part way through, and a half-written file presented as a real one is worse than no file, so
  cancelling removes the top-level names the transfer brought into being, the way `ExportVmDialog`
  deletes its `.part`. **Only those**: an entry the user chose to **Overwrite** is left where it is,
  because the tool truncated the original the moment it opened it and neither end ever held a copy,
  so there is nothing to restore and removing it would destroy what was there before the transfer
  started. That one case cannot be made clean, so it is **named in a dialog** instead of passed over
  in silence. The `rm -rf` this adds can only ever reach names the pre-flight reported as free,
  which is what makes it a tidy-up rather than a delete.
- **Cancelling is the only trigger for that.** A transfer that *failed* keeps what did land, because
  tar extracts the members it can and reports the ones it cannot; throwing those away over an
  unrelated entry is not what a failure means, and it is the same rule paste follows when it reports
  per-item failures and keeps the successes.
- **A transfer retries whole where a paste retries per item**, and that is the one place the two
  shapes differ. A paste is a loop the host reports on item by item, so a denial can re-run just the
  items that failed; a transfer is a single `tar`, so a denial re-runs all of it. Neither touches
  `_shownElevated`, which stays a fact about the listing on screen.
- **`-h` and `--no-same-owner`.** Symlinks are dereferenced in both directions (`tar -h` going out;
  plain recursion coming in, since `Directory.Exists` and `FileStream` follow links by themselves):
  somebody downloading a folder wants the files, it behaves the same on either client OS, and it
  never asks Windows to create a symlink, which needs a privilege there. The cost is that a cyclic
  symlink makes tar recurse, which is GNU tar's own caveat under `-h`. `--no-same-owner` because the
  archive is built here: as root, tar would otherwise restore whatever uid a client-built entry
  carried, so instead the result belongs to whoever ran tar.
- **`AttributesToSkip = 0` is load-bearing.** .NET's default `EnumerationOptions` skips `Hidden` and
  `System`, which on Linux means every dotfile in an uploaded tree would be silently missing. Both the
  archive walk and the size walk set it, or the bar would run past 100% on a tree it under-counted.
- **Entry names off the host are checked before anything is created.** `Remap` swaps an entry's first
  path segment for the local name the user settled on, drops anything whose first segment was not
  asked for, and refuses a `..` anywhere; the resolved path is then required to stay under the chosen
  directory. A tar's entry names are the far end's to write, so they are input.
- **The transfer strip is the module's, and a delete uses it too.** A module owns the shell's two
  status slots and has no footer, which is why paste has a `CancellationTokenSource` and nowhere to
  put a Cancel button; a multi-gigabyte transfer with no way to stop it would be worse. The strip
  is `ConsoleWindow`'s `XferPanel` markup copied rather than reinvented, hidden unless something is
  running, and the service throttles progress to 120 ms so the hop to the UI thread is not paid per
  64 KiB chunk. **Paste still does not show it**, which is why its own Cancel button is unreachable;
  it counts into the status slot instead, and wiring it up is a change nobody has made yet.
  `Shutdown` cancels the one token for every one of these operations, because the shell disposes
  the shared connection, and with it the auth material every second client borrows, straight after.
- **`_busy` is one flag for paste, delete, upload and download.** They are the same hazard: each runs
  on a connection of its own and each ends by re-listing the directory underneath it.

### Dragging

Two gestures, and deliberately not a third.

- **Files dragged in from the desktop upload** into the folder row under the pointer, or into the
  directory on screen when the pointer is not over one. The handlers go on the module root, the idiom
  all four older drop targets follow, so a drop on the empty space below the last row still lands.
  `DropFiles.LocalItems` is the new sibling of `LocalFiles`: same translation, but a **directory is a
  real answer** here, where to `LocalFiles` it is not (its callers all want one file to feed a VM).
- **Rows dragged onto a folder row move on the host**, or copy with Ctrl held, reusing `InspectPaste`
  + `ResolveConflictsAsync` + `Paste` unchanged; `HostMoveAsync` is the shared core Paste now calls
  too. Only a folder row is a target: dropping on the listing itself would mean moving to where they
  already are, and there is no `..` row to move up through.
- **The selection is captured on the press, not when the drag starts.** A `ListBox` collapses a
  multiple selection to the row under the pointer on press, so by the time the pointer has moved far
  enough to be a drag the other rows are gone. The press handler is therefore **tunnelled**, ahead of
  the `ListBox`, and a press inside the existing selection drags all of it while a press anywhere else
  drags one row.
- **There is no drag-out, and Avalonia is why.** Dragging a row to the desktop needs the bytes to
  exist locally at the moment the drop target asks: `DragDrop.DoDragDropAsync` takes the *synchronous*
  `IDataTransfer` (`IAsyncDataTransfer` is the clipboard's, not drag-and-drop's), and `DataFormat.File`
  wants a real local `IStorageItem`, so there is no promised-file hook to hang a download off. The
  bytes would have to be fetched inside a synchronous callback on the platform's drag thread, and on a
  host whose files are routinely disk images that is a freeze, not a feature. Downloading is a command
  instead. **That is also why the internal payload is an in-process format** carrying host paths and
  nothing else, no text and no files: an in-process format cannot leave the app, so a drag can never
  be accepted somewhere that would imply a transfer this module is not going to do.

## Terminal

`TerminalModule` is one shell on the SSH host, as the account the user logged in with, drawn by the
same `TerminalControl` the container console uses over the same `SshPtySession`. Every other module
wraps a command (`virsh`, `docker`, `dd`); this one is the host itself, so anything the UI does not
model no longer means leaving VirtDeck with the credentials in hand.

- **The login user's shell, not root, and that needed a second factory.** `SshPtySession.Open` wraps
  its argv in `exec sudo -S -p '<marker>' ...` unconditionally, so every session it can make is a
  root one and its gate, marker and preamble all exist to hide sudo's prompt and the login shell
  running it. A host terminal wants none of that: the MOTD and the prompt **are** the content. So
  `SshPtySession.OpenShell` (behind `SshConnectionManager.OpenShellPtyAsync`) asks for a plain SSH
  shell, which already runs the account's own shell, with no argv, no bootstrap line, no sentinel and
  the gate open from the first byte. `sudo` typed into it prompts the user the way it would anywhere
  else, and the sudo password VirtDeck holds is never written into this session.
- **Echo is the trap.** `Open` sets `TerminalModes.ECHO = 0` because the line it types at the login
  shell and the sudo password that may follow are the client's rather than the user's, and it costs
  them nothing because `docker exec -it` puts the terminal into raw mode inside the container and
  echoes for itself. There is nothing downstream of a plain login shell to do that, so copying the
  modes dictionary here would mean typing into a shell that shows nothing back. `OpenShell` uses the
  overload that takes no modes at all.
- **`Start` is now the caller's job, on both paths.** `Open` used to spawn the read thread inside the
  factory, which was safe only because its gate buffers everything until the caller has subscribed.
  With the gate open from the first byte, anything read between the factory returning and
  `DataReceived +=` is dropped, and on a login shell that is the MOTD and the first prompt. Both
  factories therefore hand back an **unstarted** session and both call sites call `Start()` right
  after subscribing, so there is one rule rather than two. A session abandoned before `Start`
  disposes cleanly.
- **`Deactivate` deliberately does nothing but stop the resize debounce.** This is the one module that
  costs something while hidden, and that is the whole feature: the PTY read thread keeps feeding
  `TerminalScreen` under its own lock, so a `tail -f` left running is still running on the way back.
  Nothing needs the UI thread on that path, and `TerminalControl` stops only its 16 ms repaint pump on
  detach, so re-attaching catches everything up in a single repaint against the stale `_drawnRevision`.
  It is the same exception the `virsh event --loop` and `docker events` tails already take, taken
  further. `Shutdown` **must** end the session, because `MainWindow.Shutdown` disposes the shared
  connection straight after, and with it the auth material this session's own client authenticated
  with.
- **Auto-start once, then stay dead.** `ActivateAsync` opens the shell the first time the module is
  looked at and never again: coming back to a live session must not disturb it, and coming back to one
  the user ended by typing `exit` must not silently revive it. There is no window to close here, so a
  clean exit leaves the last screen with a line saying so and Reconnect enabled; a failure reads the
  same way with its own reason. That is the opposite of `ContainerConsoleWindow`, which closes on a
  clean exit precisely because it *is* a window and dismissing a dead one would make the user say they
  are finished twice.
- **The status bar is the module's two slots, so there is no footer.** The container console needs its
  own status line because it is a window; a module already owns the shell's, and stacking a second bar
  above it would be one bar too many. Left says what the session is doing, right says `user@host`
  (with the port only when it is not 22), which needs no probe at all: the connection already knows.
  The commands go in a top toolbar like the other modules, and none of them is `Classes="accent"`,
  which those reserve for the one command that creates something.
- **The keyboard handler goes on the top level, not on the module.** Tunnelled and `handledEventsToo`,
  exactly as `ConsoleWindow` registers the guest's, or Tab moves focus to the toolbar and the arrow
  keys drive it. Registering it in `OnAttachedToVisualTree` and removing it in
  `OnDetachedFromVisualTree` is what scopes it to this module being on screen, since a `TabControl`
  detaches the content it is not showing; another module's Tab stays its own.
- **Font size is one setting for every terminal the app draws.** `Ctrl+wheel` lives in
  `TerminalControl` itself, checked before the alternate-screen guard so it works inside `vim` and
  `htop` too, and setting `FontSize` already relayouts and raises `TerminalResized`, so the far end
  learns its new geometry through the existing debounce. `AppSettings.TerminalFontSize` is read by
  both surfaces; the module's `A-`/`A+` buttons save at once and the wheel gesture is saved from
  `Deactivate`, which is why there is no debounce timer, and the write is guarded on the value having
  actually moved so a module switch does not rewrite `settings.json` for nothing.

## Conventions

- All wire structs follow the spice-html5 source byte-for-byte; when changing protocol code, check the matching `*.js` in `..\VmManager\VmManager\WebContent\src`.
- Pixels are BGRA end-to-end; do NOT copy spice-html5's BGRA→RGBA canvas swap.
- UI updates from channel threads go through `Dispatcher.UIThread`; framebuffer access is under `SpiceFramebuffer.SyncRoot`.
- Do not add a local `FontSize`/`Height` in a view to line controls up; fix the baseline in `Styles/JetBrainsClassic.axaml` instead (see above).
