# VirtDeck remote control agent: our own, in C#

Replace the GPL x11vnc plus vdrelay host agent with our own static NativeAOT C# agent that speaks
the X11 wire protocol on one side and RFB on the other. X11 only, no clipboard, same RFB wire, so
`RfbSession` and everything above it is untouched.

**Status: phases 0 to 5, 7 and 8 are done and the work is in the tree** (see `docs/remote-control.md`, which
is now the description rather than the plan). What is left is phase 9, the manual verification
matrix, which needs real hosts, and phase 6, the JPEG encoder, which was left out on purpose.
Delete this file once those are settled.

---

## Why, and what the spike proved

Measured on 2026-09-18, 5120x1440, NVIDIA proprietary driver, from a throwaway C# program that
spoke X11 straight off the socket (no libX11, no P/Invoke at all):

| Measurement | Result |
|---|---|
| GetImage, full 5120x1440 (28 MiB) | 8.6 ms, 3.2 GiB/s over the unix socket |
| GetImage, 1920x1080 region | 2.5 ms |
| Tile diff of a whole frame (`Span.SequenceEqual`) | 7.6 ms, 3.7 GiB/s |
| DamageNotify granularity on a compositing desktop | **always the full screen**, 75 to 97 per second |
| Keysym lookup from the core keymap | works, 10 keysyms per keycode, 248 keycodes |
| `System.IO.Compression` on real screen content | 8:1, no host libz |
| Static musl NativeAOT binary, x64 | 2,293,776 bytes, static-pie, stripped, 7.7 s to build |
| The same binary run on this glibc host | works, same speed as JIT |
| aarch64 cross publish, no emulation | 2,325,504 bytes, 4.25 s |

Four design conclusions fall out of that:

1. **No MIT-SHM.** Plain GetImage over the socket is fast enough by a wide margin, which removes
   `shmget`/`shmat` and with them the only native interop the agent would have needed. The agent
   P/Invokes nothing, not even libc.
2. **Damage is a wakeup, not a hint.** A compositing desktop damages the whole root every frame,
   so dirty regions must come from a tile compare, exactly as x11vnc does it. Damage is only used
   to avoid polling an idle screen.
3. **No XKB.** The core protocol's `GetKeyboardMapping` and `GetModifierMapping` carry the keysym
   search, so the XKB extension is never touched.
4. **arm64 cross-compiles natively.** No QEMU, no binfmt, and the build is faster than today's
   emulated C build.

What we gain: x11vnc and LibVNCServer (GPL-2.0) leave the tree, along with `x11vnc-src.zip`,
`x11vnc-src.tar.gz`, 3.3 MB of committed upstream tarballs and 14 of the 17 licence files; vdrelay
and the whole `-inetd`, loopback-pair, musl `getnameinfo` workaround go with them; one binary
replaces two; and pacing becomes ours rather than x11vnc's `-nonap` heuristics.

What we lose: clipboard text in both directions (deliberate, see phase 8), pseudocolour and depth
16 displays (stated answer instead), and x11vnc's long tail of odd-host handling.

---

## What does not change

Scope discipline: none of this is touched.

- `RfbSession`, `TightDecoder`, `TightInflater`, `RfbWriter`, `RfbInput`.
- `RemoteDisplay`, `FramebufferView`, `X11Keysyms`, the module, the picker, the Keys menu.
- `RemoteDesktopService.ProbeAsync` and its tagged-record probe.
- `AgentBundle`, the upload by hash, the cache directory, the 30 day prune, the 15 s banner
  timeout, `Diagnosis()`, the elevated retry, `SshPipe`.

One consequence worth writing down now: the agent's stderr wording must be quotable by
`RemoteDesktopConnection.Excerpt()`, which keeps lines containing "fail", "error", "cannot",
"can't", "unable", "denied", "authoriz", "sorry" or "not found". Write the agent's fatal messages
in those words.

---

## Layout

```
native/agent/
  VirtDeck.Agent/
    VirtDeck.Agent.csproj      net10.0, AOT properties only on publish
    Program.cs                 argv, fd 0 and fd 1, lifecycle, fatal messages
    X11/
      XAuth.cs                 .Xauthority, MIT-MAGIC-COOKIE-1
      XConnection.cs           socket, setup, request writer, reply/event/error demultiplexer
      XExtensions.cs           QueryExtension plus per extension QueryVersion
      XCapture.cs              GetImage, tile diff, dirty rectangle coalescing
      XCursor.cs               XFIXES cursor image and position
      XInput.cs                XTEST, keysym to keycode, spare keycode remapping
      XRandr.cs                screen change notifications
    Rfb/
      RfbServer.cs             handshake, client messages, update loop
      TightEncoder.cs          fill and zlib (JPEG is phase 6, not written)
  build.sh                     replaces native/x11vnc/build.sh
  build-in-alpine.sh
  VERSIONS.txt
  PLAN.md                      this file
```

