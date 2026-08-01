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
PulseAudio/PipeWire daemon. A sandboxed format has to be punched open for both: a Flatpak needs
`--device=all` for USB, so it is the second option, and `.deb`/`.rpm` are optional after that.

## Files

| File | Goes to | Why |
|---|---|---|
| `virtdeck.desktop` | `/usr/share/applications/` | Menu entry. `StartupWMClass=virtdeck` matches the WM_CLASS Avalonia sets from the assembly name |
| `icons/hicolor/` | `/usr/share/icons/hicolor/` | App icon, 16-512px plus the scalable SVG |
| `io.github.ljfloor.VirtDeck.metainfo.xml` | `/usr/share/metainfo/` | AppStream data for software centres |
| `70-virtdeck-usb.rules` | `/etc/udev/rules.d/` | USB redirection needs rw access to `/dev/bus/usb/*` |

`icons/make-icons.py` regenerates every icon (the PNG sizes, the scalable SVG and the Windows
`.ico`) from one set of numbers. Run it after changing the icon; nothing else rasterises, so no
build machine needs an SVG renderer.

**The udev rule is the one file the AppImage cannot install itself** (no install step, no root).
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

Everything on the *host* side (libvirt, QEMU/KVM, `virsh`) is reached over SSH, so a Linux client
needs none of it locally.
