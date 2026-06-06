Place the pinned UsbDk kernel-driver installer here:

  UsbDk_1.0.22_x64.msi

Download (signed, x64):
  https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi
  (identical mirror: https://www.spice-space.org/download.html)

The installer (SpiceVmManager.iss) bundles this MSI and runs it silently
(`msiexec /i UsbDk_1.0.22_x64.msi /qn /norestart`) during post-install, skipping it if
UsbDk is already present. Ship the MSI unmodified — it is already Authenticode-signed;
repackaging or re-signing would break its signature.

This binary is intentionally NOT committed. Fetch it (and verify its SHA-256) in CI, or
drop it here manually before running `iscc installer\SpiceVmManager.iss`.