`RfbProtocol.cs` (message numbers, encoding numbers, the Tight control byte, the pixel format) is
written once in `VirtDeck.Core/RemoteDesktop/` and linked into the agent with `<Compile Include>`,
so the two ends cannot drift.

**The agent joins `VirtDeck.sln`** but is never referenced by the app and never publishes with it.
A normal `dotnet build VirtDeck.sln` compiles it as ordinary IL, which keeps the no-regression gate
meaningful and stops it rotting; only `build.sh` runs the AOT publish, so no developer needs the
AOT toolchain.

---

## Milestone 1: first light (a picture, no input)

### Phase 0: scaffolding and the debug loop

- [x] The project, in the solution, building as IL.
- [x] `--selftest`: connect to `$DISPLAY`, print setup, extensions, geometry, exit. The spike code,
      cleaned up.
- [x] **A local debug path in `RemoteDesktopService`**: a setting or environment variable that makes
      it run the agent as a local process over stdin and stdout instead of opening an `SshPipe`.
      This is the single biggest productivity lever in the whole project. It turns every iteration
      into F5 against `:0` with a debugger attached, instead of an upload over SSH with stderr as
      the only window.

### Phase 1: the X11 connection

- [x] `XAuth`: parse `.Xauthority` (big-endian blocks), match on display number and family, fall
      back to no authentication.
- [x] `XConnection`: unix socket, setup request and reply, resource id base and mask, screen
      geometry, root window, image byte order.
- [x] The demultiplexer. X is asynchronous: replies, events and errors interleave on one socket and
      an error is tagged with the sequence number of the request that failed. One reader thread,
      sequence tracking, a pending-reply map. Get this right once; everything else sits on it.
- [x] `XExtensions`: QueryExtension plus **QueryVersion per extension**. The spike hit `BadRequest`
      from `DamageCreate` for exactly this reason. Needed for DAMAGE, XFIXES, XTEST, RANDR.
- [x] Fatal paths: a vanished X server, a refused cookie, a depth we do not support. Each exits
      cleanly with a message the module can quote, never an unhandled exception.
- [x] Require depth 24 or 32, true colour, BGRA. Anything else is a stated answer and a clean exit.

### Phase 2: capture

- [x] Full-frame GetImage into a reusable buffer, allocated once, never in the loop.
- [x] 64x64 tile compare against the previous frame with `Span.SequenceEqual`.
- [x] Coalesce dirty tiles into rectangles: runs along a row, then merge vertically adjacent runs,
      so the encoder gets a few large rectangles rather than hundreds of tiles.
- [x] Damage as a wakeup only: `DamageCreate` on the root, and when no damage has arrived and no
      update is outstanding, do not poll at all. This is the `-nonap` lesson stated positively: we
      never sleep while an update request is pending, and we never poll when none is.

### Phase 3: the RFB server subset

Only what our own client decodes; there is no other client to serve.

- [x] Handshake: `RFB 003.008`, security type None (SSH is the authentication), ServerInit with
      32 bpp, depth 24, little-endian, true colour, red 16, green 8, blue 0, which is the
      framebuffer's BGRA and what `RfbSession` expects byte for byte.
- [x] Client messages: SetPixelFormat (ours only; a change is refused with a stated reason),
      SetEncodings, FramebufferUpdateRequest, KeyEvent, PointerEvent, ClientCutText (read and
      discard).
- [x] Server messages: FramebufferUpdate carrying Raw and Tight, plus the DesktopSize, RichCursor
      and LastRect pseudo-rectangles.
- [x] Tight: fill for a single-colour rectangle, zlib-copy otherwise, **resetting the stream per
      rectangle**. The client honours the reset bits (`TightDecoder.cs:29`), so the encoder never
      needs a `Z_SYNC_FLUSH`, which is the one thing .NET's compression API makes awkward. Costs
      two bytes and a little ratio.
- [x] Incremental and non-incremental update requests; a full update after a resolution change.

**Milestone 1 is done when** the module shows the host's desktop, live, at the right size, and
survives being paused, resumed and disconnected.

---

## Milestone 2: usable (input)

### Phase 4: pointer and keyboard

- [x] XTEST `FakeInput`: absolute motion, buttons 1 to 7 (the wheel is buttons 4 to 7, each notch a
      press and a release, which `RemoteDisplay` already sends that way).
- [x] Keysym to keycode: search `GetKeyboardMapping` for a (keycode, level) producing the keysym,
      work out which modifiers that level needs from `GetModifierMapping`, fake them around the
      key, put them back. Proven in the spike: `a` at 38/0, `@` at 11/1, euro at 14/4, F1 at 67/0.
- [x] Spare keycode remapping for keysyms the layout lacks, which is the `-add_keysyms` equivalent.
      Not exotic: `é` is already absent on a US layout. `ChangeKeyboardMapping` into an unused
      keycode, use it, restore it. Never leave a remapped keycode behind, on any exit path.
- [x] Release everything on disconnect and on stdin closing: keys, buttons and any modifier the
      agent faked. The `-clear_mods` equivalent.
