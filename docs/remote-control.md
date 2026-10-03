# Remote control

`RemoteControlModule` is the host's own X11 desktop, inline in the shell like the Terminal module,
scaled to fit, driven with this computer's mouse and keyboard: TeamViewer for the machine VirtDeck
is already connected to. Nothing is installed on the host. VirtDeck carries its own agent and
uploads it the first time, VS Code server style, and RFB (the VNC protocol) travels on an SSH exec
channel's stdin and stdout: no port is opened, no VNC password exists, and the agent dies with the
channel.

**Both ends are ours.** `virtdeck-agent` is an X11 client on one side and an RFB server on the
other, written in C# and published with NativeAOT as one fully static musl binary per architecture.
It replaced a bundled x11vnc, which was GPL, needed a relay process to work under sshd at all, and
had to be built under emulation for arm64.

The pieces, host side first: `native/agent/` is the agent, `RemoteDesktop/RemoteDesktopService`
finds the X displays and starts it, `Services/SshPipe` is the channel, `RemoteDesktop/RfbSession` is
the protocol, `Controls/RemoteDisplay` over `Controls/FramebufferView` draws it, and
`Input/X11Keysyms` is the keyboard.

## The agent

`native/agent/VirtDeck.Agent` is a console program: argv in, RFB on stdout, X11 on a unix socket,
and everything fatal on stderr.

- **It speaks the X11 protocol on the wire**, not through libX11. A fully static musl binary has no
  working `dlopen`, so a libX11 the agent linked could not be one file that runs on any
  distribution; going to the wire instead means the agent **P/Invokes nothing at all, not even
  libc**, and carries no X client libraries and none of their licences. What it needs of the
  protocol is small: the setup handshake with MIT-MAGIC-COOKIE-1 (`X11/XAuth.cs` parses
  `.Xauthority` itself), GetImage, the keyboard mapping requests, XTEST, and the DAMAGE, XFIXES and
  RANDR extensions.
- **One binary per architecture, about 2.1 MB, 1.0 MB in its tarball.** `native/agent/build.sh`
  publishes both in one x86_64 Alpine container: ILC cross-compiles aarch64 by itself, so there is
  no QEMU and no binfmt, which is a strict improvement on the emulated build it replaced. See
  "The Remote Control host agent" in [build-and-packaging.md](build-and-packaging.md).
- **`XConnection` is a demultiplexer.** X is asynchronous: replies, events and errors arrive
  interleaved on one socket and an error names the sequence number of the request that caused it.
  Every request takes the next sequence number under the write lock, reply-bearing ones register
  themselves before the bytes go out, and one reader thread hands each 32 byte packet to whoever is
  waiting or to the event handlers.
- **An event handler must never make an X request.** It runs on the reader thread, which is the only
  thread that could deliver the reply, so the request waits for something nobody is left to read.
  This is not hypothetical: it is how the keyboard first hung, on a MappingNotify that arrived
  moments after the first keystroke. `XConnection.Request` now throws when called from that thread,
  so the same mistake is an error with a message rather than a session that goes quiet.
- **The clipboard is CLIPBOARD, text only, and owned rather than polled** (`X11/XClipboard.cs`).
  Text from the viewer is claimed with `SetSelectionOwner` on an unmapped InputOnly window and
  served as TARGETS, UTF8_STRING, TEXT or STRING. Large text is written onto the asker's window as
  a Replace and then Appends of one request each before it is told, so sending needs no INCR. The
  host's copies are seen through XFIXES selection events, fetched as UTF8_STRING (STRING if not),
  and read piece by piece when the owner answers INCR, as GTK and xclip do for large text. Text
  equal to the last that crossed either way is dropped, which is what keeps a host clipboard
  manager re-owning our text from echoing it back. Its own `x11-clipboard` thread makes the
  requests, under the rule below. The selection dies with the agent, so text pasted from this
  computer is gone from the host when the session ends unless a clipboard manager kept it.
