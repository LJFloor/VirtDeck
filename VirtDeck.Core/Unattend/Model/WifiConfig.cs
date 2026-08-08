namespace VirtDeck.Unattend
{
    public enum WifiMode
    {
        /// <summary>OOBE asks. What Windows does on its own, and the default.</summary>
        Interactive,

        /// <summary>Skip the page: there is a wired connection, or none is wanted.</summary>
        Skip,

        /// <summary>Join the network described by the fields below.</summary>
        Unattended,

        /// <summary>Join the network described by a WLAN profile document.</summary>
        FromProfile,
    }

    /// <summary>
    /// Named with a <c>Mode</c> suffix because the generator's own enum is <c>WifiAuthentications</c>,
    /// plural, and two names one letter apart in the same file is a bug waiting to be typed.
    /// </summary>
    public enum WifiAuthenticationMode { Open, WPA2PSK, WPA3SAE }

    /// <summary>
    /// The "WLAN / Wi-Fi setup" tab. Rarely useful for a libvirt guest, which normally has a wired
    /// virtual NIC and no wireless hardware at all, but an answer file made here can be carried to
    /// real hardware.
    /// </summary>
    public sealed class WifiConfig
    {
        public WifiMode Mode { get; set; } = WifiMode.Interactive;

        /// <summary>The SSID. Used as the profile name too.</summary>
        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        public WifiAuthenticationMode Authentication { get; set; } = WifiAuthenticationMode.WPA2PSK;

        /// <summary>The network does not broadcast its SSID, so Windows has to probe for it.</summary>
        public bool NonBroadcast { get; set; }

        public bool ConnectAutomatically { get; set; } = true;

        /// <summary>
        /// A WLANProfile document, as produced by
        /// <c>netsh.exe wlan export profile key=clear</c>. Validated against the WLAN profile schema.
        /// </summary>
        public string ProfileXml { get; set; } = "";
    }
}
