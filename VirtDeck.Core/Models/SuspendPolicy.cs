namespace VirtDeck.Models
{
    /// <summary>
    /// One desktop session's idle timer, as GNOME's own settings state it. Two per user at most:
    /// the machine may be on mains or on battery and GNOME keeps a separate answer for each.
    /// </summary>
    /// <param name="User">Whose settings these are. A greeter account (gdm) counts, and on a server
    /// that grew a desktop it is usually the only one.</param>
    /// <param name="OnBattery">The battery pair rather than the mains pair.</param>
    /// <param name="Action">What GNOME does: <c>suspend</c>, <c>hibernate</c>, <c>blank</c>,
    /// <c>nothing</c>, <c>logout</c>, <c>shutdown</c>, <c>interactive</c>.</param>
    /// <param name="After">How long idle first. GNOME reads zero as "never", whatever the action
    /// beside it says.</param>
    public sealed record DesktopSleep(string User, bool OnBattery, string Action, TimeSpan After)
    {
        public bool Sleeps => SuspendPolicy.IsSleep(Action) && After > TimeSpan.Zero;
    }

    /// <summary>
    /// Whether this host can put itself to sleep, and what would do it.
    ///
    /// <para><b>Two separate questions, and the row on the Overview needs both.</b> Almost every
    /// Linux host <i>can</i> suspend, so "can" on its own is not news and warning about it would put
    /// an amber line on every machine. What is news is something on the host actually asking for it:
    /// logind's idle action, a lid, or a desktop's own timer. And what settles it either way is
    /// whether the sleep targets are masked, because a masked target is a job systemd refuses
    /// whoever asks.</para>
    /// </summary>
    public sealed record SuspendPolicy
    {
        /// <summary>
        /// The sleep units this host has, keyed by unit name, holding <c>systemctl is-enabled</c>'s
        /// word for each (<c>static</c>, <c>masked</c>, <c>masked-runtime</c>). A unit the host does
        /// not have is absent rather than present with a placeholder, which is what keeps the mask
        /// and unmask commands to the units that exist.
        /// </summary>
        public IReadOnlyDictionary<string, string> Targets { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>logind's <c>IdleAction</c>, as logind itself reports it rather than as a file spells it.</summary>
        public string IdleAction { get; init; } = "";

        /// <summary>How long idle before <see cref="IdleAction"/>. Zero is logind's own "never".</summary>
        public TimeSpan IdleAfter { get; init; }

        /// <summary>logind's <c>HandleLidSwitch</c>. Only meaningful where <see cref="HasLid"/>.</summary>
        public string LidAction { get; init; } = "";

        /// <summary>Whether the machine has a lid to close. A server does not, and is not told about one.</summary>
        public bool HasLid { get; init; }

        /// <summary>
        /// Whether the machine has a battery. Same rule and same reason as <see cref="HasLid"/>: a
        /// desktop's battery timer can never fire, so it is not something to warn about or turn off.
        /// </summary>
        public bool HasBattery { get; init; }

        /// <summary>Every desktop idle timer found, sleeping or not. Only the sleeping ones are acted on.</summary>
        public IReadOnlyList<DesktopSleep> Desktops { get; init; } = [];

        /// <summary>Whether <c>gsettings</c> is on the host, so a desktop timer can be turned off as well as read.</summary>
        public bool HasGsettings { get; init; }

        /// <summary>
        /// Nothing was asked or nothing answered: no systemd, or a read that found no sleep unit at
        /// all. The row is not drawn, which is the absent-tooling rule as it applies to one fact.
        /// </summary>
        public bool Known => Targets.Count > 0;

        /// <summary>
        /// The host will refuse to sleep, whoever asks.
        ///
        /// <para>Two clauses, because there are two recipes in the wild and a host somebody already
        /// fixed by hand must not be warned at. <c>sleep.target</c> alone is decisive:
        /// <c>systemd-suspend.service</c> carries <c>Requires=sleep.target</c>, so masking that one
        /// unit breaks every path through it. The widely copied recipe instead masks the four
        /// targets logind starts, which is the second clause. Presence is required for either claim:
        /// a read that came back empty says "not blocked", never "blocked".</para>
        /// </summary>
        public bool Blocked =>
            Masked("sleep.target") ||
            (Masked("suspend.target") && Masked("hibernate.target") && Masked("hybrid-sleep.target"));

        /// <summary>logind's own idle timer will sleep this host.</summary>
        public bool LogindSleeps => IsSleep(IdleAction) && IdleAfter > TimeSpan.Zero;

        /// <summary>There is a lid, and closing it sleeps this host.</summary>
        public bool LidSleeps => HasLid && IsSleep(LidAction);

        /// <summary>
        /// The desktop timers that would actually fire on this machine: the ones set to sleep, minus
        /// a battery timer on something that has no battery.
        /// </summary>
        public IReadOnlyList<DesktopSleep> Sleeping =>
            Desktops.Where(d => d.Sleeps && (HasBattery || !d.OnBattery)).ToList();

        /// <summary>Whose desktop timer would sleep this host, each named once.</summary>
        public IReadOnlyList<string> SleepingUsers =>
            Sleeping.Select(d => d.User).Distinct(StringComparer.Ordinal).ToList();

        private bool Masked(string unit) =>
            Targets.TryGetValue(unit, out var state) &&
            state.StartsWith("masked", StringComparison.Ordinal);

        /// <summary>
        /// Everything on this host that would put it to sleep, most of the host first. Each one is
        /// said twice: the short half is what the row carries when there is only one of them, and
        /// the long half names whose setting it is, which is what the tooltip is for. Empty on a
        /// host where nothing asks, which together with <see cref="Blocked"/> is what decides
        /// whether the Overview draws the row at all.
        /// </summary>
        public IReadOnlyList<(string Short, string Detail)> Sources
        {
            get
            {
                var found = new List<(string, string)>();

                if (LogindSleeps)
                {
                    var line = $"logind {Verb(IdleAction)} this host after {Idle(IdleAfter)} idle";
                    found.Add((line, line + "."));
                }

                // Only where there is a lid to close. Nothing about HandleLidSwitch is worth saying
                // on a machine that has no lid, whatever it is set to.
                if (LidSleeps)
                {
                    var line = $"Closing the lid {Verb(LidAction)} this host";
                    found.Add((line, line + "."));
                }

                // Per user, and then per distinct answer: GNOME ships the same action and delay for
                // mains and for battery, and saying that twice would turn one setting into two
                // findings. Only a machine where the two really differ gets two lines, and only
                // then does either of them have to say which is which.
                foreach (var user in Sleeping.GroupBy(d => d.User, StringComparer.Ordinal))
                    foreach (var same in user.GroupBy(d => (d.Action, d.After)))
                    {
                        var first = same.First();
                        var battery = first.OnBattery && same.Count() == 1;
                        var line = $"GNOME {Verb(first.Action)} this host after {Idle(first.After)} idle" +
                                   (battery ? " on battery" : "");
                        found.Add((line, $"{line}, for {user.Key}."));
                    }

                return found;
            }
        }

        /// <summary>
        /// The one line the Status row carries. A single source is named outright, because that is
        /// the answer somebody acts on; several are counted, and the tooltip lists them.
        /// </summary>
        public string Summary()
        {
            if (Blocked) return "Blocked";

            var sources = Sources;
            return sources.Count switch
            {
                0 => "",
                1 => sources[0].Short,
                _ => $"{sources.Count} settings put this host to sleep",
            };
        }

        /// <summary>
        /// The paragraph behind the row: what would do it, or what is stopping it. A host that is
        /// blocked while something still asks says both, because "Blocked" alone would leave the
        /// second half looking like it had been dealt with.
        /// </summary>
        public string Detail()
        {
            var sources = Sources;

            if (!Blocked)
                return string.Join("\n", sources.Select(s => s.Detail));

            var masked = Targets.Where(t => t.Value.StartsWith("masked", StringComparison.Ordinal))
                                .Select(t => t.Key)
                                .ToList();

            var text = masked.Count > 0
                ? $"Masked, so systemd refuses to sleep: {string.Join(", ", masked)}."
                : "systemd refuses to sleep.";

            if (sources.Count > 0)
                text += "\n\nStill asking, and refused:\n" +
                        string.Join("\n", sources.Select(s => s.Detail));

            return text;
        }

        /// <summary>The actions that are a sleep. <c>shutdown</c> is not one: it is a different problem.</summary>
        internal static bool IsSleep(string action) =>
            action is "suspend" or "hibernate" or "hybrid-sleep" or "suspend-then-hibernate";

        private static string Verb(string action) =>
            action == "hibernate" ? "hibernates" : "suspends";

        /// <summary>
        /// An idle delay as somebody would say it. Minutes up to an hour and a half, which covers
        /// every default anything ships with, and hours past that.
        /// </summary>
        private static string Idle(TimeSpan after) =>
            after.TotalMinutes < 1 ? $"{(int)after.TotalSeconds} s"
            : after.TotalMinutes < 90 ? $"{(int)Math.Round(after.TotalMinutes)} min"
            : after.Minutes == 0 ? $"{(int)after.TotalHours} h"
            : $"{(int)after.TotalHours} h {after.Minutes} min";
    }
}