- **Uploaded once per version, by hash.** `AgentBundle` derives everything from the embedded bytes:
  the cache directory is `~/.cache/virtdeck/agent-<12 hex of the tarball's sha256>/` in the login
  user's home, and the binary's own hash is what the host checks. The upload is a plain tar (the
  host needs no gzip) through `RunPipeInAsync`, unpacked into a `mktemp -d` beside the final
  directory, checked, and moved into place, so a half-finished upload is never taken for a finished
  one. The directory is mode 700. Bundles untouched for 30 days are pruned and the one in use is
  `touch`ed on every start, so two PCs on different VirtDeck versions never delete each other's.
- **The start script re-checks the hash before running anything**, which guards against corruption;
  it is not a security boundary, since whoever can write the login user's home already is that
  user, and that user has sudo.
- **No relay process.** x11vnc needed one because its `-inetd` mode does `dup(0)` and then reads and
  writes that one descriptor, which is wrong under sshd's two one-way pipes. The agent reads fd 0
  and writes fd 1 because that is what it was written to do, so the relay, the loopback socket pair
  it needed, and the musl `getnameinfo` refusal underneath it are all gone.
- **`--selftest`** prints what the agent can see on a display (the server, the geometry, the depth,
  which extensions are there, the screen sizes RANDR will take, and what a capture costs) and exits. It is the first thing to run on a
  host that will not show its desktop. `VIRTDECK_AGENT_TRACE=1` puts a timestamped trace of the
  update loop on stderr.

## Developing it

`VIRTDECK_LOCAL_AGENT=/path/to/virtdeck-agent` makes `RemoteDesktopService` run the agent **on this
machine**, over an ordinary pipe against `$DISPLAY`, instead of opening an SSH channel (see
`RemoteDesktop/LocalAgentPipe.cs`). The host is not touched: no upload, no cache, no sudo.
Everything above the pipe is the code that ships, so the module, the session, the decoder and the
display are all exercised, under a debugger, on one machine. The agent runs on a host over an SSH
exec channel with stderr as its only voice; this is the difference between writing it and
suffering it.

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

The same round trip asks two more things, which are the answer to "there is nothing here, now
what": a `t` record per tool a desktop of our own would need (`Xvfb`, `xauth`, `dbus-run-session`),
and a `d` record per desktop the host offers, read the way a display manager reads them (the first
`Name=` and `Exec=` of every `.desktop` in the two `xsessions` directories). Hidden and `NoDisplay`
entries are skipped, so is one whose program is not on the host any more, and the same desktop
offered from both directories is listed once. Both fields are unbounded and there are two of them,
so each travels base64'd rather than raw.

## A desktop of our own

**New desktop** starts one on a host that has none (`RemoteDesktopService.StartVirtualDesktopAsync`),
and then connects to it, because starting a desktop and then being asked to pick it out of a list is
a gesture with no outcome.

- **Xvfb, not Xvnc and not Xorg.** Xvnc and Xtigervnc bring their own RFB server, which is the one
  thing this module already has; Xorg on a spare VT takes a seat, needs root, and is a different
  feature. What Xvfb gives is a private desktop that nothing on the host's monitor shows, which is
  exactly right for a headless server and is not the console.
- **Unelevated, and there is nothing to log in to.** SSH already said who the user is, so the
  server and the desktop both run as the login user. The display is the first free one from `:9` to
  `:20`.
- **`-screen 0 3840x2160x24` is a ceiling, not a resolution.** An Xvfb's screen size is its RANDR
  *maximum*: the screen can be set to anything at or below it and nothing can push it above, which
  is why the number is 4K and why it is not a setting. The cost is the framebuffer, about 33 MB of
  the host's memory, whatever size the desktop is really being shown at. The desktop starts at that
  size and is made the size of the window before the first picture is sent.
- **It outlives everything.** Server and desktop are `setsid`'d away from the channel with their
  output in `~/.cache/virtdeck/x11/display<N>.log`, and Xvfb gets `-noreset` so the last client
  leaving does not take the display with it. Disconnecting, switching host and closing VirtDeck all
  leave it running; the next probe finds it by its lock file like any other X server.
