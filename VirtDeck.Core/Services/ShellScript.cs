using System.Text;

namespace VirtDeck.Services
{
    /// <summary>
    /// The four ways this app hands a command to bash on the host, in one place.
    ///
    /// Every one of them exists for the same reason: between here and the remote bash there are one
    /// or two layers that will rewrite quotes (SSH's own exec string, and <c>RunSudoCommand</c>'s
    /// single-quoted <c>bash -c</c> rewrap), so anything built by interpolation is one apostrophe
    /// away from a broken command and one <c>$(...)</c> away from a worse one. Base64 is
    /// [A-Za-z0-9+/=] and survives every one of those layers untouched, which is what makes it the
    /// carrier rather than an obfuscation.
    ///
    /// Which wrapper a script needs is decided by the runner it is going to, and the runners are
    /// deliberately not uniform:
    /// <list type="bullet">
    /// <item><c>RunSudoCommand</c> rewraps in a single-quoted <c>bash -c</c> and sets the locale, so
    /// a payload only has to survive that: <see cref="Wrap"/>.</item>
    /// <item><c>RunCommand</c> gives no shell and no locale at all, so the payload brings its own
    /// bash: <see cref="Wrap"/> again.</item>
    /// <item>Both streaming runners neither escape nor wrap, so a caller spells its own out:
    /// <see cref="SudoWrap"/>.</item>
    /// </list>
    ///
    /// All four also carry <see cref="PathExport"/>, for the same reason each already sets a locale:
    /// what is on the far end is a non-login bash with sshd's bare PATH, not the PATH a person sees
    /// when they log in.
    ///
    /// Nothing here is a substitute for validating a value that has a shape (a container id, a user
    /// name). These helpers stop a value from being read as syntax; they cannot stop it from being
    /// the wrong value.
    /// </summary>
    internal static class ShellScript
    {
        /// <summary>
        /// What every wrapper here puts in front of its payload, and what
        /// <c>SshConnectionManager.RunSudoCommand</c> puts beside its <c>LANG=C</c>.
        ///
        /// <para>A command sent over SSH's exec channel runs in a <b>non-login, non-interactive</b>
        /// bash, which never sources <c>/etc/profile</c>, so the PATH it gets is sshd's bare
        /// default rather than the one a person sees after logging in. On a host that keeps its
        /// tooling outside <c>/usr/bin</c> that is the whole difference between finding a tool and
        /// concluding it is not installed, and the app's answer to "is this module relevant?" is a
        /// <c>command -v</c> loop. Synology DSM is the case that named this: it hands the exec
        /// channel <c>/usr/bin:/bin:/usr/sbin:/sbin</c> and installs the docker CLI in
        /// <c>/usr/local/bin</c>, which only <c>/etc/profile</c> puts on PATH. A source-built
        /// <c>virsh</c> and an Entware host's <c>/opt/bin</c> are the same story.</para>
        ///
        /// <para><b>Appended, never prepended.</b> A host that already has an opinion about which
        /// <c>docker</c> it wants keeps it; this only adds places to look once the host's own are
        /// exhausted, so no host resolves a name differently than it did before.</para>
        ///
        /// <para>It repairs the <i>wrapped</i> paths only. The streaming runners neither escape nor
        /// wrap, so a caller there spells its own out, and the ones that pass a bare command
        /// (<c>virsh</c>, <c>journalctl</c>, the package managers) inherit nothing from this.</para>
        /// </summary>
        internal const string PathExport =
            "export PATH=\"$PATH:/usr/local/sbin:/usr/local/bin:/usr/sbin:/sbin:/opt/bin:/opt/sbin\"";

        /// <summary>
        /// <see cref="PathExport"/> as a statement of its own, for concatenating in front of a
        /// script. Ends in a newline the way <see cref="ArrayFrom"/> does.
        /// </summary>
        internal const string Prologue = PathExport + "\n";

        /// <summary>
        /// Hands a script to bash on the host without the login shell or sudo's single-quote rewrap
        /// getting a say: base64 is [A-Za-z0-9+/=] and survives both untouched.
        ///
        /// Not for a script that reads stdin: piping the script into bash makes that pipe bash's
        /// stdin, so the payload command inherits the exhausted script pipe instead of the SSH
        /// channel. <c>SshConnectionManager.RunPipeInAsync</c> spells out its own
        /// <c>bash -c "$(...)"</c> for exactly that reason.
        /// </summary>
        internal static string Wrap(string script) =>
            $"echo {B64(Prologue + script)} | base64 -d | bash";

        /// <summary>
        /// The wrapper for <c>SshConnectionManager.RunSudoCommandStreaming</c> and
        /// <c>RunCommandStreaming</c>, which unlike <c>RunSudoCommand</c> neither escape their
        /// argument nor wrap it in a shell, so callers spell their own out. Same rule, same shape as
        /// <c>VirshService.SparsifyDisk</c>.
        /// </summary>
        internal static string SudoWrap(string script) =>
            $"bash -c \"$(echo {B64(Prologue + script)} | base64 -d)\"";

        /// <summary>
        /// One remote command built from an argument vector, with nothing quoted and nothing
        /// interpolated. The vector is assembled in C#, joined NUL-separated, base64'd and rebuilt as
        /// a bash array on the host: NUL is the one byte an argv member cannot contain, so the
        /// rejoin is exact however odd the arguments are. <c>read -r -d ''</c> rather than
        /// <c>mapfile -d ''</c>, so nothing depends on the host's bash being 4.4 or newer.
        /// </summary>
        internal static string Argv(IReadOnlyList<string> argv) =>
            ArrayFrom("a", argv) + "\"${a[@]}\"";

        /// <summary>
        /// A named bash array rebuilt on the host from a NUL-separated base64 blob, for a script that
        /// has to loop over user-controlled values rather than run one command built from them.
        /// Ends in a newline, so it reads as a statement of its own in a script being concatenated.
        /// </summary>
        internal static string ArrayFrom(string name, IEnumerable<string> values)
        {
            var blob = string.Concat(values.Select(v => v + "\0"));
            return $"{name}=(); while IFS= read -r -d '' x; do {name}+=(\"$x\"); done " +
                   $"< <(echo {B64(blob)} | base64 -d)\n";
        }

        /// <summary>Base64 of some UTF-8 text, the carrier every wrapper above rides on.</summary>
        internal static string B64(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// The other direction, for a field a script base64'd on its way back out. Answers empty on
        /// anything unparseable, because a listing must not die on one bad record.
        /// </summary>
        internal static string Decode(string b64)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch { return string.Empty; }
        }
    }
}
