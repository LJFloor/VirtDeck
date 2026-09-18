# Remote control

`RemoteControlModule` is the host's own X11 desktop, inline in the shell like the Terminal module,
scaled to fit, driven with this computer's mouse and keyboard: TeamViewer for the machine VirtDeck
is already connected to. Nothing is installed on the host. VirtDeck carries a static x11vnc and
uploads it the first time, VS Code server style, and RFB (the VNC protocol) travels on an SSH exec
channel's stdin and stdout: no port is opened, no VNC password exists, and the x11vnc process dies
with the channel.

The pieces, host side first: `native/x11vnc/` builds the agent, `RemoteDesktop/RemoteDesktopService`
finds the X displays and starts it, `Services/SshPipe` is the channel, `RemoteDesktop/RfbSession` is
the protocol, `Controls/RemoteDisplay` over `Controls/FramebufferView` draws it, and
`Input/X11Keysyms` is the keyboard.

## The agent

- **Two static binaries per architecture**, x11vnc 0.9.17 over LibVNCServer 0.9.15 and `vdrelay`,
  musl and fully static so one build runs on any distribution of that architecture. x86_64 and
  aarch64 are built; a host of any other architecture gets a stated answer and no tab content.
  `native/x11vnc/build.sh` builds both in Alpine containers (docker or podman, QEMU user emulation
  for arm64 on an x86 machine), from tarballs committed in `native/x11vnc/src/` and Alpine's own
  static packages. The results, `agent-x86_64.tar.gz` and `agent-aarch64.tar.gz`, are committed like
  `native/win-x64` and embedded in `VirtDeck.Core` on every platform, Windows included, because
  they run on the Linux host and not here. See `native/x11vnc/VERSIONS.txt`.
- **`vdrelay` exists because x11vnc's `-inetd` mode does `dup(0)` and then reads and writes that one
  descriptor.** That is right under inetd, where fd 0 is a socket, and wrong under sshd, which hands
  a command without a terminal two one-way pipes (checked: fd 0 is a read-only pipe and fd 1
  another one), so every reply would go to a read-only pipe. The relay makes a connected pair of
  sockets, gives x11vnc one end as fd 0, and pumps the other end to and from the pipes. It is the
  whole of the fix and keeps x11vnc unpatched. stderr is inherited, so x11vnc's log reaches the
  channel's stderr, which `SshPipe` drains into the tail the module quotes on failure.
- **The pair is TCP over loopback, not a socketpair.** x11vnc refuses a client whose address it
  cannot name (`check_access: denying empty host IP address string`), and naming a Unix socket's
  peer is something glibc's `getnameinfo` does and musl's does not: a stock glibc x11vnc behind a
  socketpair worked, the static musl one refused every connection. A loopback connection's peer is
  plainly 127.0.0.1, and TCP_NODELAY applies to it as well. The listener exists only between
  `listen` and `accept`, and the connection accepted must come from the relay's own connecting
  socket, so a local process racing for the port is refused rather than handed the stream.
- **Uploaded once per version, by hash.** `AgentBundle` derives everything from the embedded bytes:
  the cache directory is `~/.cache/virtdeck/agent-<12 hex of the tarball's sha256>/` in the login
  user's home, and the two binaries' own hashes are what the host checks. The upload is a plain tar
  (the host needs no gzip) through `RunPipeInAsync`, unpacked into a `mktemp -d` beside the final
  directory, checked, and moved into place, so a half-finished upload is never taken for a finished
  one. The directory is mode 700. Bundles untouched for 30 days are pruned and the one in use is
  `touch`ed on every start, so two PCs on different VirtDeck versions never delete each other's.
- **The start script re-checks the hashes before running anything**, which guards against
  corruption; it is not a security boundary, since whoever can write the login user's home already
  is that user, and that user has sudo.
- **The host's own x11vnc is never used.** It would have no relay to run under, and a relay of the
  right architecture is exactly what a host without a bundled build lacks.

## Finding the displays

One un-elevated round trip in the tagged-record idiom (`RemoteDesktopService.ProbeAsync`):

