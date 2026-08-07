using System.Text;
using System.Xml.Linq;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// The result of reading an answer file: the settings that could be represented, plus what had to
    /// be left behind. The warnings are not diagnostics for us, they are the honest answer to "did my
    /// file survive this?": everything listed is in the file, is not in the UI, and will be gone from
    /// whatever the window writes next.
    /// </summary>
    public sealed class UnattendImportResult
    {
        public required UnattendConfig Config { get; init; }

        /// <summary>One line per thing in the file the window cannot hold. Empty when nothing was lost.</summary>
        public required IReadOnlyList<string> Warnings { get; init; }
    }

    /// <summary>
    /// Reads an <c>autounattend.xml</c> back into an <see cref="UnattendConfig"/>. The inverse of
    /// <see cref="UnattendXmlBuilder"/>, and deliberately only that far: the window models one section
    /// of one pass, so anything else in the file (the windowsPE pass, other components, elements no tab
    /// exists for yet) is reported and dropped rather than silently carried, because the window cannot
    /// write back what it cannot show.
    ///
    /// Matching ignores namespaces. Every real answer file declares
    /// <c>urn:schemas-microsoft-com:unattend</c>, but a hand-edited one that lost the declaration is
    /// still obviously the file the user meant to open, and refusing it teaches nothing.
    /// </summary>
    public static class UnattendXmlReader
    {
        private const string ShellSetup = "Microsoft-Windows-Shell-Setup";
        private const string BuiltInAdministrator = "Administrator";

        /// <summary>Children of the Shell-Setup component this reader understands.</summary>
        private static readonly string[] KnownShellElements =
            { "AutoLogon", "FirstLogonCommands", "OOBE", "UserAccounts" };

        /// <summary>
        /// Parses answer-file bytes. Throws <see cref="InvalidDataException"/> when the file is not an
        /// answer file at all; a file that is one but says nothing this window models parses to
        /// defaults with warnings, which is a different thing and must not be an error.
        /// </summary>
        public static UnattendImportResult Parse(byte[] xml)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(Encoding.UTF8.GetString(StripBom(xml)));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The file is not valid XML: " + ex.Message, ex);
            }

            var root = doc.Root;
            if (root == null || root.Name.LocalName != "unattend")
                throw new InvalidDataException(
                    "The file is XML but not an answer file: its root element is " +
                    (root == null ? "missing" : "<" + root.Name.LocalName + ">") + " rather than <unattend>.");

            var warnings = new List<string>();
            var config = new UnattendConfig();

            var oobe = root.Elements().Where(e => e.Name.LocalName == "settings")
                           .FirstOrDefault(e => (string?)e.Attribute("pass") == "oobeSystem");

            foreach (var pass in root.Elements().Where(e => e.Name.LocalName == "settings" && e != oobe))
                warnings.Add($"The '{(string?)pass.Attribute("pass") ?? "unnamed"}' pass was not imported; " +
                             "this window only covers the oobeSystem pass.");

            if (oobe == null)
            {
                warnings.Add("The file has no oobeSystem pass, so nothing on this page was filled in.");
                return new UnattendImportResult { Config = config, Warnings = warnings };
            }

            var components = oobe.Elements().Where(e => e.Name.LocalName == "component").ToList();
            var shell = components.FirstOrDefault(e => (string?)e.Attribute("name") == ShellSetup);

            foreach (var other in components.Where(e => e != shell))
                warnings.Add($"Component '{(string?)other.Attribute("name") ?? "unnamed"}' was not imported.");

            if (shell == null)
            {
                warnings.Add($"The oobeSystem pass has no {ShellSetup} component, so nothing on this page " +
                             "was filled in.");
                return new UnattendImportResult { Config = config, Warnings = warnings };
            }

            ReadUserAccounts(shell, config.UserAccounts, warnings);

            foreach (var name in shell.Elements().Select(e => e.Name.LocalName).Distinct()
                                      .Where(n => !KnownShellElements.Contains(n)))
                warnings.Add($"<{name}> was not imported; there is no page for it yet.");

            return new UnattendImportResult { Config = config, Warnings = warnings };
        }

        private static void ReadUserAccounts(XElement shell, UserAccountsConfig a, List<string> warnings)
        {
            var userAccounts = Child(shell, "UserAccounts");
            var localAccounts = userAccounts == null ? null : Child(userAccounts, "LocalAccounts");

            // Obscured passwords are a property of the file, not of any one account: PlainText false
            // anywhere means the file was written that way, and the checkbox is what reproduces it.
            a.ObscurePasswords = shell.Descendants().Any(e => e.Name.LocalName == "PlainText" &&
                                                              IsFalse(e.Value));

            a.Accounts = (localAccounts?.Elements().Where(e => e.Name.LocalName == "LocalAccount")
                                       .Select(e => ReadAccount(e, warnings))
                                       .Where(x => !x.IsEmpty).ToList())
                         ?? new List<LocalAccount>();

            // Accounts named in the file is the whole point of the third mode; with none named, the
            // OOBE screens are the only thing left to tell the two interactive modes apart.
            a.AccountCreation =
                a.Accounts.Count > 0 ? AccountCreationMode.LocalAccounts :
                IsTrue(Value(Child(shell, "OOBE"), "HideOnlineAccountScreens"))
                    ? AccountCreationMode.LocalAccountInteractive
                    : AccountCreationMode.MicrosoftAccountInteractive;

            ReadFirstLogon(shell, userAccounts, a, warnings);
            ReadPolicies(shell, a, warnings);
        }

        private static LocalAccount ReadAccount(XElement element, List<string> warnings)
        {
            var name = Value(element, "Name")?.Trim() ?? "";
            var group = Value(element, "Group")?.Trim() ?? "";
            var display = Value(element, "DisplayName")?.Trim() ?? "";

            // The table offers two groups. Anything else (a comma-separated list, a custom group) is
            // outside what the page can show, so say so rather than quietly demoting the account.
            if (group.Length > 0 &&
                !group.Equals(LocalAccount.GroupAdministrators, StringComparison.OrdinalIgnoreCase) &&
                !group.Equals(LocalAccount.GroupUsers, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Account '{name}' is in group '{group}', which the table cannot show; " +
                             $"it was imported as {LocalAccount.GroupUsers}.");
                group = "";
            }

            return new LocalAccount
            {
                Name = name,
                // The builder fills a blank display name in from the account name; folding that back
                // keeps the box empty rather than showing a value the user never typed.
                DisplayName = display.Equals(name, StringComparison.Ordinal) ? "" : display,
                Password = ReadPassword(element, "Password", warnings),
                Group = group.Length > 0 ? group : LocalAccount.GroupUsers,
            };
        }

        /// <summary>
        /// Which account Setup logs on to. The builder only ever writes an AutoLogon for the built-in
        /// Administrator or for the first administrator in the table, so a username that is neither is
        /// a file we did not write: it imports as the nearest option and says so.
        /// </summary>
        private static void ReadFirstLogon(XElement shell, XElement? userAccounts, UserAccountsConfig a,
                                           List<string> warnings)
        {
            var autoLogon = Child(shell, "AutoLogon");
            var user = Value(autoLogon, "Username")?.Trim() ?? "";
            bool enabled = autoLogon != null && !IsFalse(Value(autoLogon, "Enabled"));

            if (!enabled)
            {
                a.FirstLogon = FirstLogonMode.None;
                return;
            }

            if (user.Equals(BuiltInAdministrator, StringComparison.OrdinalIgnoreCase))
            {
                a.FirstLogon = FirstLogonMode.BuiltInAdministrator;
                // The password lives in UserAccounts (setting it is what activates the account); the
                // copy inside AutoLogon is the same secret and stands in when that element is absent.
                a.AdministratorPassword = userAccounts != null
                    ? ReadPassword(userAccounts, "AdministratorPassword", warnings)
                    : ReadPassword(autoLogon!, "Password", warnings);
                return;
            }

            a.FirstLogon = FirstLogonMode.FirstAdminAccount;

            var firstAdmin = a.Accounts.FirstOrDefault(x => x.IsAdministrator);
            if (firstAdmin == null || !firstAdmin.Name.Trim().Equals(user, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"The file logs on as '{user}', which is not the first administrator in the " +
                             "table; the first administrator will be used instead.");
        }

        /// <summary>
        /// The two policies that have no unattend element and travel as <c>net accounts</c> calls. An
        /// absent call is Windows' own default, not the window's default of Never.
        /// </summary>
        private static void ReadPolicies(XElement shell, UserAccountsConfig a, List<string> warnings)
        {
            a.PasswordExpiry = PasswordExpiryMode.WindowsDefault;
            a.Lockout = LockoutMode.Default;

            var commands = Child(shell, "FirstLogonCommands");
            if (commands == null) return;

            foreach (var command in commands.Elements().Where(e => e.Name.LocalName == "SynchronousCommand")
                                            .Select(e => Value(e, "CommandLine")?.Trim() ?? "")
                                            .Where(c => c.Length > 0))
            {
                var switches = NetAccountsSwitches(command);
                if (switches == null)
                {
                    warnings.Add($"First-logon command '{command}' was not imported; this page only holds " +
                                 "the account policies.");
                    continue;
                }

                if (switches.TryGetValue("maxpwage", out var age))
                {
                    if (age.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
                        a.PasswordExpiry = PasswordExpiryMode.Never;
                    else if (int.TryParse(age, out var days) && days > 0)
                    {
                        a.PasswordExpiry = PasswordExpiryMode.Custom;
                        a.PasswordExpiryDays = days;
                    }
                }

                if (switches.TryGetValue("lockoutthreshold", out var threshold) &&
                    int.TryParse(threshold, out var count))
                {
                    // Zero attempts is how "never lock out" is spelled, so it is the disabled option
                    // rather than a custom threshold of none.
                    if (count == 0) a.Lockout = LockoutMode.Disabled;
                    else
                    {
                        a.Lockout = LockoutMode.Custom;
                        a.LockoutThreshold = count;
                        if (switches.TryGetValue("lockoutwindow", out var w) && int.TryParse(w, out var window))
                            a.LockoutWindowMinutes = window;
                        if (switches.TryGetValue("lockoutduration", out var d) && int.TryParse(d, out var duration))
                            a.LockoutDurationMinutes = duration;
                    }
                }
            }
        }

        /// <summary>
        /// Splits a <c>net accounts /switch:value</c> command into its switches, or null when the
        /// command is something else entirely.
        /// </summary>
        private static Dictionary<string, string>? NetAccountsSwitches(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !parts[0].Equals("net", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("accounts", StringComparison.OrdinalIgnoreCase))
                return null;

            var switches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Skip(2))
            {
                if (part.Length == 0 || (part[0] != '/' && part[0] != '-')) continue;
                var colon = part.IndexOf(':');
                if (colon < 0) continue;
                switches[part[1..colon]] = part[(colon + 1)..];
            }
            return switches;
        }

        /// <summary>
        /// Reads a password element. Base64 form is UTF-16LE of the password with the name of the
        /// element it sits in appended, so the suffix comes off here exactly as the builder puts it on.
        /// </summary>
        private static string ReadPassword(XElement parent, string element, List<string> warnings)
        {
            var password = Child(parent, element);
            if (password == null) return "";

            // PlainText is per element, so it decides on its own: the checkbox only says how the window
            // will write the file back out, and a file may well mix the two.
            var value = Value(password, "Value") ?? "";
            if (value.Length == 0 || !IsFalse(Value(password, "PlainText"))) return value;

            try
            {
                var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(value));
                return decoded.EndsWith(element, StringComparison.Ordinal)
                    ? decoded[..^element.Length]
                    : decoded;
            }
            catch (FormatException)
            {
                warnings.Add($"<{element}> is marked as encoded but is not valid Base64; it was imported " +
                             "as typed.");
                return value;
            }
        }

        private static XElement? Child(XElement? parent, string name) =>
            parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

        private static string? Value(XElement? parent, string name) => Child(parent, name)?.Value.Trim();

        private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        private static bool IsFalse(string? value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A BOM in front of the declaration makes <see cref="XDocument.Parse(string)"/> throw. Setup's
        /// own parser does not want one either, but plenty of editors write one.
        /// </summary>
        private static byte[] StripBom(byte[] bytes) =>
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes[3..]
                : bytes;
    }
}