- [x] Watch for a keymap change under us (the user, or another tool, remapping while connected) and
      re-read the mapping rather than typing nonsense.

**Milestone 2 is done when** typing and clicking work on a real desktop, including AltGr characters
and a character the host layout does not have.

---

## Milestone 3: complete

### Phase 5: cursor and resize

- [x] XFIXES: `SelectCursorInput` for CursorNotify, `GetCursorImage` into a RichCursor rectangle
      (ARGB with a hotspot). An empty cursor hides it, which the client already handles.
- [x] Cursor position: the spike showed the hardware cursor is not in the root image, so position
      comes from `QueryPointer` alongside motion, not from the framebuffer.
- [x] RANDR: `SelectInput` for ScreenChangeNotify, re-read geometry, reallocate the buffers, send
      DesktopSize and then a full update. The client already handles the resize.

### Phase 6: encoding quality (NOT DONE)

The encoder ships zlib only. That is fine for a desktop and bandwidth-hungry for full-screen video,
which is what the client's "What it does not do" now says. Left deliberately, not forgotten.

- [ ] Baseline JPEG encoder, chosen per rectangle by a cheap heuristic (distinct colours in a
      sample), roughly quality 6 to match what the client asks for.
- [ ] Measure against x11vnc on the same desktop and link before calling it done. Bandwidth and
      latency, not vibes.

---

## Milestone 4: the swap

### Phase 7: the build

- [x] `native/agent/build.sh` and `build-in-alpine.sh`, same shape as today: Alpine container,
      tarball per architecture, committed, embedded in `VirtDeck.Core`.
- [x] x64: `-p:PublishAot=true -p:StaticExecutable=true -p:InvariantGlobalization=true
      -p:UseSystemResourceKeys=true -p:StripSymbols=true` with `apk add clang lld build-base`.
- [x] aarch64, cross, no emulation. Two gotchas found in the spike, both worth a comment in the
      script:
      - apk-tools 3 **ignores a copied `repositories` file**, so the sysroot install needs the
        repository passed with `-X`:
        `apk --root /sysroot --arch aarch64 --initdb --allow-untrusted -X <main> -X <community> add musl-dev gcc zlib-static`
      - `-p:LinkerFlavor=lld` is required, because the default `-fuse-ld=bfd` in that image is
        x86-only, and `-p:ObjCopyName=llvm-objcopy` for the strip.
      - `-p:SysRoot=/sysroot -p:CppCompilerAndLinker=clang` for the rest.
- [x] `AgentBundle`: one binary instead of two, so two hashes become one.
- [x] A short `VERSIONS.txt`: our own source, one dependency (the .NET runtime, MIT).

### Phase 8: wiring, deletion and documentation

- [x] `RemoteDesktopService.OpenAsync`: the start script loses every x11vnc flag and vdrelay, and
      execs the agent with the display and auth file as a NUL-separated argv, exactly as now.
- [x] Delete `native/x11vnc/` entirely: the agent tarballs, `vdrelay.c`, `src/`, `licenses/`,
      `SOURCES.sha256`, `packages-*.txt`.
- [x] Packaging: drop `x11vnc-src.zip` from `.github/workflows/publish.yml`, drop
      `x11vnc-src.tar.gz` from `packaging/build-appimage.sh`, update `packaging/README.md`.
- [x] `THIRD-PARTY-NOTICES.md`: remove the Remote Control host agent section.
- [x] Clipboard out of the module: the two-tier 500 ms `HostClipboard` poll, the forced read on
      connect and on window activation, the remote-copy baseline. **Keep Type clipboard** in the
      Keys menu, which is client-side and still types a password into a greeter.
- [x] Status bar right slot: the agent's version instead of x11vnc's.
- [x] `docs/remote-control.md` rewritten: the agent section, "The protocol" now describing both
      ends as ours, and "What it does not do" gains clipboard text.
- [x] `docs/build-and-packaging.md` and `CLAUDE.md` adjusted where they name x11vnc.

### Phase 9: verification

No tests in this repo, so the matrix is manual and this is it:

- [ ] This machine's own X session (compositing, NVIDIA).
- [ ] A server-ish Xorg with no compositor.
- [ ] Xvfb, for the no-GPU path.
- [ ] A greeter (LightDM or GDM), which is the elevated path.
- [ ] Another user's session, which is the sudo path.
- [ ] The login user's own session with a root-owned auth file, which is the one-shot elevated
      retry.
- [ ] Layouts: US, US-International, NL. AltGr characters, dead keys, function keys,
      Ctrl+Alt+Del, Alt+Tab, Super.
- [ ] A resolution change while connected.
- [ ] Both architectures on real hardware.
- [ ] Pause and resume by switching tabs; disconnect by switching host; the host's X server dying
      under a live session.

---

## Sizing

Roughly 2,400 lines: XConnection 300, XAuth 80, capture 250, cursor 120, input 400, randr 60,
RFB server 400, Tight 200, JPEG 400, Program 150. The input phase is the largest and the one most
likely to be underestimated; everything else is known work.