- **`x` per X server, found from its lock file.** `/tmp/.X<N>-lock` is world readable, holds the
  server's pid and exists for every X server, including those logind never hears of (startx from a
  console, Xvnc, Xvfb) and GDM's, whose command line carries `-displayfd` instead of the display
  number. The `-auth` file comes off that pid's `/proc/<pid>/cmdline`. **Only a process called
  X-something counts**: a Wayland compositor's X display is Xwayland, which shows the compositor's
  X clients and not the desktop, and current GNOME starts Xwayland on demand and holds the lock
  itself, so there the lock names `gnome-shell` (measured on this machine).
- **`s` per logind session**, as key=value pairs parsed by name, never by position: whose session a
  display is, whether it is on screen, which one is the login screen (`Class=greeter`), and which
  active session is Wayland, which is the answer to "why is there nothing to connect to" on a
  modern desktop.
- A display logind does not know about belongs to whoever runs its server, or to nobody (a root
  server with no session on it) and is listed as a bare X server.

The picker lists active sessions first, then the login user's own, then by display.

## Starting x11vnc

- **Whose rights.** The login user's own session runs x11vnc unelevated. Anybody else's session and
  the login screen run it through sudo, with the password handed over exactly as
  `RunPipeInAsync` does it (password, sentinel, then the payload). The login user's own session
  gets **one** elevated retry, and only when stderr says the display turned it away
  (`XOpenDisplay`, `Authorization required`), which is what a LightDM session looks like when the
  server's auth file is root's: the file explorer's one-shot root retry, in the same spirit.
- **The auth file**: the one the server was started with when this account can read it, else the
  session user's `~/.Xauthority`, else none. Not x11vnc's own `-auth guess`, whose search shells out
  to tools a modern host lacks and failed with a `netstat: not found` that read as nonsense; a
  display that wants a cookie it was not given says so plainly, and that plain refusal is what the
  elevated retry keys on. Every value the script uses (the agent directory, the display, the auth
  file, the user, the hashes) travels as a NUL-separated argv through `ShellScript.ArrayFrom`, never
  interpolated.