- **Its own credentials.** A cookie out of `/dev/urandom` goes into
  `~/.cache/virtdeck/x11/display<N>.auth` through `xauth`, and the file is created empty and 0600
  first, because `xauth` would make it 0644 on the way to holding what is effectively the display's
  password. The server is `-nolisten tcp`.
- **The auth path is the marker.** A server whose `-auth` sits under `~/.cache/virtdeck/x11/` is
  ours, and is listed as a virtual desktop rather than a bare X server. That is what **End desktop**
  is offered for and nothing else, and the end script re-checks it against `/proc/<pid>/cmdline` on
  the host before it signals anything: a display number is a number, and numbers are reused. Ending
  it kills the X server, which is what ends the desktop, since its clients cannot outlive it.
- **It is the size of the window, and it follows it.** The module asks for the room the picture has
  in this screen's real pixels, so a virtual desktop is drawn one host pixel to one of ours at any
  scaling, and **Fit to window** has nothing left to do and is taken away while one is on screen.
  The size goes out before the session starts, in the handshake, so the first frame is already the
  right one rather than a 4K frame of a desktop about to shrink; later ones wait 400 ms for a drag
  of the window's edge to settle. A desktop somebody else's session is running is never asked for
  anything. See "Asking for a size".
- **The desktop's command line** comes from the host's own `.desktop` file and goes back to the host
  as an argv member, run by `sh -c` under `dbus-run-session` where there is one, with `DISPLAY`,
  `XAUTHORITY`, `XDG_SESSION_TYPE` and an `XDG_RUNTIME_DIR` if the login did not already set one. A
  host that offers no desktop entry at all can still be given a bare X server to run one thing in.
- **A host that cannot be given one keeps the button** and says why on hover
  (`RemoteDesktopProbe.StartBlockedReason`): no Xvfb, or no xauth. Absent tooling is a stated
  answer, and a disabled control states its reason on hover, so the button sits in a `Border` that
  carries the tip.

## Starting the agent

- **Whose rights.** The login user's own session runs the agent unelevated. Anybody else's session
  and the login screen run it through sudo, with the password handed over exactly as
  `RunPipeInAsync` does it (password, sentinel, then the payload). The login user's own session
  gets **one** elevated retry, and only when stderr says the display turned it away, which is what
  a LightDM session looks like when the server's auth file is root's: the file explorer's one-shot
  root retry, in the same spirit.
- **The auth file**: the one the server was started with when this account can read it, else the
  session user's `~/.Xauthority`, else none. Nothing guesses: a display that wants a cookie it was
  not given says so plainly, and that plain refusal is what the elevated retry keys on. Every value
  the script uses (the agent directory, the display, the auth file, the user, the hash) travels as a
  NUL-separated argv through `ShellScript.ArrayFrom`, never interpolated.
- **The arguments** are `--display` and, when there is one, `--auth`. There is nothing else to pass:
  the flags x11vnc needed to stop it doing the wrong thing (no nap, no scroll heuristics, no client
  cache, no PRIMARY selection, no stuck modifiers) are not flags here, they are how the agent
  behaves.
- **Never a signal.** The session ends by closing the agent's stdin, which it takes as the viewer
  leaving and exits cleanly on. `SshPipe.Dispose` deliberately does not use `SshCommand.CancelAsync`,
  which asks sshd to signal the command; every EOF-only close tried has let the far end exit.
- **Fifteen seconds for the banner.** A far end that never speaks (a sudo reading a wrong password,
  an agent that cannot open its display) says why only on stderr, so a start that does not reach
  `RFB 003.008` in time, or fails the handshake, is reported with the telling lines of that tail
  (`RemoteDesktopConnection.Diagnosis`). **The agent's own fatal messages are worded for that
  filter**, which keeps lines saying "cannot", "failed", "denied", "authoriz" and the like.

## The protocol

Both ends are ours: `RfbSession` here, `Rfb/RfbServer` in the agent. The wire numbers they share
live once, in `RemoteDesktop/RfbProtocol.cs`, which the agent's project links, so the two cannot
drift. Written from RFC 6143 and the community protocol description; noVNC was only a behaviour
cross-check and is not ported (it is MPL).

