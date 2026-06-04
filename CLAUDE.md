# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Build

```bash
dotnet build SpiceVmManager.sln
```

Two-project solution (.NET 8, `net8.0-windows`):
- **SpiceClient** — a native, dependency-light SPICE protocol client library (no WinForms; uses `System.Drawing` for `Bitmap`/JPEG only).
- **VmManager.App** — the WinForms desktop app (`WinExe`, assembly name `VmManager`). References SpiceClient + SSH.NET.

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

### VmManager.App (WinForms)
- `Services/` — `SshConnectionManager`, `SshPortForwarder`, `VirshService` (ported verbatim from the old app; sudo via stdin + marker). `VirshService.DisableImageCompressionAsync` is the QUIC fallback (`virt-xml … --graphics image_compression=off`).
- `Controls/SpiceDisplayControl` — custom `Control`: paints the framebuffer (1:1 `DrawImage`, double-buffered, ~60 Hz dirty-rect repaint timer), forwards mouse, and **owns all cursor assignment** (the exactly-one-cursor state machine).
- `Input/WinFormsKeyMap` — VK → AT set-1 scancode. **Extended keys are `0xE0 | (atCode << 8)`** (e.g. PageUp = `0x49E0`), matching spice-html5 utils.js — NOT `0xE0XX`. Key-up high bit applied in `InputsChannel.SendKey`.
- `Interop/CursorInterop` — builds a native `Cursor` (alpha + hotspot) via `CreateIconIndirect`; caller must `DestroyIcon` the HICON on replace/dispose.
- `Forms/` — `LoginForm`, `VmListForm`, `VmConsoleForm` (low-level WH_KEYBOARD_LL hook → `InputsChannel.SendKey`; Power/Keyboard/Mouse/Display dropdowns; Fit Window).

## Cursor rule (the key requirement)

Exactly ONE cursor must be visible over the display — never zero, never two. `SpiceDisplayControl.ApplyCursor()` is the single chokepoint and just sets `Control.Cursor` (the OS never stacks cursors):
- ShowHostCursor policy → `Cursors.Default`;
- before any cursor message → `Cursors.Default` (no missing cursor at startup);
- guest hid the cursor → a transparent blank cursor;
- otherwise → the built SPICE cursor (or default if RESET).

The "Show host cursor" toggle (per-VM, in `HKCU\SOFTWARE\VmManager`) is the user's accepted fallback.

## Image compression / QUIC

QUIC and GLZ are not yet decoded. GLZ is disabled via DISPLAY_INIT (`glz_dictionary_window_size=0`). BITMAP + LZ_RGB + JPEG cover most VMs; if a VM's server still sends QUIC the screen won't render — use the console's **Display ▸ Force raw bitmaps** (sets `image compression='off'`, needs a VM restart) or port QUIC from `quic.js`.

## Conventions

- Forms use manual layout in `*.Designer.cs` (no VS designer/resx), keeping the old WinForms style (`ListView`/`ToolStrip`/`StatusStrip`).
- All wire structs follow the spice-html5 source byte-for-byte; when changing protocol code, check the matching `*.js` in `..\VmManager\VmManager\WebContent\src`.
- Pixels are BGRA end-to-end (matches `Format32bppArgb`) — do NOT copy spice-html5's BGRA→RGBA canvas swap.
- UI updates from channel threads go through `BeginInvoke`; framebuffer access is under `SpiceFramebuffer.SyncRoot`.
