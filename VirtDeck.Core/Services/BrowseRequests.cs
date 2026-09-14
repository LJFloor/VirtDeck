using System.Runtime.CompilerServices;

namespace VirtDeck.Services
{
    /// <summary>
    /// A directory another module has asked the File explorer to open, left here for it to pick up.
    ///
    /// <para><c>IModuleNavigator</c> carries nothing beyond which page to show, so the instruction is
    /// left where both modules can see it and taken by the explorer inside its own activation, which
    /// is the rule <see cref="PackageService.RequestInstallAll"/> set for the Overview module's Update now.
    /// The Containers module's Browse files on a volume is the one caller.</para>
    ///
    /// <para>Keyed on the connection for <see cref="PackageService"/>'s reason: a host switch builds a
    /// new shell over a new connection, and nothing asked of the old host may land on the new one. The
    /// two modules share no service of their own, and one pending path is too small a thing to turn
    /// <see cref="RemoteFileService"/> into a shared instance for.</para>
    /// </summary>
    public sealed class BrowseRequests
    {
        private static readonly ConditionalWeakTable<SshConnectionManager, BrowseRequests> Shared = new();

        /// <summary>The requests for this connection, making them the first time they are asked for.</summary>
        public static BrowseRequests For(SshConnectionManager ssh) =>
            Shared.GetValue(ssh, _ => new BrowseRequests());

        // Private, so there is exactly one of these per connection, as with PackageService.
        private BrowseRequests() { }

        private string? _dir;

        /// <summary>Asks for <paramref name="dir"/> to be opened, replacing any request not yet taken.</summary>
        public void Request(string dir) => _dir = dir;

        /// <summary>
        /// The directory asked for, or null, and spent by being read: a request left behind would open
        /// that directory again the next time somebody went to the explorer for reasons of their own.
        /// </summary>
        public string? Take()
        {
            var dir = _dir;
            _dir = null;
            return dir;
        }
    }
}
