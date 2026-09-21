using System.Text;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// Whether the host will put itself to sleep, and the two commands that settle it.
    ///
    /// <para><b>Why this exists at all:</b> installing a desktop on a server brings its idle timer
    /// with it, and the first sign of that is the host going away under whoever depended on it.
    /// Nothing announces the setting, so the Overview asks.</para>
    ///
    /// <para><b>The mask is the guarantee and everything else is tidying.</b> A masked target is a
    /// job systemd refuses whoever asks, so masking the sleep targets stops logind, GNOME and a
    /// typed <c>systemctl suspend</c> alike. Turning the source off as well is what stops a desktop
    /// asking every quarter of an hour and being refused; it is done best-effort and each failure is
    /// reported, because a failure there is otherwise invisible: the re-read says Blocked, which is
    /// true, while GNOME keeps trying.</para>
    ///
    /// <para><b>Elevated</b>, because a greeter's and another login's dconf are root's to read and
    /// everything written here is root's to write. The page this feeds already pays one elevated
    /// call per visit for its workload counts.</para>
    /// </summary>
    public class SuspendService(SshConnectionManager ssh)
    {
        private readonly SshConnectionManager _ssh = ssh;

        /// <summary>
        /// Ours, and named so nobody has to guess whose it is. A drop-in rather than an edit to
        /// <c>logind.conf</c>, so the host's own file is never rewritten and removing one file is
        /// the whole of the way back.
        /// </summary>
        private const string DropInDir = "/etc/systemd/logind.conf.d";
        private const string DropIn = DropInDir + "/99-virtdeck-no-suspend.conf";

        /// <summary>The units logind starts to put a machine to sleep, plus the one they all go through.</summary>
        private const string Units =
            "sleep.target suspend.target hibernate.target hybrid-sleep.target suspend-then-hibernate.target";

        /// <summary>GNOME's power plugin, the one desktop setting that matters here.</summary>
        private const string Schema = "org.gnome.settings-daemon.plugins.power";

        // Runs a command as somebody else, from the root shell everything here runs in. runuser
        // first, sudo second: both are on every host this app supports, and neither asks for a
        // password from root. `timeout` because a home directory on a stale NFS mount is a hang and
        // this sits in a page read, which is the argument the sampler's `df` already made.
        private const string AsUser = """
            as_user() {
                au_u=$1; shift
                au_h=$(getent passwd "$au_u" 2>/dev/null | cut -d: -f6)
                [ -n "$au_h" ] && [ -d "$au_h" ] || return 1
                timeout 10 runuser -u "$au_u" -- env HOME="$au_h" "$@" 2>/dev/null ||
                    timeout 10 sudo -n -u "$au_u" env HOME="$au_h" "$@" 2>/dev/null
            }
            """;

        // One round trip, tagged records.
        //
        // `m` is what systemd would refuse, one record per unit this host actually has, so the two
        // commands below mask and unmask exactly those and never name a unit that is not there.
        //
        // `l` is logind's own answer rather than a file's: its config is a main file plus drop-ins
        // in four directories with a merge order, and what it loaded is the only thing worth
        // drawing. One property per call and matched by name, because `busctl` has no `--value` on
        // every systemd we support (255 does not) and a three-property call answers by position.
        // No busctl is no `l` record; the rest of the answer still stands.
        //
        // `g` is where a server that grew a desktop keeps the setting logind never reports. Every
        // login with a session plus the greeter accounts, whose own dconf is what suspends a machine
        // sitting at a login screen. One `gsettings` per user rather than one per key: four keys are
        // four processes and this is a page read.
        private const string ReadBody = $$"""
            export LC_ALL=C
            [ -d /run/systemd/system ] || exit 0

            {{AsUser}}

            for t in {{Units}}; do
                s=$(systemctl is-enabled "$t" 2>/dev/null)
                [ -n "$s" ] && [ "$s" != not-found ] && printf 'm\t%s\t%s\n' "$t" "$s"
            done

            for p in IdleAction IdleActionUSec HandleLidSwitch; do
                v=$(busctl get-property org.freedesktop.login1 /org/freedesktop/login1 \
                        org.freedesktop.login1.Manager "$p" 2>/dev/null) || continue
                printf 'l\t%s\t%s\n' "$p" "$v"
            done
            [ -d /proc/acpi/button/lid ] && printf 'l\tLid\tyes\n'
            for b in /sys/class/power_supply/*; do
                [ "$(cat "$b/type" 2>/dev/null)" = Battery ] && { printf 'l\tBattery\tyes\n'; break; }
            done

            command -v gsettings >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' gsettings

            { loginctl list-users --no-legend 2>/dev/null | awk '{print $2}'
              printf '%s\n' gdm gdm3 sddm lightdm; } | sort -u | while read -r u; do
                [ -n "$u" ] || continue
                as_user "$u" gsettings list-recursively {{Schema}} |
                    awk -v u="$u" '$2 ~ /^sleep-inactive-(ac|battery)-(type|timeout)$/ {
                        printf "g\t%s\t%s\t%s\n", $2, $NF, u }'
            done
            exit 0
            """;

        /// <summary>
        /// What this host has been told to do about sleeping. Cheap enough to ask on every visit to
        /// the page: a handful of <c>systemctl</c> calls and one <c>gsettings</c> per login.
        /// </summary>
        public async Task<SuspendPolicy> ReadAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(ReadBody)), ct);
            return Parse(raw);
        }

        /// <summary>
        /// Stops this host putting itself to sleep, and turns off whatever was asking.
        ///
        /// <para>Answers the list of things that could not be turned off, empty for a clean run.
        /// Only the mask can fail the command outright; the rest is reported and carried on from,
        /// because the mask alone already means the host stays up.</para>
        /// </summary>
        public async Task<IReadOnlyList<string>> DisableAsync(SuspendPolicy policy,
                                                              CancellationToken ct = default)
        {
            var raw = await Task.Run(
                () => _ssh.RunSudoCommand(ShellScript.Wrap(DisableBody(policy))), ct);

            return PackageScripts.Records(raw)
                                 .Where(r => r.Tag == "f")
                                 .Select(r => r.Text.Trim())
                                 .ToList();
        }

        /// <summary>
        /// The command Disable suspend sends, built from what this host actually has: the units it
        /// really carries, the logind keys that were really sleeping, and the desktop timers that
        /// can really fire. A builder of its own, the way <see cref="HostPowerService"/> keeps its
        /// two, so what gets sent can be read without an SSH connection in hand.
        /// </summary>
        private static string DisableBody(SuspendPolicy policy)
        {
            var script = new StringBuilder("export LC_ALL=C\n");
            script.Append(ShellScript.ArrayFrom("u", policy.Targets.Keys));
            script.Append("systemctl mask --no-pager -- \"${u[@]}\" || exit $?\n");

            // Only the keys that were actually sleeping, so a host whose lid was already ignored
            // does not get a line about lids, and a host where logind was never the problem gets no
            // file at all.
            var conf = new List<string>
            {
                "# Written by VirtDeck: this host must not put itself to sleep.",
                "[Login]",
            };
            if (policy.LogindSleeps) conf.Add("IdleAction=ignore");
            if (policy.LidSleeps) conf.Add("HandleLidSwitch=ignore");

            if (conf.Count > 2)
            {
                script.Append(ShellScript.ArrayFrom("c", conf));
                script.Append($$"""
                    err=$( { mkdir -p {{DropInDir}} && printf '%s\n' "${c[@]}" > {{DropIn}}; } 2>&1 ) ||
                        printf 'f\t%s\n' "Could not write {{DropIn}}: ${err:-unknown error}"
                    if [ "$(systemctl is-active systemd-logind 2>/dev/null)" = active ]; then
                        err=$(systemctl reload systemd-logind 2>&1) ||
                            printf 'f\t%s\n' "logind keeps its old settings until the host restarts: $err"
                    fi

                    """);
            }

            // One entry per key to turn off, as two arrays read by the same index: a user name is
            // the host's and never becomes syntax, which is what ArrayFrom is for.
            var users = new List<string>();
            var keys = new List<string>();
            foreach (var desktop in policy.Sleeping)
            {
                users.Add(desktop.User);
                keys.Add(desktop.OnBattery ? "sleep-inactive-battery-type" : "sleep-inactive-ac-type");
            }

            if (users.Count > 0 && policy.HasGsettings)
            {
                script.Append(ShellScript.ArrayFrom("du", users));
                script.Append(ShellScript.ArrayFrom("dk", keys));

                // A write goes through the dconf service, which needs a bus, which is what
                // dbus-run-session is: measured, it activates ca.desrt.dconf and the set lands.
                // $pre is deliberately unquoted: it is our own two words or none, and the one place
                // word splitting is the point. runuser then sudo, the pair the read uses and for
                // the same reason.
                script.Append($$"""
                    pre=
                    command -v dbus-run-session >/dev/null 2>&1 && pre="dbus-run-session --"
                    for i in "${!du[@]}"; do
                        du_u=${du[$i]}; du_k=${dk[$i]}
                        du_h=$(getent passwd "$du_u" 2>/dev/null | cut -d: -f6)
                        if [ -z "$du_h" ]; then
                            printf 'f\t%s\n' "$du_u has no home directory, so GNOME's timer was left alone."
                            continue
                        fi
                        err=$( { timeout 20 runuser -u "$du_u" -- env HOME="$du_h" \
                                     $pre gsettings set {{Schema}} "$du_k" "'nothing'" ||
                                 timeout 20 sudo -n -u "$du_u" env HOME="$du_h" \
                                     $pre gsettings set {{Schema}} "$du_k" "'nothing'"; } 2>&1 ) && continue
                        # dbus-run-session's own daemon writes two lines to stderr when it works, so
                        # what it says is not an error message and is not reported as one.
                        err=$(printf '%s\n' "$err" | grep -v '^dbus-daemon\[' | tail -n 2 | tr '\n' ' ')
                        printf 'f\t%s\n' "GNOME's $du_k for $du_u: ${err:-could not be changed}"
                    done

                    """);
            }

            script.Append("exit 0\n");
            return script.ToString();
        }

        /// <summary>
        /// Lets the host sleep again: unmasks the same units and removes our drop-in.
        ///
        /// <para>It does <b>not</b> put a desktop's own timer back, because that timer was the
        /// problem and nothing recorded what it was. The button says so.</para>
        /// </summary>
        public Task AllowAsync(SuspendPolicy policy, CancellationToken ct = default) =>
            Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(AllowBody(policy))), ct);

        /// <summary>The command behind Allow suspend, kept beside <see cref="DisableBody"/>.</summary>
        private static string AllowBody(SuspendPolicy policy)
        {
            return "export LC_ALL=C\n" +
                   ShellScript.ArrayFrom("u", policy.Targets.Keys) +
                   "systemctl unmask --no-pager -- \"${u[@]}\" || exit $?\n" +
                   $"rm -f {DropIn}\n" +
                   """
                   if [ "$(systemctl is-active systemd-logind 2>/dev/null)" = active ]; then
                       systemctl reload systemd-logind >/dev/null 2>&1 || true
                   fi
                   exit 0
                   """;
        }

        // ---- Reading the answer -------------------------------------------------

        private static SuspendPolicy Parse(string raw)
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            var settings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            string idle = "", lid = "";
            var after = TimeSpan.Zero;
            bool hasLid = false, hasBattery = false, hasGsettings = false;

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "m":
                    {
                        var parts = text.Split('\t', 2);
                        if (parts.Length == 2) targets[parts[0]] = parts[1].Trim();
                        break;
                    }
                    case "l":
                    {
                        var parts = text.Split('\t', 2);
                        if (parts.Length != 2) break;
                        switch (parts[0])
                        {
                            case "IdleAction": idle = Quoted(parts[1]); break;
                            case "IdleActionUSec": after = Microseconds(parts[1]); break;
                            case "HandleLidSwitch": lid = Quoted(parts[1]); break;
                            case "Lid": hasLid = true; break;
                            case "Battery": hasBattery = true; break;
                        }
                        break;
                    }
                    case "v":
                        hasGsettings = true;
                        break;
                    case "g":
                    {
                        // Key, value, then the user: the unbounded field goes last, so a name with a
                        // space in it is still one field.
                        var parts = text.Split('\t', 3);
                        if (parts.Length != 3) break;
                        var user = parts[2].Trim();
                        if (user.Length == 0) break;
                        if (!settings.TryGetValue(user, out var values))
                            settings[user] = values = new Dictionary<string, string>(StringComparer.Ordinal);
                        values[parts[0]] = parts[1].Trim();
                        break;
                    }
                }
            }

            return new SuspendPolicy
            {
                Targets = targets,
                IdleAction = idle,
                IdleAfter = after,
                LidAction = lid,
                HasLid = hasLid,
                HasBattery = hasBattery,
                HasGsettings = hasGsettings,
                Desktops = Desktops(settings),
            };
        }

        /// <summary>
        /// GNOME's four keys per user as at most two answers: mains and battery. A pair with no
        /// action is not an answer and is dropped, rather than being carried as an empty one.
        /// </summary>
        private static List<DesktopSleep> Desktops(Dictionary<string, Dictionary<string, string>> settings)
        {
            var found = new List<DesktopSleep>();

            foreach (var (user, values) in settings.OrderBy(p => p.Key, StringComparer.Ordinal))
                foreach (var battery in new[] { false, true })
                {
                    var prefix = battery ? "sleep-inactive-battery-" : "sleep-inactive-ac-";
                    if (!values.TryGetValue(prefix + "type", out var action)) continue;

                    action = Quoted(action);
                    if (action.Length == 0) continue;

                    values.TryGetValue(prefix + "timeout", out var timeout);
                    found.Add(new DesktopSleep(user, battery, action, Seconds(timeout)));
                }

            return found;
        }

        /// <summary>
        /// The text inside the quotes, which is how both <c>busctl</c> (<c>s "ignore"</c>) and
        /// <c>gsettings</c> (<c>'suspend'</c>) state a string. Anything unquoted is already the
        /// value, which is what a plain enum word looks like on an older gsettings.
        /// </summary>
        private static string Quoted(string raw)
        {
            var text = raw.Trim();
            foreach (var quote in new[] { '"', '\'' })
            {
                int open = text.IndexOf(quote), close = text.LastIndexOf(quote);
                if (open >= 0 && close > open) return text[(open + 1)..close];
            }
            return text;
        }

        /// <summary>
        /// A <c>busctl</c> uint64 of microseconds, as <c>t 1800000000</c>. Zero for never, which is
        /// what logind's own infinity (<c>UINT64_MAX</c>) means and what an unreadable value has to
        /// answer: a delay nobody stated must not become a delay of no time at all.
        /// </summary>
        private static TimeSpan Microseconds(string raw)
        {
            var token = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (!ulong.TryParse(token, out var usec) || usec == 0) return TimeSpan.Zero;
            if (usec > (ulong)(TimeSpan.MaxValue.Ticks / 10)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(usec / 1000.0);
        }

        /// <summary>GNOME's timeout, in seconds. Zero is its own "never", whatever the action says.</summary>
        private static TimeSpan Seconds(string? raw) =>
            int.TryParse((raw ?? "").Trim(), out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
    }
}
