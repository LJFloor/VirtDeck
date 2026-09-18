# Virtual machines: capabilities, media and the console

## Host capability checks

`VirshService` probes once, on the VM module's first activation (`LoadHostCapabilitiesAsync`), and caches the results.

| Property | Check | Used by |
|---|---|---|
| `VirtSparseAvailable` | `which virt-sparsify` | Export dialog: disables sparse checkbox |
| `CheckHostCapabilities()` | `/proc/cpuinfo` svm/vmx, `/dev/kvm`, `systemctl is-active libvirtd` | Status-bar indicators |

All default to available on SSH error, **except `VirtSparseAvailable`** which defaults false (safe: prevents a silent no-op export).

## Install media identification and device defaults

- **Reading the ISO:** `Services/IsoIdentifier` reads the ISO 9660 volume descriptors, which ECMA-119 fixes at **sector 16 (byte offset 32768)**, so identifying an image is a 16 KiB read at a known offset and never a download. Local files seek; server files get one `dd if=... bs=2048 skip=16 count=8 | base64`. It scans a few descriptors rather than assuming the Primary is first, because an El Torito boot record can precede it. UDF-only and floppy images have no PVD and answer **null**, which callers must treat as unknown rather than as any particular OS.
- **Matching:** `Services/GuestOsProfile.Match` runs the hand-curated `mediaPatterns` table from `Data/osinfo-labels.json` against the volume id, first match wins (ordered specific-first), which is largely what osinfo-db does too.
- **Windows media does not carry its version, and the files that would say are unreachable.** Windows 10 and 11 both ship `CCCOMA_X64FRE_EN-US_DV9`. `sources/install.wim` holds the build number, but a retail Windows ISO exposes **only `README.TXT`** through ISO 9660 and puts the real tree on the UDF side (verified against a 25H2 image: three entries in the ISO 9660 root). So modern Windows preselects **`win10`** with `osAssumed: true`, and `fileNamePatterns` refines it to `win11` or a Server release when the **file name** says so (Microsoft's tool names its images `Win11_24H2_English_x64.iso`). That refinement **only ever refines**: the volume id must have identified the family first, a file-name pattern is ignored unless its family agrees, and an exact volume-id match is never overridden, because a name is user-controlled and survives renaming while the descriptor is written at mastering time. Only the legacy releases have version-specific volume ids (`WXPVOL_EN`, `WIN98`) and preselect an exact OS. Patterns carry an `os` short id where the regex pins one (`ubuntu$1`, `debian$1`); the rest are family-only, which still gets the devices right.
- **The policy** is `Services/GuestDevices`: Linux gets `virtio`, BIOS-only guests get `ide` + `rtl8139`, and **everything else including unknown gets `sata` + `e1000e`**, which every installer has drivers for. Unknown deliberately lands on emulated devices: a VM that installs slowly beats one that cannot see its disk. `OsFamily.Unknown` is a real answer, not a missing one.
- **The OS dropdown is the source of truth**, not the ISO: `CreateVmWizard.GuestProfile()` reads the selection and falls back to the detected family only while it still says "generic". See the auto-fill rule in "Shared idioms" for `_osUserPicked`/`_nicsTouched`/`_disksTouched`.
- **The name box folds illegal characters as you type** (a domain name is `[a-zA-Z0-9_.-]+`), by the caret-preserving fold. `ValidateGeneralAsync` keeps the regex as the backstop that owns the rule, but only the empty name can now reach it.
- **Media pickers remember where you were:** `Services/MediaLocations` is the single entry point for every ISO/floppy picker and the one place the directory is stored. `AppSettings.LastLocalMediaDir` and `HostProfile.LastServerMediaDir` are **separate**, because a path on this PC means nothing to the host browser; the server one is **per host** for the same reason one step further, since a path on one host means nothing on another. The server methods take the `RemoteFileService` the caller already holds and read the host off its `ProfileKey`, so no call site passes a host. Only a confirmed pick is remembered. A caller with something better than history (the drive's current medium) passes it as the initial path; the server fallback is `/var/lib/libvirt/images`.
- **Page order follows the dependency:** install media sits directly under the name, *above* vCPUs/memory/OS type/firmware, because identifying the media fills the OS type in. The mode defaults to **stream from this PC**. The one thing below firmware is the Windows setup row, for the reason in "Unattended Windows setup" below.
- **The last page is a summary**, built fresh on every entry (`BuildSummary`) by reading the pages themselves, not the fields `FinishAsync` captures, so Back-and-change is reflected. Its **"Start the VM after creation"** checkbox is on by default and is the only thing that decides whether Finish calls `StartVmAsync`; `VmStarted` reports what happened, and the module opens the console only when it is true, because a defined-but-shut-off VM has no console.

## Export VM (`Views/ExportVmDialog.axaml.cs`)

Right-click -> Export VM -> a `.tar` containing `domain.xml` + `disks/`.

**Streaming pipeline** (no temp file for known-size disks):
```
SSH (sudo dd) -> Pipe.Writer -> KnownLengthStream -> TarWriter -> FileStream
```
`TarWriter` (PAX) requires the entry size in the header before data flows. `KnownLengthStream` wraps the pipe reader and reports a pre-measured `Length` while reading sequentially, satisfying TarWriter without buffering locally.

**Sparse before export**: optional checkbox (needs `libguestfs-tools`, VM must be off). Runs `virt-sparsify --in-place` per disk, streams its progress to the status label through `RunSudoCommandStreaming`, and the size is measured **after** so the tar header reflects the shrunken file. Disabled with a tooltip when the VM is running or the tool is absent.

The **save picker** is shown first (from `Opened`), so the config fetch and the virt-sparsify check complete while the user is browsing.

## Clipboard sharing

Text and images are mirrored both ways through the guest agent. **Files are not clipboard content here**: they go into the guest by dropping them on the console, over `VD_AGENT_FILE_XFER`.

**Typed, not text-only.** `MainChannel.GrabClipboard(types)` announces what the host offers, `SendClipboardData(type, bytes)` answers a request, and `SpiceSession` raises `ClipboardTextFromGuest` / `ClipboardImageFromGuest(type, bytes)` / `ClipboardRequestedByGuest(type)`. The client advertises **PNG + BMP** and always sends PNG (the Windows agent maps `CF_DIB` to either, the Linux agent maps all four, PNG is the one every agent implements). Incoming PNG/BMP/JPEG all decode through `Avalonia.Media.Imaging.Bitmap`, so there is no format switch on receive.

**A request that cannot be served must be answered with `VD_AGENT_CLIPBOARD_NONE`**, never with silence: the Windows agent blocks its paste for three seconds waiting for a reply.

**Large payloads changed two invariants in `MainChannel`.** `SendClipboardData` hands the encode and enqueue to a pool thread, because `EnqueueAgentMessage` blocks on the 8-deep send queue and the caller is the UI thread. `HandleIncomingAgentData` caps a reassembled message at `MaxAgentMessageBytes` (32 MiB); before images nothing on that path was bigger than a few KiB and the `u32` size field was simply trusted.

**The host poll is two-tier** (`ConsoleWindow.PollHostClipboardAsync`). Every 500 ms it asks only which formats are on offer (`GetDataFormatsAsync`: a TARGETS round trip on X11, `EnumClipboardFormats` on Windows), and pulls bytes only when that set changed or `_clipboardReadPending` is set. Reading a multi-megabyte bitmap out of the X11 selection twice a second is not an option. The cost: **two images copied in a row from the same app offer an identical format set and are not noticed**, which is why `Activated` forces a re-read.

**The guest-host-guest loop is broken by a re-read, not by the bytes sent** (`SeedAfterGuestWriteAsync`). The baseline has to be whatever the *next poll* will read, and that is not the bytes handed to `SetImageAsync`: the image goes on as a decoded `Bitmap` and comes back PNG-encoded by us, so hashing what the guest sent would not match and the guest would get its own image back.

**A `Bitmap` on the clipboard must outlive the call, and one read back is not yours to dispose.** Both halves crashed the app (`ObjectDisposedException` on `Ref<IBitmapImpl>` inside `Avalonia.X11.Selections.SelectionDataProvider`, on the X11 event loop, where no `catch` of ours can reach it), and a clipboard manager makes it immediate.
- Ownership is **lazy**: Avalonia produces the bytes by calling `Bitmap.Save` on the instance it was given, when something asks. So `HostClipboard.SetImageAsync` parks it in `_offered` and retires it only when a later set replaces it, instead of the obvious `using`. Retiring is safe because selection requests are served on the UI thread, the same thread that sets; it is deliberately **not** released when a console closes, since `_offered` is shared across consoles.
- A read while we still own the clipboard is **not a round trip**: `X11ClipboardImpl.TryGetDataAsync` returns the stored transfer unchanged when the selection owner is itself. `ReadImageAsync` therefore disposes only a bitmap that is not `_offered`.

**Why files are not pasted.** `VD_AGENT_CLIPBOARD_FILE_LIST` carries **paths, not bytes**: the guest resolves them against a WebDAV share the client would have to host (`dav://localhost:9843` via gvfs, `\\localhost@9843\DavWWWRoot` via the mini-redirector), which also needs an `org.spice-space.webdav.0` spiceport device and `spice-webdavd` in the guest. That was implemented and removed; drag-and-drop covers the same need with nothing to install. Guest-to-host file copy does not exist in this protocol either (vdagent x11.c: "we don't support file copying in this direction yet").

**Sharing is unconditional, so the console has no Clipboard menu at all.** The old per-VM "Share images" toggle gated only the last step while the costly parts ran regardless, so it bought nothing. `AppSettings.VmSettings.ClipboardImagesOff` stays in the model, written by nobody, so old settings files remain valid. The keystroke fallback **"Type clipboard"** lives under **Keyboard** and is **always there**, never hidden on agent state: a command that comes and goes with something the user cannot see reads as a bug.

## Cursor rule (the key requirement)

Exactly ONE cursor must be visible over the display: never zero, never two. `FramebufferView.ApplyCursor()` (the base `SpiceDisplay` shares with the Remote Control module's display) is the single chokepoint and just sets `Control.Cursor` (the OS never stacks cursors):
- ShowHostCursor policy -> the default arrow;
- before any cursor message -> the default arrow (no missing cursor at startup);
- guest hid the cursor -> `StandardCursorType.None`;
- otherwise -> the built SPICE cursor (or default if RESET).

The "Show host cursor" toggle (per-VM, in `AppSettings`) is the user's accepted fallback.

## Image compression / QUIC

QUIC and GLZ are not decoded. The client decodes BITMAP + LZ_RGB + JPEG and steers the server **away** from QUIC/GLZ at runtime: `DisplayChannel` advertises `DISPLAY_CAP_PREF_COMPRESSION` and sends `MSGC_DISPLAY_PREFERRED_COMPRESSION` = **LZ** right after `DISPLAY_INIT` (GLZ is also disabled via `glz_dictionary_window_size=0`). So unmodified VMs render with **no per-VM `<image compression>` change or restart**. `SpiceSession.SetPreferredCompression` can send the message live, but nothing in the UI calls it; the console's old Display radio pair is gone. Porting QUIC/GLZ is only needed to ride the server's native `auto_glz` for bandwidth, not for correctness.

## Removable media (ISO / floppy)

Optical (`.iso`, `device='cdrom'`) and floppy (`device='floppy'` on the `fdc` bus, target `fda`) share one pipeline. Each can be a **file on the server** or **streamed from this PC**: the local file is served by `Services/NbdServer` (a native C# NBD fixed-newstyle server, file-agnostic) over an SSH reverse-forward, and QEMU pulls it through its built-in NBD client from a `<disk type='network' protocol='nbd'>` element (`VirshService.BuildNetworkMediaXml`). NBD is always compiled into QEMU, so streaming needs **no host package**. ISO is exported read-only; **floppy is exported read-write, so guest writes persist back to the local file**. Change/eject reuse `virsh change-media`/`--eject` (generic by target).

Streaming surfaces: the Create-VM wizard (a single **install-media** picker, ISO to CD-ROM and floppy image to floppy, classified in `BuildInstallMediaOpAsync` and added to the boot order), the editor's disk context menu, and the console's **CD/DVD** and **Floppy** toolbar dropdowns. The console's Floppy button is hidden unless the VM has a floppy drive, and the editor's boot-order list includes **Floppy** (`<boot dev='fd'/>`). Caveat: the `fdc` controller is native on `i440fx` (the BIOS-only XP F6-driver case) but may be unavailable on `q35`/UEFI.

**A floppy is identified by size, not by extension** (`Services/FloppyImage`). QEMU never reads the extension (libvirt attaches the image as `<driver type='raw'/>`), so `.vfd`, `.ima` and `.flp` are the same bytes under different conventions. **`.img` cannot be read off the name**: it is equally the convention for raw hard-disk images and hybrid ISOs, yet FreeDOS ships its install sets as `.img`. The tie-break is the file size against the standard PC geometries (160K through 2.88M), which is how QEMU itself picks the geometry (`fd_formats` in `hw/block/fdc.c`), so the client agrees with the thing that has to accept the image. An `.img` of any other size stays **unclassified** rather than guessed at, and "All files" is the escape hatch for an exotic geometry. Settling an `.img` on the server costs a `stat` over SSH, so `CreateVmWizard` resolves it in the background detect pass and caches per path; drops are always local, so `DropFiles` can stat inline.

**Console drops:** any file is sent into the guest with `SpiceSession.SendFile` (the vdagent file-transfer channel), and the status bar shows one shared progress bar and Cancel for the whole drop, refcounted in `_activeXfers` and driven **only** by the session's file events, because `SendFile` returns silently for a missing file or a departed agent. A single ISO or floppy image dropped on a VM with the matching drive **always asks** (`MessageDialog.Choose`) whether to insert or send; with no guest agent the send button is disabled with a reason rather than the drop silently mounting. Nothing is ever mounted unconfirmed, and a shut-off VM is a valid drop target (the media change goes straight to the saved config).

