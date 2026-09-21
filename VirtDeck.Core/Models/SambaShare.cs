namespace VirtDeck.Models
{
    /// <summary>
    /// What one user or group may do in a shared folder. Three levels, because that is the whole
    /// vocabulary Synology offers and it is the one people already read correctly: anything finer
    /// belongs to the filesystem and anything coarser cannot express "read only".
    /// </summary>
    public enum ShareAccess
    {
        /// <summary>
        /// Not in <c>valid users</c> and no ACL entry at all. <b>Absence is the encoding</b>, in both
        /// halves: there is no such thing as a deny entry here, so a name set to this is simply not
        /// written anywhere.
        /// </summary>
        None,

        ReadOnly,

        ReadWrite,
    }

    /// <summary>
    /// What an unauthenticated client may do. Separate from <see cref="ShareAccess"/> only so the
    /// dialog can word it differently; the three cases line up one for one.
    /// </summary>
    public enum GuestAccess
    {
        None,
        ReadOnly,
        ReadWrite,
    }

    /// <summary>
    /// Whether a path can carry POSIX ACLs, which is the question the whole permissions half of this
    /// module rests on. It is a value and not a bool, because "no" and "there is nothing there" and
    /// "we could not tell" are three different sentences the UI has to say.
    /// </summary>
    public enum AclVerdict
    {
        /// <summary>Not asked yet. The probe is cached per path for the session.</summary>
        Unknown,

        /// <summary><c>setfacl</c> succeeded on a temporary file in the directory.</summary>
        Yes,

        /// <summary>The filesystem took the file but refused the ACL. exfat, ntfs-3g, nfs, zfs without <c>acltype=posixacl</c>.</summary>
        No,

        /// <summary>There is no directory at this path.</summary>
        Missing,

        /// <summary>The directory is there and root could not create a file in it. Read-only mount, or a full disk.</summary>
        Unwritable,
    }

    /// <summary>What one line of a samba config file is, as far as this app needs to care.</summary>
    public enum SambaLineKind
    {
        /// <summary>Empty or whitespace. Kept, because blank lines are how a config is laid out.</summary>
        Blank,

        /// <summary>Starts with <c>#</c> or <c>;</c>.</summary>
        Comment,

        /// <summary><c>[name]</c>.</summary>
        Section,

        /// <summary><c>key = value</c>.</summary>
        Parameter,

        /// <summary>
        /// Anything else, which in practice is a line continued from the one above it with a
        /// trailing backslash. Carried through untouched and never interpreted, which is the right
        /// answer for a construct this app has no reason to produce and no business rewriting.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// One line of a samba config file, kept as the text it was read as.
    ///
    /// <para><see cref="Raw"/> is the whole of the round trip: a line nothing touched is written
    /// back byte for byte, indentation, spacing and comment markers included. <see cref="Key"/> and
    /// <see cref="Value"/> are a reading of it, never a replacement for it.</para>
    /// </summary>
    public sealed class SambaLine
    {
        public SambaLineKind Kind { get; init; } = SambaLineKind.Unknown;

        /// <summary>The line exactly as read, with no trailing newline.</summary>
        public string Raw { get; set; } = string.Empty;

        /// <summary>For <see cref="SambaLineKind.Section"/>, the name between the brackets.</summary>
        public string Section { get; init; } = string.Empty;

        /// <summary>
        /// For <see cref="SambaLineKind.Parameter"/>, the name normalised the way samba normalises
        /// it: lower case with inner whitespace collapsed, so <c>readonly</c>, <c>read only</c> and
        /// <c>Read  Only</c> are one key.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>For <see cref="SambaLineKind.Parameter"/>, everything after the first <c>=</c>, trimmed.</summary>
        public string Value { get; init; } = string.Empty;

        /// <summary>The whitespace this line opens with, so a rewritten value keeps the file's own indenting.</summary>
        public string Indent { get; init; } = string.Empty;

        /// <summary>The parameter name as the file spells it, so rewriting a value does not restyle the key.</summary>
        public string RawKey { get; init; } = string.Empty;
    }

    /// <summary>
    /// One whole samba config file: where it lives, what it holds, and the exact text it was read
    /// as.
    ///
    /// <para><see cref="Digest"/> is what makes a write safe. It is taken over the text as read,
    /// sent with the write, and checked on the host before anything is installed, so an edit made
    /// at a terminal between the read and the save is refused rather than thrown away. Same
    /// contract as <c>CronFile</c>, for the same reason.</para>
    /// </summary>
    public sealed class SambaFile
    {
        public string Path { get; init; } = string.Empty;

        /// <summary>The file exactly as read.</summary>
        public string Text { get; init; } = string.Empty;

        public string Digest { get; init; } = string.Empty;

        /// <summary>Whether the text ended in a newline. Preserved rather than normalised, so saving
        /// an unchanged file is genuinely a no-op.</summary>
        public bool TrailingNewline { get; init; } = true;

        public List<SambaLine> Lines { get; init; } = new();

        /// <summary>True for <c>/etc/samba/smb.conf</c>: the file samba reads first and the one a new
        /// share goes in unless somebody picks another.</summary>
        public bool IsMain { get; init; }

        /// <summary>Why this file cannot be edited, or empty. A file too large to carry, or one read
        /// through an include this app could not resolve.</summary>
        public string Problem { get; set; } = string.Empty;

        public bool Writable => Problem.Length == 0;

        /// <summary>What the Where column says and what the destination picker lists.</summary>
        public string Label => IsMain ? "smb.conf" : System.IO.Path.GetFileName(Path);
    }

    /// <summary>One name in a share's permission list. Flat and mutable: the dialog's rows bind to it.</summary>
    public class SharePermission
    {
        /// <summary>A group rather than a user, which is the <c>@</c> prefix in the config and the <c>g:</c> prefix in the ACL.</summary>
        public bool IsGroup { get; set; }

        public string Name { get; set; } = string.Empty;

        public ShareAccess Access { get; set; } = ShareAccess.ReadOnly;

        /// <summary>How this name is written in <c>valid users</c> and friends.</summary>
        public string ConfigName => IsGroup ? "@" + Name : Name;
    }

    /// <summary>
    /// One shared folder, as some file on the host defines it.
    ///
    /// <para>Flat and mutable for the same reason <see cref="ContainerSpec"/> is: the editor writes
    /// to it keystroke by keystroke and has to be allowed to be invalid in between. Turning one of
    /// these back into config text is <c>SambaConfig</c>'s job and turning it into ACL specs is
    /// <c>SambaAcl</c>'s, because those two are the only files in the app that speak samba.</para>
    ///
    /// <para><b>Every share on the host is one of these, not just the ones VirtDeck wrote.</b> There
    /// is no marker of ours to recognise and no "ours versus theirs" to draw, which is what makes
    /// the byte-exact round trip in <see cref="SambaFile"/> load-bearing rather than a nicety.</para>
    /// </summary>
    public class SambaShare
    {
        /// <summary>The section name, which is what clients see and what the file is keyed by.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The file this share is defined in. Empty on one being created.</summary>
        public string FilePath { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        /// <summary>
        /// Samba's <c>available</c>, which is the enable switch. A share set to no stays in the file
        /// and stops being served, which is what makes Disable reversible without losing its
        /// permissions.
        /// </summary>
        public bool Available { get; set; } = true;

        public bool Browseable { get; set; } = true;

        public GuestAccess Guest { get; set; } = GuestAccess.None;

        public List<SharePermission> Permissions { get; set; } = new();

        public bool Recycle { get; set; }

        /// <summary>Where the recycle vfs module puts a deleted file. <c>%U</c> is samba's own macro for the session user.</summary>
        public string RecycleRepository { get; set; } = ".recycle/%U";

        public bool RecycleKeepTree { get; set; } = true;

        public bool RecycleVersions { get; set; } = true;

        /// <summary>
        /// The patterns samba hides and refuses, one per element. Written into <c>veto files</c>,
        /// whose separator is <c>/</c>, which is why a pattern may not contain one.
        /// </summary>
        public List<string> VetoFiles { get; set; } = new();

        public bool FollowSymlinks { get; set; }

        /// <summary>
        /// Whether saving this share also writes the folder's own permissions, and with them the
        /// masks that make those permissions hold. Cleared by the escape hatch on the Advanced page,
        /// for a folder whose permissions somebody else owns (a docker bind mount, a dataset another
        /// tool manages) or a foreign share whose own mode settings must not be touched.
        /// </summary>
        public bool ManageFolderPermissions { get; set; } = true;

        /// <summary>
        /// Whether the section already carried the full set of mask parameters this module writes,
        /// which is the only evidence available that VirtDeck has managed this share before. There
        /// is no marker in smb.conf and there is not going to be one, so this is a reading of the
        /// file rather than a claim about history.
        ///
        /// <para>What it gates is one confirmation. Saving a hand-written share through this editor
        /// takes over its folder: it adds the masks and writes an ACL whose <c>other</c> class is
        /// closed. That is the right thing to do and it is not the thing somebody expects from
        /// editing a description, so the first time it happens it is asked about.</para>
        /// </summary>
        public bool ManagedHere { get; set; }

        /// <summary>A deep copy, so the editor can be cancelled without having edited the catalog.</summary>
        public SambaShare Clone() => new()
        {
            Name = Name,
            FilePath = FilePath,
            Path = Path,
            Comment = Comment,
            Available = Available,
            Browseable = Browseable,
            Guest = Guest,
            Permissions = Permissions
                .Select(p => new SharePermission { IsGroup = p.IsGroup, Name = p.Name, Access = p.Access })
                .ToList(),
            Recycle = Recycle,
            RecycleRepository = RecycleRepository,
            RecycleKeepTree = RecycleKeepTree,
            RecycleVersions = RecycleVersions,
            VetoFiles = new List<string>(VetoFiles),
            FollowSymlinks = FollowSymlinks,
            ManageFolderPermissions = ManageFolderPermissions,
            ManagedHere = ManagedHere,
        };
    }

    /// <summary>
    /// One account samba's own password database knows about, which is not the same set as the
    /// host's login accounts and is the reason this module has a Users tab at all: SMB authenticates
    /// with an NTLM hash, which no unix password database holds.
    /// </summary>
    public class SambaUser
    {
        public string Name { get; set; } = string.Empty;

        /// <summary><c>pdbedit</c>'s account flags, as it prints them: <c>[U          ]</c>, <c>[DU         ]</c>.</summary>
        public string Flags { get; set; } = string.Empty;

        /// <summary>The D flag. A disabled account keeps its hash and is refused at logon.</summary>
        public bool Disabled => Flags.Contains('D');

        /// <summary>
        /// The matching unix account, or null when samba knows a name the passwd database does not.
        /// That combination is broken rather than exotic (samba resolves every SMB name to a uid), so
        /// the table says so rather than hiding the row.
        /// </summary>
        public UserAccount? Account { get; set; }

        /// <summary>
        /// Whether VirtDeck created the unix account, read off the GECOS marker. What it gates is
        /// deletion: an account this app did not make is never handed to <c>userdel</c>.
        /// </summary>
        public bool CreatedHere { get; set; }
    }

    /// <summary>
    /// Everything the shared folders module and its dialogs read, from one round trip. The dialogs
    /// are handed the catalog the module already loaded, so neither costs a round trip of its own
    /// and neither can open onto empty pickers.
    /// </summary>
    public class SambaCatalog
    {
        /// <summary>
        /// What <c>smbd --version</c> answered, or empty when samba is not there at all. Empty is
        /// what makes the module say why it can do nothing rather than show an empty list.
        /// </summary>
        public string ToolVersion { get; set; } = string.Empty;

        public bool Installed => ToolVersion.Length > 0;

        /// <summary>Why the listing is not a listing, drawn in place of the table. A value, never an exception.</summary>
        public string ListFailure { get; set; } = string.Empty;

        /// <summary>The unit name this host uses, <c>smbd</c> on Debian and <c>smb</c> on RHEL, or empty when neither is loaded.</summary>
        public string SmbdUnit { get; set; } = string.Empty;

        /// <summary><c>ActiveState</c>: active, inactive, failed. Empty when there is no unit to ask about.</summary>
        public string SmbdState { get; set; } = string.Empty;

        /// <summary><c>UnitFileState</c>: enabled, disabled, masked. What decides whether starting it needs an unmask first.</summary>
        public string SmbdEnabled { get; set; } = string.Empty;

        public string NmbdUnit { get; set; } = string.Empty;

        public string NmbdState { get; set; } = string.Empty;

        /// <summary>The tools found on the host, by name. What every disabled-with-a-reason command keys off.</summary>
        public HashSet<string> Tools { get; set; } = new(StringComparer.Ordinal);

        public bool Has(string tool) => Tools.Contains(tool);

        /// <summary>
        /// The shell a share account is created with. Asked rather than assumed: Debian keeps
        /// <c>nologin</c> in <c>/usr/sbin</c>, RHEL in <c>/sbin</c>, and a host with neither still
        /// has <c>/bin/false</c>.
        /// </summary>
        public string NologinShell { get; set; } = string.Empty;

        /// <summary><c>getenforce</c>'s word: Enforcing, Permissive, Disabled. Empty where there is no SELinux at all.</summary>
        public string SeLinux { get; set; } = string.Empty;

        public bool SeLinuxEnforcing =>
            string.Equals(SeLinux, "Enforcing", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Every config file this module read, smb.conf first. Each is editable on its own, with its
        /// own digest, exactly as the cron module holds one <c>CronFile</c> per crontab.
        /// </summary>
        public List<SambaFile> Files { get; set; } = new();

        public SambaFile? Main => Files.FirstOrDefault(f => f.IsMain);

        public SambaFile? FileAt(string path) =>
            Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

        /// <summary>
        /// Includes this app could not follow, with the reason. An <c>include</c> naming a path with
        /// a <c>%</c> macro in it resolves per connection, per client or per user, so there is no one
        /// file behind it to read; that is said out loud rather than guessed at.
        /// </summary>
        public List<string> SkippedIncludes { get; set; } = new();

        /// <summary>The effective <c>[global]</c> section as testparm reports it, lower-cased keys.</summary>
        public Dictionary<string, string> Globals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One global with the default samba itself would use when the host does not set it.
        /// testparm prints only what is set away from its default, so a missing key is not a missing
        /// answer.
        /// </summary>
        public string Global(string key, string fallback) =>
            Globals.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

        /// <summary>The unix account a guest session runs as, which is the name its ACL entry carries.</summary>
        public string GuestAccount => Global("guest account", "nobody");

        /// <summary>
        /// True when the host keeps its configuration in the registry, under which the text files
        /// this module edits are not what samba is reading.
        /// </summary>
        public bool RegistryBackend =>
            Global("config backend", "file").Contains("registry", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when accounts live in a domain rather than on this host, which makes the Users tab
        /// meaningless: <c>smbpasswd -a</c> writes to a passdb nothing consults.
        /// </summary>
        public bool DomainMember
        {
            get
            {
                var security = Global("security", "user");
                if (security.Equals("ADS", StringComparison.OrdinalIgnoreCase) ||
                    security.Equals("DOMAIN", StringComparison.OrdinalIgnoreCase) ||
                    security.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                    return true;

                var passdb = Global("passdb backend", "tdbsam");
                return !passdb.StartsWith("tdbsam", StringComparison.OrdinalIgnoreCase) &&
                       !passdb.StartsWith("smbpasswd", StringComparison.OrdinalIgnoreCase);
            }
        }

        public List<SambaShare> Shares { get; set; } = new();

        public List<SambaUser> Users { get; set; } = new();

        /// <summary>Every unix account, for the permission pickers and for resolving a samba name to a uid.</summary>
        public List<UserAccount> Accounts { get; set; } = new();

        public List<UserGroup> Groups { get; set; } = new();

        /// <summary>The host's own <c>UID_MIN</c>, which is what separates a login account from a system one.</summary>
        public int UidMin { get; set; } = 1000;

        public int UidMax { get; set; } = 60000;

        /// <summary>The same boundary for groups, which is what orders the permission editor's group
        /// dropdown: a host has far more system groups than real ones and they are not what anybody
        /// opening that list is looking for.</summary>
        public int GidMin { get; set; } = 1000;

        public int GidMax { get; set; } = 60000;

        /// <summary>
        /// What the ACL probe answered per share path, carried across refreshes because the probe
        /// writes a file and re-running it every few seconds would churn a directory something else
        /// may be watching. Refreshed after a save or a reapply, which are the two moments the answer
        /// can have changed.
        /// </summary>
        public Dictionary<string, AclVerdict> AclByPath { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The filesystem type under each share path, for wording the refusal.</summary>
        public Dictionary<string, string> FsByPath { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Paths whose SELinux context is not one smbd may serve. Empty where SELinux is off.</summary>
        public HashSet<string> UnlabelledPaths { get; set; } = new(StringComparer.Ordinal);

        public AclVerdict AclFor(string path) =>
            AclByPath.TryGetValue(path, out var verdict) ? verdict : AclVerdict.Unknown;

        public SambaShare? Find(string name) =>
            Shares.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Every section name on the host, ours and samba's own, for refusing a name that is taken.</summary>
        public HashSet<string> SectionNames
        {
            get
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Files)
                    foreach (var line in file.Lines)
                        if (line.Kind == SambaLineKind.Section) names.Add(line.Section);
                return names;
            }
        }
    }
}