- **Pixel format** 32 bpp, depth 24, little-endian, true colour, red at bit 16, green at 8, blue at
  0: every pixel arrives as B, G, R, which is the framebuffer's BGRA **and X11's own ZPixmap layout
  at depth 24 on a little-endian server**, so a captured screen is copied rather than converted, end
  to end. The agent serves that one format and refuses any other with a stated reason; there is no
  conversion path because there is no client that would need one.
- **The server offers only what this client decodes**: Tight, plus the DesktopSize and RichCursor
  pseudo-rectangles. No CopyRect, because working out that something moved rather than changed is
  guesswork that draws artefacts, which is why the x11vnc this replaced was run with its scroll
  detection off. No ZRLE, no Hextile, no colour map, no bell, no ExtendedDesktopSize (following the
  host's resolution is all DesktopSize is needed for, and resizing somebody's real monitor is not
  ours to do). The client still asks for Raw and CopyRect, which costs nothing and would let it
  drive a stock VNC server.
- **Cut text is UTF-8**, both ways, not RFC 6143's Latin-1, and capped at 16 MiB. The agent always
  comes from the viewer's own bundle, so there is nothing to negotiate. The agent sends a host copy
  from its update loop whether or not an update was asked for, keeping that loop the only writer.
- **Tight, one rectangle at a time.** The protocol lets a server keep four deflaters alive for the
  session and end each rectangle's share with a sync flush; .NET has no public way to sync flush a
  deflater and keep it, but the protocol also has a per-rectangle reset bit, and the decoder honours
  it, so the agent resets stream 0 and starts a fresh `ZLibStream` for every rectangle. It costs the
  two byte zlib header and a little ratio, and it buys an encoder with no state to get wrong. The
  decoder's side is unchanged and still handles session-long streams, a feed that **throws when
  asked for bytes it does not have** rather than returning 0, because a stream out of step with the
  server has to be a disconnect with a reason and never a wrong picture.
- **A rectangle's compressed data cannot exceed 4 MiB**, because Tight's compact length is seven
  bits in each of the first two bytes and eight in the third. Exceeding it does not fail, it wraps:
  a 5.8 MB rectangle announces itself as 99 bytes and the viewer waits for ever for the rest of a
  stream it has already been given. That is why every Tight encoder splits its rectangles, and why
  `RfbServer.Split` cuts anything over a megapixel into horizontal bands before the count in the
  update header is decided.
- **Compression is level 1.** The client asks for 6 out of politeness to x11vnc; on screen content
  the difference is about 8:1 against 6:1, for five to ten times the CPU.
- **Threads**: here, one reader thread does the handshake and decodes, blitting each rectangle under
  `SyncRoot` after decoding it outside, and `RfbWriter` has its own thread because a write on an SSH
  channel blocks while the window is full, which on a slow link is exactly when the user is dragging
  something. In the agent, client messages are read on their own thread and go straight to XTEST,
  and only the update loop writes to the channel.
- **Asking for a size.** The one thing this client says that a viewer usually does not: the
  ExtendedDesktopSize extension's `SetDesktopSize` message, borrowed for its body and nothing else.
  The answer is the ordinary DesktopSize rectangle, so there is no encoding to negotiate and no
  status to read, and a size the display will not take is simply the nearest one it will. It goes
  out only for a desktop VirtDeck started, and the agent is told the same thing on its command line
  (`--resizable`), because a viewer's good manners are not where "somebody's real monitor is not
  ours to resize" belongs. See "Resizing a desktop of our own".
- **Pacing and pause.** RFB is pull: the next update request goes out as soon as an update's header
  arrives, so one is always in flight. That is also why the agent counts outstanding requests rather
  than holding a flag, since the next request always arrives while the current update is still being
  written and a flag cleared afterwards throws it away. `Paused` stops asking, which is how a hidden
  module costs the host and the link nothing; resuming is an ordinary incremental request (a full
  one only after a resolution change).
