using System.Xml.Linq;

namespace VirtDeck.Services
{
    /// <summary>What the domain XML currently has, relevant to USB redirection.</summary>
    public sealed class UsbRedirInspection
    {
        public bool HasSpice;
        public bool HasUsbController;
        public int RedirdevCount;
    }

    /// <summary>
    /// Ensures a guest domain is configured for USB redirection: a USB controller plus up to
    /// <see cref="DESIRED_CHANNELS"/> &lt;redirdev type='spicevmc'&gt; channels (one per simultaneously
    /// redirected device). redirdev channels hot-plug when a controller already exists; adding a
    /// controller (none present) is persistent-only and needs a power-cycle. Idempotent.
    /// </summary>
    public sealed class UsbProvisioning
    {
        public const int DESIRED_CHANNELS = 4;

        private readonly VirshService _virsh;

        public UsbProvisioning(VirshService virsh) => _virsh = virsh;

        public UsbRedirInspection Inspect(string vmName)
        {
            var domain = XDocument.Parse(_virsh.GetDomainXml(vmName)).Root
                ?? throw new Exception("Empty domain XML.");
            var devices = domain.Element("devices");
            var insp = new UsbRedirInspection();
            if (devices == null) return insp;

            insp.HasSpice = devices.Elements("graphics")
                .Any(g => (string?)g.Attribute("type") == "spice");

            // A controller explicitly set to model='none' disables USB and doesn't count.
            insp.HasUsbController = devices.Elements("controller").Any(c =>
                (string?)c.Attribute("type") == "usb" &&
                !string.Equals((string?)c.Attribute("model"), "none", StringComparison.OrdinalIgnoreCase));

            insp.RedirdevCount = devices.Elements("redirdev").Count(r =>
                (string?)r.Attribute("type") == "spicevmc" &&
                ((string?)r.Attribute("bus") ?? "usb") == "usb");

            return insp;
        }

        /// <summary>
        /// Brings the VM up to <see cref="DESIRED_CHANNELS"/> redirdev channels, adding a USB
        /// controller first if none exists. Returns how many redirdev channels were added and
        /// whether a power-cycle is required (true only when a controller had to be added).
        /// </summary>
        public (int added, bool needsPowerCycle) EnsureRedirDevices(string vmName)
        {
            var insp = Inspect(vmName);
            if (!insp.HasSpice)
                throw new Exception("This VM has no SPICE graphics, so USB redirection is not available.");

            bool needsController = !insp.HasUsbController;
            bool live = !needsController; // redirdevs can only hot-plug onto an existing controller

            if (needsController)
            {
                // USB controllers can't be hot-plugged, so the whole change is persistent-only and
                // takes effect on the next power-cycle. A single xHCI controller covers all device
                // speeds in one element, the simplest reliable add.
                _virsh.AttachDeviceXml(vmName, "<controller type='usb' model='qemu-xhci' ports='8'/>", live: false);
            }

            int toAdd = Math.Max(0, DESIRED_CHANNELS - insp.RedirdevCount);
            for (int i = 0; i < toAdd; i++)
                _virsh.AttachDeviceXml(vmName, "<redirdev bus='usb' type='spicevmc'/>", live: live);

            return (toAdd, needsController);
        }
    }
}
