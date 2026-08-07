using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// Turns an <see cref="UnattendConfig"/> into the bytes of an <c>autounattend.xml</c>.
    ///
    /// Element order is not cosmetic: the unattend schema declares each component's children as a
    /// sequence, and the order used here is the one Windows System Image Manager itself emits
    /// (Password first inside LocalAccount and AutoLogon, Name last). Reordering to taste is how a
    /// hand-written answer file ends up being rejected.
    ///
    /// Everything this builder writes today lives in the <c>oobeSystem</c> pass. The <c>windowsPE</c>
    /// pass (disk layout, edition, product key) belongs to tabs that do not exist yet.
    /// </summary>
    public static class UnattendXmlBuilder
    {
        private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
        private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

        private const string ShellSetup = "Microsoft-Windows-Shell-Setup";
        private const string BuiltInAdministrator = "Administrator";

        /// <summary>Returns UTF-8 bytes without a BOM, which is what Setup's parser expects.</summary>
        public static byte[] Build(UnattendConfig config)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(Ns + "unattend",
                    new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
                    OobeSystem(config.UserAccounts)));

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };

            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, settings)) doc.Save(writer);
            return stream.ToArray();
        }

        /// <summary>The oobeSystem pass: accounts, autologon, the OOBE screens and the policy commands.</summary>
        private static XElement OobeSystem(UserAccountsConfig a)
        {
            // The other two modes hand account creation to OOBE, so there is nothing to name in the
            // answer file and nothing to log on to afterwards.
            bool predefined = a.AccountCreation == AccountCreationMode.LocalAccounts;
            var accounts = predefined ? a.Accounts.Where(x => !x.IsEmpty).ToList() : new List<LocalAccount>();
            var shell = new List<XElement>();

            if (predefined && AutoLogon(a, accounts) is { } autoLogon) shell.Add(autoLogon);
            if (FirstLogonCommands(a) is { } commands) shell.Add(commands);
            shell.Add(Oobe(a));
            if (UserAccounts(a, accounts, predefined) is { } userAccounts) shell.Add(userAccounts);

            return new XElement(Ns + "settings",
                new XAttribute("pass", "oobeSystem"),
                Component(ShellSetup, shell));
        }

        private static XElement? UserAccounts(UserAccountsConfig a, List<LocalAccount> accounts, bool predefined)
        {
            var children = new List<XElement>();

            // Setting a password for the built-in Administrator is also what activates the account.
            if (predefined && a.FirstLogon == FirstLogonMode.BuiltInAdministrator)
                children.Add(Password("AdministratorPassword", a.AdministratorPassword, a.ObscurePasswords));

            if (accounts.Count > 0)
                children.Add(new XElement(Ns + "LocalAccounts", accounts.Select(acc =>
                    new XElement(Ns + "LocalAccount",
                        new XAttribute(Wcm + "action", "add"),
                        Password("Password", acc.Password, a.ObscurePasswords),
                        new XElement(Ns + "DisplayName", DisplayNameOf(acc)),
                        new XElement(Ns + "Group", acc.Group),
                        new XElement(Ns + "Name", acc.Name.Trim())))));

            return children.Count > 0 ? new XElement(Ns + "UserAccounts", children) : null;
        }

        private static string DisplayNameOf(LocalAccount account)
        {
            var display = account.DisplayName.Trim();
            return display.Length > 0 ? display : account.Name.Trim();
        }

        /// <summary>
        /// Logs the chosen account on once, so the machine lands on a desktop rather than a sign-in
        /// screen. LogonCount 1 means the autologon retires itself after that first boot.
        /// </summary>
        private static XElement? AutoLogon(UserAccountsConfig a, List<LocalAccount> accounts)
        {
            string? user = null;
            string password = "";

            if (a.FirstLogon == FirstLogonMode.BuiltInAdministrator)
            {
                user = BuiltInAdministrator;
                password = a.AdministratorPassword;
            }
            else if (a.FirstLogon == FirstLogonMode.FirstAdminAccount &&
                     accounts.FirstOrDefault(x => x.IsAdministrator) is { } first)
            {
                user = first.Name.Trim();
                password = first.Password;
            }

            // No administrator to log on to (an empty table, or every account in Users): fall back to
            // the sign-in screen rather than writing an AutoLogon for an account that does not exist.
            if (user == null) return null;

            return new XElement(Ns + "AutoLogon",
                Password("Password", password, a.ObscurePasswords),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "LogonCount", 1),
                new XElement(Ns + "Username", user));
        }

        /// <summary>
        /// Policies Windows has no unattend element for, applied with <c>net accounts</c> at the first
        /// logon. They therefore do not run at all under <see cref="FirstLogonMode.None"/> until
        /// somebody signs in, which is inherent to the mechanism.
        /// </summary>
        private static XElement? FirstLogonCommands(UserAccountsConfig a)
        {
            var commands = new List<string>();

            switch (a.PasswordExpiry)
            {
                case PasswordExpiryMode.Never:
                    commands.Add("net accounts /maxpwage:unlimited");
                    break;
                case PasswordExpiryMode.Custom:
                    commands.Add($"net accounts /maxpwage:{a.PasswordExpiryDays}");
                    break;
            }

            switch (a.Lockout)
            {
                case LockoutMode.Disabled:
                    commands.Add("net accounts /lockoutthreshold:0");
                    break;
                case LockoutMode.Custom:
                    commands.Add($"net accounts /lockoutthreshold:{a.LockoutThreshold} " +
                                 $"/lockoutwindow:{a.LockoutWindowMinutes} " +
                                 $"/lockoutduration:{a.LockoutDurationMinutes}");
                    break;
            }

            if (commands.Count == 0) return null;

            return new XElement(Ns + "FirstLogonCommands", commands.Select((cmd, i) =>
                new XElement(Ns + "SynchronousCommand",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(Ns + "Order", i + 1),
                    new XElement(Ns + "CommandLine", cmd),
                    new XElement(Ns + "Description", "VirtDeck account policy"),
                    new XElement(Ns + "RequiresUserInput", "false"))));
        }

        /// <summary>
        /// Which OOBE screens survive. Hiding the online account screens is what lets a machine with
        /// local accounts defined walk past account creation unattended, and it is also what turns the
        /// interactive path into the offline one; leaving them up is the Microsoft-account choice.
        /// </summary>
        private static XElement Oobe(UserAccountsConfig a) =>
            new(Ns + "OOBE",
                new XElement(Ns + "HideEULAPage", "true"),
                new XElement(Ns + "HideOnlineAccountScreens",
                    Xml(a.AccountCreation != AccountCreationMode.MicrosoftAccountInteractive)),
                new XElement(Ns + "ProtectYourPC", 3));

        /// <summary>
        /// A password element. Base64 form is what Windows itself writes: UTF-16LE of the password with
        /// the <b>name of the element it sits in</b> appended, which is why the suffix is a parameter and
        /// not a constant. It is obfuscation, not encryption.
        /// </summary>
        private static XElement Password(string element, string password, bool obscure)
        {
            var value = obscure
                ? Convert.ToBase64String(Encoding.Unicode.GetBytes(password + element))
                : password;

            return new XElement(Ns + element,
                new XElement(Ns + "Value", value),
                new XElement(Ns + "PlainText", Xml(!obscure)));
        }

        private static XElement Component(string name, IEnumerable<XElement> content) =>
            new(Ns + "component",
                new XAttribute("name", name),
                new XAttribute("processorArchitecture", "amd64"), // every VM this wizard defines is x86_64
                new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"),
                content);

        /// <summary>XML booleans are lowercase; <c>bool.ToString()</c> is not.</summary>
        private static string Xml(bool value) => value ? "true" : "false";
    }
}
