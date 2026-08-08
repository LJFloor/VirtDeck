namespace VirtDeck.Unattend
{
    /// <summary>What Windows should do once the last account screen is behind it.</summary>
    public enum FirstLogonMode
    {
        /// <summary>Log on to the first account in the table that is in the Administrators group.</summary>
        FirstAdminAccount,

        /// <summary>Activate the built-in Administrator account, set its password and log on to it.</summary>
        BuiltInAdministrator,

        /// <summary>Leave the sign-in screen up. Setup is finished but nobody is logged on.</summary>
        None,
    }

    public enum PasswordExpiryMode
    {
        /// <summary>Passwords never expire, which is what current NIST guidance asks for.</summary>
        Never,

        /// <summary>Leave Windows' own 42 days alone.</summary>
        WindowsDefault,

        Custom,
    }

    public enum LockoutMode { Default, Disabled, Custom }

    /// <summary>
    /// How the machine gets its first user account. The three are alternatives, not options that stack:
    /// either the answer file names the accounts, or OOBE asks for one, and asking for a Microsoft
    /// account and asking for a local one are different screens.
    /// </summary>
    public enum AccountCreationMode
    {
        /// <summary>Leave OOBE's Microsoft-account screens up and let the user sign in there.</summary>
        MicrosoftAccountInteractive,

        /// <summary>Hide the Microsoft-account screens so OOBE offers the offline account path.</summary>
        LocalAccountInteractive,

        /// <summary>Setup creates the accounts in the table and OOBE asks for nothing.</summary>
        LocalAccounts,
    }

    /// <summary>One row of the local accounts table.</summary>
    public sealed class LocalAccount
    {
        public string Name { get; set; } = "";

        /// <summary>Blank means "same as <see cref="Name"/>"; the generator fills it in.</summary>
        public string DisplayName { get; set; } = "";

        public string Password { get; set; } = "";

        /// <summary>"Administrators" or "Users"; goes into the answer file verbatim.</summary>
        public string Group { get; set; } = GroupUsers;

        public const string GroupAdministrators = "Administrators";
        public const string GroupUsers = "Users";

        /// <summary>A row with no account name is an unused row of the table, not an account.</summary>
        public bool IsEmpty => Name.Trim().Length == 0;

        public bool IsAdministrator =>
            string.Equals(Group, GroupAdministrators, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The "User accounts" tab. See <see cref="UnattendConfigMapper"/> for which generator setting each
    /// property becomes.
    /// </summary>
    public sealed class UserAccountsConfig
    {
        public AccountCreationMode AccountCreation { get; set; } = AccountCreationMode.MicrosoftAccountInteractive;

        /// <summary>Only read under <see cref="AccountCreationMode.LocalAccounts"/>.</summary>
        public List<LocalAccount> Accounts { get; set; } = new();

        /// <summary>Only read under <see cref="AccountCreationMode.LocalAccounts"/>: there is no
        /// predefined account to log on to in the other two modes.</summary>
        public FirstLogonMode FirstLogon { get; set; } = FirstLogonMode.FirstAdminAccount;

        /// <summary>Only meaningful for <see cref="FirstLogonMode.BuiltInAdministrator"/>.</summary>
        public string AdministratorPassword { get; set; } = "";

        public PasswordExpiryMode PasswordExpiry { get; set; } = PasswordExpiryMode.Never;
        public int PasswordExpiryDays { get; set; } = 42;

        // Windows' own defaults since 11 22H2: ten attempts within ten minutes, locked for ten.
        public LockoutMode Lockout { get; set; } = LockoutMode.Default;
        public int LockoutThreshold { get; set; } = 10;
        public int LockoutWindowMinutes { get; set; } = 10;
        public int LockoutDurationMinutes { get; set; } = 10;

        /// <summary>
        /// Base64-encode the passwords in the answer file. Not encryption (anyone can decode it) and
        /// Windows itself does this, but it keeps passwords from being readable over a shoulder.
        /// </summary>
        public bool ObscurePasswords { get; set; }
    }

}