- A handshake failure fails `Ready`, which the caller reports; a session that ends after it raises
  `Disconnected`, once, and never for a session this end closed.

## Resizing a desktop of our own

`X11/XRandr.Resize` in the agent, under `--resizable`. **An Xvfb's `-screen` size is its RANDR
maximum, not a fixed mode**, which is the whole reason this is possible at all and why the server is
started at 4K.

- **The sequence is the one xrandr performs**, and its order is not arbitrary: the screen cannot
  shrink under a CRTC still driving a larger mode, and every change to what modes the output has
  moves the server's configuration timestamp on. So: read the resources, turn the CRTC **off**, take
  the mode this agent last made off the output, put the new one on, set the screen size, read the
  resources again, and turn the CRTC back on at the new mode. Both timestamps the CRTC requests
  carry are what the server is told to stamp the configuration with, not a promise about what it
  looks like now.
- **The whole sequence runs under a server grab, and the CRTC is set with `CurrentTime`.** A desktop
  that manages displays itself (GNOME's mutter, and the same code in its forks) answers every screen
  change by comparing the server's two RANDR timestamps: a configuration time later than the set
  time means a hotplug, and a hotplug means re-applying **its own** configuration, which on an Xvfb
  is the output's first mode, which is the 4K the server was started at. The grab is what keeps it
  from ever seeing the half-configured state in the middle of the sequence (it is why xrandr grabs,
  and mutter grabs for its own reconfigurations too), and `CurrentTime` is what leaves the set time
  newer than the configuration time the new mode moved on. Handing back the timestamp that was read,
  as this did at first, freezes the set time for the life of the server, so every later change looks
  like a hotplug: that is why a GNOME desktop jumped to 4K the moment the window was resized, while
  xfce4, which manages nothing, stayed where it was put. Measured against Xvfb with a mutter-derived
  window manager on it: three resizes the old way, three reconfigurations by the desktop; three the
  new way, none.
- **A mode the server already has is used, and only then is one made.** A mode outlives the agent
  that created it (it stays on the output, and the CRTC is on it), so the session before this one
  left its own behind; `RRCreateMode` with a name that exists is `BadName`, and the name is the
  size. Looking first is what keeps those two from ever meeting. A mode this agent did make is taken
  off the output when it moves away from it, so a dragged window edge does not leave a trail; that
  happens with the CRTC already off, since a mode in use cannot go.
- **Nothing here can end a session.** A display that refuses says so on stderr once per size and the
  picture carries on at the size it has.
- **A capture can be overtaken by a resize.** GetImage of a root that has just shrunk is `BadMatch`,
  which used to be fatal. The agent's own resize and its own capture are now serialised against each
  other, and a read that comes back BadMatch anyway (anybody on the host can change the mode) is
  followed rather than thrown: measure again, resize the buffers, and tell the viewer.

## Capture

- **A full read and a compare, not a damage region.** Damage rectangles look like the answer and are
  not: measured on a compositing desktop, every DamageNotify was the whole 5120x1440 screen, 75 to
  97 times a second, because the compositor redraws the root each frame. So DAMAGE is used only as a
  wakeup, to know that anything happened at all, and what actually changed comes from comparing
  64x64 tiles of the new frame against the last one, which is what x11vnc did too. Dirty tiles are
  coalesced into runs along a row and then merged downwards, so a changed window is a few rectangles
  rather than a few hundred tiles.
- **The numbers say it is affordable.** On 5120x1440 with the NVIDIA proprietary driver a full
  GetImage is about 9 ms and the tile compare about 8 ms; on an ordinary 1920x1080 host the pair is
  about 4 ms. Measured through the whole stack, that is 27 to 37 incremental frames a second at
  5120x1440.
- **No MIT-SHM.** GetImage over the unix socket runs at about 3 GiB/s, far more than an SSH link can
  carry, so the shared memory path buys nothing and the agent needs no native interop for it.
- **When nothing has changed, nothing is read.** The loop waits on the damage wakeup, so a connected
  but idle desktop costs neither host nor link. With no DAMAGE extension the agent says so on stderr
  and polls instead.
- **The cursor is never in the picture.** A hardware cursor is composited by the display engine, not
  by the X server, so it is not in a GetImage of the root; XFIXES hands over its image and it goes as
  a RichCursor rectangle. That is what makes the one-cursor rule work: the picture has none and the
  viewer draws exactly one.

## The display## The display

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
console's scancodes (see `PhysicalKeyMap`): the agent is told "the user typed @" and works out which
of its keys, with which modifiers, produce that. It is the standard VNC behaviour and the only one
that survives a local and a remote layout that differ.

- `X11Keysyms.For`: keys that type nothing (Enter, arrows, function keys, modifiers, the keypad)
  from the physical key, left and right kept apart; otherwise the character Avalonia says was typed
  (`KeySymbol`). Latin-1 is its own keysym, the rest `0x01000000` plus the code point.
- **When Avalonia says nothing, the key and Shift do.** With Ctrl held the character is a control
  code, and on X11 there is no character at all for a keysym Avalonia has no `Key` of its own for,
  which is every shifted digit, `!` to `)`. Shift is the half that must not be lost: the far end is
  holding the user's Shift, and asking for a character that does not want Shift makes the agent let
  go of it, so Shift+1 arrived as `1` and Ctrl+Shift+A as Ctrl+A. What the key types at the shift
  level held is sent instead, and the agent adds the Ctrl it already has. The gap that is left is a
  character X11 names and Avalonia has no `Key` for on a layout that is not QWERTY, an AltGr
  character above all: the key's own plain character is the best the client has, so AltGr+E on Linux
  arrives as `e`. Windows reports the character itself and has neither problem.
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
  disposes the session, whose channel ending is what ends the agent on the host.
- **Stated answers on the page**, most specific first: how the last session ended (with stderr's
  reason), the probe's failure, no agent for this architecture, a Wayland desktop, no X session at
  all, or "Choose a session and Connect".
- **The way out is under the words.** The empty page carries the picker and Connect while the host
  has a session to attach to, and New desktop while it has none, so the answer to what it says is in
  the same place as the words. The picker is the strip's own selection shown twice and kept in step,
  and a host that cannot be given a desktop keeps a disabled button with the reason on hover. A
  probe still running, one that failed and a connection in progress carry words alone.
- **New desktop and End desktop** sit with the picker and Refresh rather than with Connect, because
  they are about the list and not about this connection. The flyout is the host's own session menu;
  End is enabled only for a display VirtDeck started, and confirms, because what is running in one
  goes with it. See "A desktop of our own".
- **The clipboard is shared, text only.** A copy on the host lands on this computer's clipboard.
  This computer's text goes to the host when it changes while the module is on screen in the active
  window (the VM console's poll: formats every 500 ms, the text only when they changed), and once on
  every entry: connecting, switching to the module, activating the window. That makes the agent
  the host's clipboard owner, replacing what was copied there. **Type clipboard** in the Keys menu
  stays, for where a paste cannot reach: a login screen's password box.
- Fit is one setting for every host (`AppSettings.RemoteControlFit`): it is about this screen. It is
  hidden, and the picture is fitted, while a virtual desktop is on screen, because that one is made
  the size of the space it has and the two settings would draw the same thing. The saved setting is
  not touched, so it is back as it was on the next real display.
- Status: left what is connected, at what size and scale; right the agent's version.

## What it does not do

Wayland sessions (nothing to attach to without the compositor's cooperation, and the portal path is
a second agent with a consent dialog on the host's screen, not a flag on this one), images on the
clipboard and the PRIMARY selection, resizing a display that is not one of ours (somebody's real monitor is theirs),
growing a virtual desktop past the 4K its Xvfb was started with, one
monitor at a time (the whole X screen is shown,
all monitors), JPEG for photographic rectangles (zlib only, so a busy full-screen video is
bandwidth-hungry), displays that are not 24 or 32 bit true colour, and audio.
