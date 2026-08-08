using VirtDeck.Services;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// The answer disc: an <c>autounattend.xml</c> in the root of a small ISO 9660 image, attached to
    /// the VM as a second CD-ROM.
    ///
    /// Windows Setup runs an implicit answer-file search at the start of every configuration pass, and
    /// one of the entries in that search is "root of removable read-only media, in drive-letter order".
    /// A separate disc therefore needs no command line, no keystroke and no modification of the install
    /// ISO. Editing the install ISO would not work anyway: a retail Windows image serves its real tree
    /// from UDF, where a file added to the ISO 9660 side is invisible.
    ///
    /// The disc is written to the host rather than streamed over NBD because an unattended install spans
    /// several reboots and libvirt refuses <c>startupPolicy</c> on network sources, so a stream that ends
    /// with the VirtDeck session would leave a domain that cannot start at all.
    /// </summary>
    public static class UnattendMedia
    {
        /// <summary>The name Setup searches for. Case does not matter to it; this is the usual spelling.</summary>
        public const string FileName = "autounattend.xml";

        private const string VolumeId = "UNATTEND";

        /// <summary>Marks a generated disc, so the delete-VM path can tell it from a user's own ISO.</summary>
        private const string PathSuffix = "-unattend.iso";

        /// <summary>Same directory the wizard's boot disk defaults to.</summary>
        private const string ImageDirectory = "/var/lib/libvirt/images/";

        /// <exception cref="UnattendBuildException">The config cannot be turned into an answer file.</exception>
        public static byte[] BuildIso(UnattendConfig config) =>
            Iso9660Builder.Build(FileName, UnattendXml.Build(config), VolumeId);

        public static string RemotePath(string vmName) => ImageDirectory + vmName + PathSuffix;

        /// <summary>
        /// Whether a path is a disc VirtDeck generated. Used to offer it for deletion along with the
        /// VM's disk images; a user's own install ISO is never offered.
        /// </summary>
        public static bool IsAnswerIso(string path) =>
            path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
