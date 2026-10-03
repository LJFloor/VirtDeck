# Linux packaging

## Building the AppImage

```bash
packaging/build-appimage.sh [version]     # -> publish/VirtDeck-<version>-x86_64.AppImage
```

Needs `dotnet` and `curl` (it downloads `appimagetool` into `publish/appimage/` on first run;
set `APPIMAGETOOL=/path/to/appimagetool` to use your own). The AppImage bundles the .NET runtime
and nothing else; see *Runtime dependencies* below for why.

AppImage is the primary Linux artifact because it keeps the app on the **host's** libusb and
libpulse: USB redirection opens `/dev/bus/usb/*` directly and audio goes to the user's running
PulseAudio/PipeWire daemon. A sandboxed format has to be punched open for both, so there is no
Flatpak or Snap.

## Building the .deb and .rpm

```bash
packaging/build-packages.sh [version]     # -> publish/virtdeck_<version>-1_amd64.deb
                                          #    publish/virtdeck-<version>-1.x86_64.rpm
```

Needs `dotnet` and `curl` (it downloads a pinned, checksummed `nfpm` into `publish/linuxpkg/`;
set `NFPM=/path/to/nfpm` to use your own). Both formats come from `nfpm.yaml`, so no `dpkg` or
`rpmbuild` is needed. The app goes to `/usr/lib/virtdeck` with `/usr/bin/virtdeck` linking to it,
and the license files to `/usr/share/doc/virtdeck/`.

What they add over the AppImage is the udev rule: installed as root into `/usr/lib/udev/rules.d/`,
with `udev-reload.sh` reloading udev after install and removal and replaying a change event, so a
device that was already plugged in works without a replug.

The X11, fontconfig and ICU libraries are hard dependencies (the app does not start without them);
the rows under *Runtime dependencies* are recommendations. Debian names ICU by version, so the
.deb lists `libicu70` to `libicu80` as alternatives; a release past that refuses the install until
the list in `nfpm.yaml` is extended. Tested on Debian 12 and 13, Ubuntu 22.04 and 24.04, and Fedora.

## Files

| File | Goes to | Why |
|---|---|---|
| `virtdeck.desktop` | `/usr/share/applications/` | Menu entry. `StartupWMClass=virtdeck` matches the WM_CLASS Avalonia sets from the assembly name |
| `icons/hicolor/` | `/usr/share/icons/hicolor/` | App icon, 16-512px plus the scalable SVG |
| `io.github.ljfloor.VirtDeck.metainfo.xml` | `/usr/share/metainfo/` | AppStream data for software centres |
| `70-virtdeck-usb.rules` | `/usr/lib/udev/rules.d/` (packages), `/etc/udev/rules.d/` (by hand) | USB redirection needs rw access to `/dev/bus/usb/*` |
| `nfpm.yaml`, `udev-reload.sh` | (build only) | The .deb/.rpm definition and their post-install/remove script |

The Remote Control module's host agent is not a file here: it is embedded in `VirtDeck.Core` and
uploaded to the host it runs on (see `native/agent/`). It is ours, so unlike the x11vnc it replaced
there is no corresponding-source archive to carry.

`icons/make-icons.py` regenerates every icon (the PNG sizes, the scalable SVG and the Windows
`.ico`) from one set of numbers. Run it after changing the icon; nothing else rasterises, so no
build machine needs an SVG renderer.

**The udev rule is the one file the AppImage cannot install itself** (the packages do) (no install step, no root).
It is carried inside the image at `usr/share/virtdeck/70-virtdeck-usb.rules`, and the USB picker
prints that exact path in the `LIBUSB_ERROR_ACCESS` message so the user can copy the command:

```bash
sudo install -m 0644 70-virtdeck-usb.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```

The ACL is applied when a device appears, so anything already plugged in must be replugged once.

## Runtime dependencies

These come from the distro on purpose; they are the parts that have to match the host's kernel
drivers and sound daemon, and each one degrades gracefully when missing:

| Feature | Package (Debian/Ubuntu) | Package (Fedora) | Without it |
|---|---|---|---|
| USB redirection | `libusb-1.0-0`, `libusbredirhost1t64` (`libusbredirhost1` before the t64 transition) | `libusb1`, `usbredir` | The usbredir channels are **not connected at all** and the picker says what to install |
| USB mass-storage hand-over | `udisks2` | `udisks2` | Falls back to `umount`, which needs the mount to be user-unmountable |
| Guest audio | `libpulse0` (PipeWire's Pulse layer counts) | `pulseaudio-libs` | Silence: the session drops to a null sink |
| Remembering passwords | a keyring serving `org.freedesktop.secrets` (`gnome-keyring`, `kwalletd`/`kwallet-secrets`, KeePassXC). No client library: VirtDeck speaks the protocol itself | ditto | "Remember passwords" is disabled in the connect window with the reason under it; passwords are typed each time, exactly as before |

Everything on the *host* side (libvirt, QEMU/KVM, `virsh`) is reached over SSH, so a Linux client
needs none of it locally.

**The keyring needs nothing installed.** It used to need libsecret, which links the distro's glib,
and the AppImage bundling nothing was what kept the two from disagreeing at load time. The Secret
Service is now spoken directly on the session bus (see `docs/saved-hosts.md`), so the only thing that
has to be there is a keyring serving it. The bundle-nothing rule still holds for libusb and libpulse,
which are loaded from the distro by soname for exactly that reason.

## What the keyring does and does not protect

Saved passwords are encrypted at rest, so another **user** of the machine cannot read them. Neither
store protects against another process **running as you**: the freedesktop Secret Service has no
per-application isolation, so anything on your session bus can read VirtDeck's items (KeePassXC's
per-access prompt is a KeePassXC feature, not a property of the protocol), and on Windows anything in
your logon session can read the credentials by target name. That is the same bargain every browser's
password manager makes. Leaving "Remember passwords" off is the alternative, and it is the default.
