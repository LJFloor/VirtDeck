using System.Runtime.InteropServices;
using SpiceClient.Interop;

namespace SpiceClient.Usb;

/// <summary>
/// Answers "can this client redirect USB at all?" without opening anything, just whether the
/// native stack (<c>libusb-1.0</c> + <c>usbredirhost</c>) is present.
///
/// This has to be checked *before* a usbredir channel is connected, not after. A channel that
/// links and then cannot drive a usbredirhost never sends the usb_redir hello, and the SPICE
/// server responds by closing the **entire** connection (display, inputs and all) a second or so
/// later. The console then reconnects, links the dead channel again, and gets dropped again: a
/// flickering console on any VM that has &lt;redirdev&gt; elements. So when the stack is missing the
/// channel is simply not opened, exactly as spice-gtk built without usbredir does.
/// </summary>
public static class UsbSupport
{
    /// <summary>
    /// True when both native libraries can be loaded. <paramref name="reason"/> is a user-facing
    /// explanation naming what to install when they can't.
    /// </summary>
    public static bool IsAvailable(out string? reason)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        try
        {
            if (!NativeLibraryResolver.CanLoad("libusb-1.0"))
            {
                reason = windows
                    ? "USB redirection is unavailable: libusb-1.0.dll is missing from the install folder."
                    : "USB redirection is unavailable: libusb-1.0 is not installed " +
                      "(package libusb-1.0-0 on Debian/Ubuntu, libusb1 on Fedora).";
                return false;
            }

            if (!NativeLibraryResolver.CanLoad("usbredirhost"))
            {
                reason = windows
                    ? "USB redirection is unavailable: usbredirhost.dll is missing from the install folder."
                    : "USB redirection is unavailable: the usbredir library is not installed " +
                      "(package libusbredirhost1t64 or libusbredirhost1 on Debian/Ubuntu, " +
                      "usbredir on Fedora).";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"USB redirection is unavailable: {ex.Message}";
            return false;
        }

        reason = null;
        return true;
    }
}
