using System.Text;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The accounts samba authenticates, which are not the accounts the host logs in.
    ///
    /// <para><b>SMB carries an NTLM hash, and no unix password database holds one</b>, so there is
    /// no arrangement under which <c>/etc/shadow</c> could be samba's authentication backend and
    /// this tab could be skipped. What samba keeps instead is its own passdb, and every name in it
    /// still has to resolve to a uid, because a file on the share is owned by a unix user and by
    /// nothing else. So a share account is two things at once: a locked system account with no
    /// password and no shell, and an entry in samba's passdb that does have a password.</para>
    ///
    /// <para>Split from <see cref="SambaService"/> for the reason <see cref="UserAccountService"/>
    /// is split from the rest: this file speaks shadow-utils and passdb, that one speaks smb.conf
    /// and ACLs, and nothing reads better for having both in one place.</para>
    ///
    /// <para>One rule runs through the whole file, the same one
    /// <see cref="UserAccountService.SetPasswordAsync"/> states: <b>a password never touches a
    /// command line.</b> <c>RunSudoCommand</c> puts its argument where <c>ps</c> shows it to every
    /// local user, base64 or not, so every password here goes over stdin.</para>
    /// </summary>
    public class SambaUserService(SshConnectionManager ssh)
    {
        private readonly SshConnectionManager _ssh = ssh;

        /// <summary>
        /// Creates or adopts an account and gives it a Samba password.
        ///
        /// <para>Ordering is not incidental. <c>useradd</c> comes first because <c>smbpasswd -a</c>
        /// calls <c>getpwnam</c> and refuses a name the passwd database does not know. The lock comes
        /// last, and only on an account this app created, because
        /// <c>smbpasswd</c> under Debian's stock <c>unix password sync = yes</c> also drives
        /// <c>passwd</c> and would otherwise have quietly given a "locked" account a real unix
        /// password to log in with.</para>
        ///
        /// <para>From the moment <c>useradd</c> returns, the account exists, so a later step failing
        /// is reported as what it is rather than as the whole thing having failed. That is
        /// <see cref="UserAccountService.CreateUserAsync"/>'s rule and it holds for the same reason.</para>
        /// </summary>
        /// <param name="nologinShell">The shell the host actually has, from the listing. Never assumed.</param>
        public async Task CreateAsync(string name, string password, bool createAccount,
                                      string nologinShell, CancellationToken ct = default)
        {
            if (createAccount && !UserAccountService.IsValidNewUserName(name))
                throw new ArgumentException(
                    $"'{name}' is not a valid user name: start with a letter or underscore, " +
                    "then letters, digits, underscores and hyphens.");

            RequireSafe(name);
            RequirePassword(password);

            if (createAccount)
            {
                var shell = nologinShell.Trim() is { Length: > 0 } s ? s : "/bin/false";

                // --system, so the uid lands below UID_MIN, there is no password aging, and the
                // shadow entry's password field is '!' from the start. --no-create-home, because
                // nothing ever logs in as this account and an empty /home directory per share user
                // is litter. The GECOS marker is what makes deletion this app's to do later.
                await Run(ct, "useradd", "--system", "--no-create-home", "--shell", shell,
                          "-c", SambaService.AccountMarker, "--", name);
                Diagnostics.SpiceLog.Log($"[samba] created account {name}");
            }

            try
            {
                await SetPasswordAsync(name, password, add: true, ct);
            }
            catch (Exception ex) when (createAccount)
            {
                throw new Exception(
                    $"{name} was created on the host, but its Samba password could not be set, so it " +
                    $"cannot sign in to a shared folder yet.\n\n{ex.Message}", ex);
            }

            // Only ever on an account this app made. Locking one that was already on the host would
            // take somebody's login away as a side effect of giving them a share.
            if (createAccount)
                await Run(ct, "usermod", "--lock", "--", name);
        }

        /// <summary>
        /// Sets the Samba password, over stdin. <c>smbpasswd -s</c> reads the new password and its
        /// retype and prompts for neither, and as root it does not ask for the old one.
        /// </summary>
        public async Task SetPasswordAsync(string name, string password, bool add = false,
                                           CancellationToken ct = default)
        {
            RequireSafe(name);
            RequirePassword(password);

            var argv = add
                ? new[] { "smbpasswd", "-s", "-a", "--", name }
                : new[] { "smbpasswd", "-s", "--", name };

            try
            {
                await _ssh.RunPipeInAsync(ShellScript.Argv(argv), elevated: true, async (stdin, token) =>
                {
                    var bytes = Encoding.UTF8.GetBytes(password + "\n" + password + "\n");
                    await stdin.WriteAsync(bytes, token);
                    await stdin.FlushAsync(token);
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new Exception($"The Samba password could not be set: {Reason(ex.Message)}", ex);
            }

            Diagnostics.SpiceLog.Log($"[samba] samba password set for {name}");
        }

        /// <summary>
        /// Puts the unix lock back on an account VirtDeck created, after anything that may have
        /// lifted it.
        ///
        /// <para>Not redundant, and this is the whole reason it is a method of its own: Debian's
        /// stock <c>smb.conf</c> sets <c>unix password sync = yes</c>, under which <c>smbpasswd</c>
        /// also drives <c>/usr/bin/passwd</c> and quietly gives a "locked" account a real unix
        /// password to log in with. Only ever called on an account this app created, because
        /// locking somebody's own login as a side effect of a share password would be a bug with
        /// consequences outside this module.</para>
        /// </summary>
        public Task RelockAsync(string name, CancellationToken ct = default)
        {
            RequireSafe(name);
            return Run(ct, "usermod", "--lock", "--", name);
        }

        /// <summary>
        /// Disables or enables a passdb entry. A disabled account keeps its hash and is refused at
        /// logon, which is what makes this reversible without asking for the password again.
        /// </summary>
        public Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default)
        {
            RequireSafe(name);
            Diagnostics.SpiceLog.Log($"[samba] {(enabled ? "enable" : "disable")} {name}");
            return Run(ct, "smbpasswd", enabled ? "-e" : "-d", "--", name);
        }

        /// <summary>
        /// Removes the passdb entry, and the unix account with it only when VirtDeck created that
        /// account. No <c>-r</c>: a share account has no home directory to remove.
        /// </summary>
        public async Task DeleteAsync(string name, bool removeAccount, CancellationToken ct = default)
        {
            RequireSafe(name);
            Diagnostics.SpiceLog.Log($"[samba] delete {name} (account: {removeAccount})");

            await Run(ct, "smbpasswd", "-x", "--", name);
            if (removeAccount)
                await Run(ct, "userdel", "--", name);
        }

        // ---- Plumbing --------------------------------------------------------

        private Task Run(CancellationToken ct, params string[] argv) =>
            Task.Run(() => _ssh.RunSudoCommand(ShellScript.Argv(argv)), ct);

        /// <summary>
        /// The floor every value reaching an argv has to clear, the same one
        /// <see cref="UserAccountService"/> keeps: something, on one line, that cannot be mistaken
        /// for an option. Addressing rather than creating, because a samba account already on the
        /// host may be called anything the host allowed.
        /// </summary>
        private static void RequireSafe(string name)
        {
            if (name.Trim().Length == 0)
                throw new ArgumentException("No user name was given.");
            if (name.IndexOfAny(['\0', '\n', '\r', '\t', ':']) >= 0)
                throw new ArgumentException($"'{name}' is not a user name.");
            if (name[0] == '-')
                throw new ArgumentException("A user name cannot start with a hyphen.");
        }

        private static void RequirePassword(string password)
        {
            if (password.Length == 0)
                throw new ArgumentException("A share user with no password cannot sign in, so one is required.");
            if (password.IndexOfAny(['\n', '\r', '\0']) >= 0)
                throw new ArgumentException("A password cannot contain a line break.");
        }

        /// <summary>Strips the transfer primitive's framing off a message so the tool's own words lead.</summary>
        private static string Reason(string message)
        {
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            var text = at >= 0 ? message[(at + 3)..] : message;
            return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
        }
    }
}
