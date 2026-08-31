namespace VirtDeck.Models
{
    /// <summary>
    /// Whether the account can be logged into with a password. Read from <c>passwd -S</c>'s status
    /// field, which is the only thing that reports it without parsing the shadow file by hand.
    /// <see cref="Unknown"/> is a real answer, not a missing one: a host whose shadow tooling is
    /// unusual still gets its user list, with this column blank.
    /// </summary>
    public enum PasswordState
    {
        Unknown,

        /// <summary>A usable password is set (<c>passwd -S</c> says P).</summary>
        Set,

        /// <summary>No password at all (NP). The account exists and cannot be logged into with one.</summary>
        None,

        /// <summary>Locked (L): the hash is prefixed with <c>!</c>, so no password can match it.</summary>
        Locked,
    }

    /// <summary>
    /// One line of the host's passwd database, plus the two things that are not on that line: which
    /// groups the user is in, and whether the account is locked.
    /// </summary>
    public class UserAccount
    {
        /// <summary>The login name. The key everything else here is addressed by.</summary>
        public string Name { get; set; } = string.Empty;

        public int Uid { get; set; }

        /// <summary>The gid on the passwd line, which on every host VirtDeck manages names the account's own group.</summary>
        public int Gid { get; set; }

        /// <summary>
        /// The GECOS field exactly as the host holds it, commas and all. Kept raw because
        /// <see cref="FullName"/> is only its first element and writing that back has to rejoin the
        /// rest rather than discard somebody's room number and phone extension.
        /// </summary>
        public string Gecos { get; set; } = string.Empty;

        /// <summary>The first comma-separated element of <see cref="Gecos"/>, which is what everything calls the full name.</summary>
        public string FullName { get; set; } = string.Empty;

        public string Home { get; set; } = string.Empty;

        /// <summary>The login shell as the passwd line spells it, an absolute path or empty.</summary>
        public string Shell { get; set; } = string.Empty;

        /// <summary>
        /// The groups the user is in: every group listing them as a member, with the account's own
        /// group left out, since that one is the gid on the passwd line and is not a membership
        /// anybody adds or removes. This is the set the edit dialog ticks and the set
        /// <c>gpasswd</c> adds to and removes from.
        /// </summary>
        public List<string> Groups { get; set; } = new();

        public PasswordState Password { get; set; } = PasswordState.Unknown;

        /// <summary>True when the account is below the host's own threshold for a real login account.</summary>
        public bool IsSystem { get; set; }
    }

    /// <summary>One line of the host's group database.</summary>
    public class UserGroup
    {
        public string Name { get; set; } = string.Empty;

        public int Gid { get; set; }

        /// <summary>
        /// The group file's own member list, which does <b>not</b> include the users whose passwd
        /// gid names this group. Those are counted in separately, because somebody reading the
        /// table means "who is in this group" rather than "who does the file list".
        /// </summary>
        public List<string> Members { get; set; } = new();
    }

    /// <summary>
    /// Everything VirtDeck can say about an account it is about to create or change. Flat and
    /// mutable, for the same reason <see cref="ContainerSpec"/> is: the dialog writes to it
    /// keystroke by keystroke and it has to be allowed to be invalid in between.
    ///
    /// Turning one of these into a command is <c>UserAccountService</c>'s job, because that file is
    /// the only place in the app that knows any shadow-utils vocabulary.
    /// </summary>
    public class UserSpec
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>What goes in GECOS element 0. The rest of the field is carried in <see cref="GecosTail"/>.</summary>
        public string FullName { get; set; } = string.Empty;

        /// <summary>
        /// GECOS elements 1 and up, joined, so an edit that changes the full name puts the room and
        /// phone numbers back. Empty on a new account.
        /// </summary>
        public string GecosTail { get; set; } = string.Empty;

        /// <summary>Empty means the host's own default (<c>/home/&lt;name&gt;</c>). Only ever read when creating.</summary>
        public string Home { get; set; } = string.Empty;

        /// <summary>Empty means the host's own default, which is what <c>useradd</c> does with no <c>-s</c>.</summary>
        public string Shell { get; set; } = string.Empty;

        /// <summary>
        /// The new password, in the clear, on its way to <c>chpasswd</c> over stdin. Empty means
        /// "no password" when creating and "leave it alone" when editing. Never written to a
        /// command line, a settings file or the log.
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>Whether the account should end up locked (<c>usermod --lock</c>).</summary>
        public bool Locked { get; set; }

        /// <summary>The groups the user should end up in, which is what <c>gpasswd</c> manages.</summary>
        public List<string> Groups { get; set; } = new();
    }

    /// <summary>
    /// Everything the user-accounts module and both its dialogs read, from one round trip. The
    /// dialogs are handed the catalog the module already loaded, so neither has a round trip of its
    /// own and neither can open onto empty pickers.
    /// </summary>
    public class AccountCatalog
    {
        public List<UserAccount> Users { get; set; } = new();
        public List<UserGroup> Groups { get; set; } = new();

        /// <summary>The login shells <c>/etc/shells</c> lists. A suggestion, not a closed set.</summary>
        public List<string> Shells { get; set; } = new();

        /// <summary>The host's own <c>UID_MIN</c>, which is what separates a login account from a system one.</summary>
        public int UidMin { get; set; } = 1000;

        /// <summary>
        /// The host's own <c>UID_MAX</c>. The upper end matters as much as the lower one: <c>nobody</c>
        /// is uid 65534 and would otherwise read as an ordinary login account, since it clears
        /// <see cref="UidMin"/> by a mile. Cockpit gets there by naming <c>nobody</c>; using the
        /// host's own number covers the whole convention instead, including <c>nfsnobody</c> and
        /// whatever else lives above the range.
        /// </summary>
        public int UidMax { get; set; } = 60000;

        public int GidMin { get; set; } = 1000;

        public int GidMax { get; set; } = 60000;

        /// <summary>
        /// The directory the host puts home directories in, from <c>useradd -D</c>. Asked rather
        /// than assumed because it is not always <c>/home</c>: an ostree host (Fedora Silverblue and
        /// friends) says <c>/var/home</c>, and a dialog that filled a path in for the user has to
        /// fill in the one the host would have chosen itself.
        /// </summary>
        public string HomeBase { get; set; } = "/home";

        /// <summary>
        /// What <c>useradd --version</c> answered, or empty when the tool is not there at all. Empty
        /// is what makes the module say why it can do nothing instead of showing an empty list.
        /// </summary>
        public string ToolVersion { get; set; } = string.Empty;

        public bool Available => ToolVersion.Length > 0;
    }
}