- **The flags**, each for a reason: `-inetd` (serve the channel), `-nopw` (SSH is the
  authentication), `-norc` (no host config changes our behaviour), `-shared`, `-noncache` (the
  client-side cache makes the framebuffer about ten times taller, which Fit would show), `-noxrecord`
  and `-nowf` (scroll and wireframe heuristics that draw artefacts), `-noprimary` (every mouse
  selection on the host must not overwrite this computer's clipboard), `-xkb` and `-add_keysyms`
  (keysyms the host's layout lacks still type), `-clear_mods` (no modifier left stuck by a previous
  viewer), `-xrandr newfbsize` (follow the host's resolution, see below), and **`-nonap`**. Without
  that last one x11vnc naps between screen polls whenever the viewer sends no input, which is its
  way of saving CPU on an idle desktop and exactly wrong for one being watched: measured against an
  animating display, the first picture arrived a second late and nothing else until the animation
  had stopped for a second; with it, updates arrived every 50 ms in step with the animation. A
  paused (hidden) module still costs little, since nothing is asked for.
- **Never a signal.** The session ends by closing x11vnc's stdin, which it takes as the viewer
  leaving and exits cleanly on. `SshPipe.Dispose` deliberately does not use
  `SshCommand.CancelAsync`, which asks sshd to signal the command: with it, x11vnc was found wedged
  on a futex for good after the session closed, still holding its X connection, while every
  EOF-only close tried since let it exit. vdrelay keeps to the same rule: x11vnc runs in its own process group, a signal to the
  relay ends the stream rather than being passed on, and anything still there five seconds after
  its stream ended gets SIGKILL.
- **Fifteen seconds for the banner.** A far end that never speaks (a sudo reading a wrong password,
  an x11vnc that cannot open its display) says why only on stderr, so a start that does not reach
  `RFB 003.008` in time, or fails the handshake, is reported with the telling lines of that tail
  (`RemoteDesktopConnection.Diagnosis`, x11vnc's timestamps taken off).

## The protocol

`RfbSession` is written from RFC 6143 and the community protocol description. noVNC was only a
behaviour cross-check and is not ported: it is MPL, and Core is proprietary.

- **Pixel format** 32 bpp, depth 24, little-endian, true colour, red at bit 16, green at 8, blue at
  0: every pixel arrives as B, G, R, which is the framebuffer's BGRA, so Raw rectangles are copied as
  they come. The convention is BGRA end to end, here as in SPICE.
- **Encodings**: Tight, CopyRect and Raw, plus DesktopSize, LastRect and RichCursor, at compress and
  quality level 6. Tight is fill, JPEG (`ImageDecoders.DecodeJpeg`, the SPICE decoder) or zlib
  through the copy, palette or gradient filter; TPIXEL is three bytes in R, G, B order. Out on
  purpose: **ExtendedDesktopSize**, because following the host's resolution is all DesktopSize is
  needed for and resizing somebody's real monitor is not ours to do; Extended Clipboard, unverified
  in x11vnc; ZRLE and Hextile, which x11vnc never needs with Tight on offer; the bell.
- **Tight's zlib streams are session-long.** The server keeps four deflaters and ends each
  rectangle's share with a sync flush, so every rectangle continues its stream. .NET has no public
  raw inflater, so each stream is a `ZLibStream` over a feed that each rectangle's bytes are
  appended to, read for exactly the size the rectangle decodes to. **The feed throws when asked for
  bytes it does not have** rather than returning 0: how `DeflateStream` treats an early end of stream
  is its own business, and a stream out of step with the server has to be a disconnect with a
  reason, never a wrong picture. The zlib streams are the reader thread's and are disposed by it.
- **Threads**: one reader thread does the handshake and decodes, like a SPICE channel, blitting each
  rectangle under `SyncRoot` after decoding it outside. The UI never writes: `RfbWriter` has its own
  thread, because a write on an SSH channel blocks while the window is full, which on a slow link is
  exactly when the user is dragging something. Pointer moves queued behind one with the same buttons
  replace it; a change of buttons or a key is never merged across.
- **Pacing and pause.** RFB is pull: the next update request goes out as soon as an update's header
  arrives, so one is always in flight. `Paused` stops asking, which is how a hidden module costs the
  host and the link nothing; libvncserver keeps collecting what changed, so resuming is an ordinary
  incremental request (a full one only after a resolution change).
- **Cut text** is Latin-1, the base protocol's; anything outside it goes as `?`.
- A handshake failure fails `Ready`, which the caller reports; a session that ends after it raises
  `Disconnected`, once, and never for a session this end closed.

## The display

`FramebufferView` is `SpiceDisplay`'s painting lifted into a base class both displays share: the
16 ms dirty-row pump, the framebuffer swap on the UI thread, the render, and the exactly-one-cursor
rule. What a subclass keeps is how input goes back. Both sessions implement `IFramebufferSource`.

- **One `ImageRect` answers where the picture goes, for the paint and for the pointer alike**, so a
  click lands on the pixel under it at any scale. `Unscaled` is the VM console's centred one pixel
  per DIP, exactly as it was (the console must not change by a pixel); `Fit` scales up or down,
  aspect kept; `Actual` is one remote pixel per **device** pixel, so 1:1 on a 150% screen really is
  1:1, and measures itself at that size so the module's `ScrollViewer` can scroll it. The picture is
  placed on a whole device pixel, and smoothed (`MediumQuality`) only when it is being resampled.
- **The pump follows the visual tree, and the bitmap survives it.** The shell takes a module's
  content out of the tree on every tab switch, and `SpiceDisplay` used to stop its pump for good and
  drop its bitmap on detach: right for a console window that is closing, fatal for a module that
  comes back. The base now stops on detach and restarts on attach with the bitmap kept;
  `SpiceDisplay` overrides detach to keep its full teardown.
- **RFB has no press and release**, only "the pointer is here with these buttons held", so
  `RemoteDisplay` keeps one button mask: left, middle, right, and the wheel as buttons 4 to 7, each
  notch a press and a release, a touchpad's fractions added up to whole notches. A press captures the
  pointer so a drag that leaves the control still ends where it was let go; a lost capture lets
  every button go.
- The cursor is RichCursor's, drawn by this computer at its own size whatever the scale, which is
  what TeamViewer does too; an empty one hides it.

## Keyboard

**RFB carries keysyms, so the local layout decides the character**, the opposite of the VM
console's scancodes (see `PhysicalKeyMap`): x11vnc is told "the user typed @" and works out which of
its keys, with which modifiers, produce that. It is the standard VNC behaviour and the only one that
survives a local and a remote layout that differ.

- `X11Keysyms.For`: keys that type nothing (Enter, arrows, function keys, modifiers, the keypad)
  from the physical key, left and right kept apart; otherwise the character Avalonia says was typed
  (`KeySymbol`); with Ctrl held that is a control code, so the key's own unshifted character, and
  x11vnc adds the Ctrl it already has. Latin-1 is its own keysym, the rest `0x01000000` plus the
  code point.
- **Right Alt is AltGr** (`ISO_Level3_Shift`), which is what it is on every layout that has one,
  US-International included; left Alt stays Alt. Windows reports AltGr as a left Ctrl and a right
  Alt arriving together, and the fake Ctrl is released before it reaches the far end, or every AltGr
  character would arrive as a shortcut.
- **Key up releases exactly what key down pressed**, remembered per physical key, whatever the
  modifiers did in between; a held key repeats as more key downs of the same keysym. Everything still
  held is let go when the display loses focus, the window loses activation or the module is switched
  away, so no key stays down on the host.
- **Dead keys and input methods**: a key that types nothing by itself sends nothing, and the
  character the platform composes arrives as text input and is typed as a press and a release. The
  key that completes a composition is not sent itself. This is the part most in need of checking on
  each platform and layout (US-International on Linux and Windows).
- Routing is Terminal's: tunnelled, handled-too, on the top level, added with the visual tree and
  gated on the display having focus, plus the VM console's X11 keyboard grab while the window is
  active, a session is live, the module is on screen and the pointer is over the picture, so Alt+Tab
  and Super reach the far end but the window's chrome still works. The Keys menu sends Ctrl+Alt+Del,
  Alt+Tab, Alt+F4 and Super for when there is no grab (Windows), and Type clipboard types text as
  keysyms, for the login screen's password box.

## The module

- **Hidden on a host with no X server, and not polled for.** `HostTools` gains an `x` record that
  asks `command -v` about Xorg, Xvfb, Xvnc and Xtigervnc and never runs them (a `--version` of an X
  server's wrapper starts an X server), carried as `HostToolset.HasXServer`; the module's
  `IsRelevant` is that. It answers false to `IModule.ReprobeWhileHidden`, because what hides it is a
  headless server, and waiting for one to grow an X server would keep a fully equipped server polled
  every four seconds for the life of the session. See "Which modules a host gets". A Wayland desktop
  with Xorg installed does show the tab, and says why it cannot attach.
- **Terminal's lifecycle.** Every entry without a live session looks at the host again, because
  whether anybody is logged in is exactly what changes while nobody watches. The first entry
  connects by itself **only to the login user's own desktop, on screen**; anyone else's session and
  the login screen are an explicit Connect. Coming back never disturbs a live session or revives an
  ended one. `Deactivate` pauses updates and lets go of keys, buttons and the grab; `Shutdown`
  disposes the session, whose channel ending is what ends x11vnc on the host.
- **Stated answers on the page**, most specific first: how the last session ended (with stderr's
  reason), the probe's failure, no agent for this architecture, a Wayland desktop, no X session at
  all, or "Choose a session and Connect".
- **Clipboard, text both ways**, the VM console's policy: the two-tier 500 ms poll of
  `HostClipboard` while the module is on screen and the window active, a forced read on connect and
  on the window coming back, and a remote copy taken as the new baseline so it does not bounce back.
- Fit is one setting for every host (`AppSettings.RemoteControlFit`): it is about this screen.
- Status: left what is connected, at what size and scale; right the agent's x11vnc version.

## What it does not do

Wayland sessions (nothing to attach to without the compositor's cooperation), resizing the host's
screen, one monitor at a time (the whole X screen is shown, all monitors), clipboard text beyond
Latin-1, images or files on the clipboard, and audio.
