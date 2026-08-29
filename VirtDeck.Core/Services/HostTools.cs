using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// One un-elevated round trip asking a host what it says it is and which of a named set of
    /// tools are on it.
    ///
    /// <para>This is the <see cref="PackageService"/> probe, lifted so the shell can run it too.
    /// It started life pointed at the four package managers; the module side menu now asks the same
    /// question about <c>virsh</c>, <c>docker</c> and <c>systemctl</c> to decide which of its
    /// modules belong on this host at all, and a second copy of a <c>command -v</c> loop is exactly
    /// the kind of thing that drifts. The caller names the tools, so nothing here knows what any of
    /// them are for.</para>
    ///
    /// <para>It has to live in Core and be public because <see cref="ShellScript.Wrap"/> and
    /// <c>PackageScripts.Records</c> are both internal to this assembly, so the Avalonia shell
    /// cannot assemble or read this script itself.</para>
    /// </summary>
    public static class HostTools
    {
        // One round trip, un-elevated, in the tagged-record idiom.
        //
        // The version is asked for here rather than on demand because it is one `--version` per tool
        // found, which is a handful on any real host, and asking later would mean a second round
        // trip before the status bar could say anything.
        //
        // `. /etc/os-release` is fenced: a host without the file is an ordinary case (it is not
        // universal), and the tool search below still answers on its own.
        private const string ProbeBody = """
            export LC_ALL=C
            . /etc/os-release 2>/dev/null
            printf 'o\t%s\t%s\n' "${ID:-}" "${ID_LIKE:-}"
            for m in TOOLS; do
              p=$(command -v "$m" 2>/dev/null) || continue
              printf 'v\t%s\t%s\n' "$m" "$("$p" --version 2>/dev/null | head -n 1)"
            done
            exit 0
            """;

        /// <summary>
        /// Asks the host which of <paramref name="tools"/> it has. Never latches anything: the
        /// answer belongs to the caller, because "a host that had nothing when VirtDeck connected
        /// may have something now" is true of every caller and each keeps its own reading current
        /// in its own way.
        /// </summary>
        public static async Task<HostToolset> ProbeAsync(
            SshConnectionManager ssh, IEnumerable<string> tools, CancellationToken ct = default)
        {
            var names = tools.Distinct(StringComparer.Ordinal).ToList();
            if (names.Count == 0) return HostToolset.Empty;

            var raw = await Task.Run(
                () => ssh.RunCommand(ShellScript.Wrap(
                    ProbeBody.Replace("TOOLS", string.Join(' ', names)))), ct);

            return Parse(raw);
        }

        internal static HostToolset Parse(string raw)
        {
            var id = string.Empty;
            var idLike = string.Empty;
            var tools = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (tag, text) in Updates.PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "o":
                    {
                        var f = text.Split('\t', 2);
                        id = f[0].Trim();
                        idLike = f.Length > 1 ? f[1].Trim() : string.Empty;
                        break;
                    }

                    case "v":
                    {
                        // A tool that answered nothing to --version is still installed, so the name is
                        // what matters and an empty version is a real value. Recording only the ones
                        // that printed something would hide a working manager behind a quiet binary.
                        var f = text.Split('\t', 2);
                        if (f[0].Trim() is { Length: > 0 } name)
                            tools[name] = f.Length > 1 ? f[1].Trim() : string.Empty;
                        break;
                    }
                }
            }

            return new HostToolset(id, idLike, tools);
        }
    }
}
