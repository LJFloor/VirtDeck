#!/bin/sh
# Post-install and post-remove script of the .deb and .rpm.
#
# Reloads udev so 70-virtdeck-usb.rules takes effect (or stops) without a reboot, then replays a
# change event for every USB device: that re-applies uaccess, so a device that was already plugged
# in gets the seat user's ACL without being replugged. Never fails the transaction: a container or
# chroot has no running udev, and the rule simply applies at next boot.
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules >/dev/null 2>&1 || true
    udevadm trigger --subsystem-match=usb --action=change >/dev/null 2>&1 || true
fi
exit 0
