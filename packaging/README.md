# Linux packaging bits

Files a Linux package (or a user running from a `dotnet publish` output) needs to install
alongside the app itself.

| File | Goes to | Why |
|---|---|---|
| `70-virtdeck-usb.rules` | `/etc/udev/rules.d/` | USB redirection needs rw access to `/dev/bus/usb/*` |

## Runtime dependencies

These come from the distro — nothing is bundled:

| Feature | Package (Debian/Ubuntu) | Package (Fedora) | Without it |
|---|---|---|---|
| USB redirection | `libusb-1.0-0`, `libusbredirhost1t64` (`libusbredirhost1` before the t64 transition) | `libusb1`, `usbredir` | The usbredir channels are **not connected at all** and the picker says what to install |
| USB mass-storage hand-over | `udisks2` | `udisks2` | Falls back to `umount`, which needs the mount to be user-unmountable |
| Guest audio | `libpulse0` (PipeWire's Pulse layer counts) | `pulseaudio-libs` | Silence — the session drops to a null sink |

The udev rule is the only one that is not just a shared library: without it `libusb_open` fails
with `LIBUSB_ERROR_ACCESS` and the picker tells the user to install it.
