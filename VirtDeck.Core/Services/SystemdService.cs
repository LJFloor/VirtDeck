using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's systemd units, driven the way <see cref="DockerService"/> drives docker and
    /// <see cref="UserAccountService"/> drives shadow-utils: the CLI over the shared SSH connection,
    /// one round trip per listing, mutators that throw and queries that answer a value.
    ///
    /// <b>The two scopes do not run the same way, and that is the one fact in this file worth
    /// knowing.</b> Listing is unprivileged in both, because <c>systemctl list-units</c> asks the
    /// manager a question it answers for anybody, so it goes through <c>RunCommand</c> like the file
    /// explorer's listings rather than through sudo like everything else in the app. Mutating the
    /// system manager needs root and goes through <c>RunSudoCommand</c>. Mutating the <b>user</b>
    /// manager must never do: <c>sudo systemctl --user</c> addresses <i>root's</i> user manager, not
    /// the logged-in user's, so it would silently act on the wrong tree and report success.
    ///
    /// A user manager that is not running (no session, no lingering) is a stated failure carried on
    /// the catalog, not an exception, because the module has to draw it.
    /// </summary>
    public class SystemdService
    {
        private readonly SshConnectionManager _ssh;

        public SystemdService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        /// <summary>The last catalog read per scope, so a tab switch draws before the round trip lands.</summary>
        private readonly Dictionary<UnitScope, UnitCatalog> _catalogs = new()
        {
            [UnitScope.System] = new UnitCatalog { Scope = UnitScope.System },
            [UnitScope.User] = new UnitCatalog { Scope = UnitScope.User },
        };

        public UnitCatalog Catalog(UnitScope scope) => _catalogs[scope];

        // ---- Reading -------------------------------------------------------

        // The preamble both read scripts share.
        //
        // XDG_RUNTIME_DIR is what makes `--user` work at all over a non-interactive SSH session:
        // systemctl finds the user bus at $XDG_RUNTIME_DIR/bus, and a command channel has no session
        // to inherit it from. Harmless for --system, so it is exported unconditionally rather than
        // making the two scripts differ by more than the flag.
        private const string Preamble = """
            export LC_ALL=C
            export XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}

            """;

        // The cheap read, and the one the poll runs. `list-units` costs about 13 ms on a host with
        // 200 service units, against the 1.6 s the full catalog below costs, so splitting the two is
        // what lets this module refresh every few seconds instead of every ten and still hold the
        // shared SSH lock for a fraction as long.
        //
        // Three record kinds, a strict subset of the catalog's, so Parse handles both with no
        // branch: `v` the version, `e` a stated listing failure, `u` a list-units line. What it
        // cannot answer is anything about unit *files*, which is why SystemdService.ReadStateAsync
        // carries the previous catalog's file states forward rather than letting Parse's answer stand
        // on its own.
        private const string StateScript = Preamble + """
            v=$(systemctl --version 2>/dev/null | head -n 1)
            [ -n "$v" ] || exit 0
            printf 'v\t%s\n' "$v"

            u=$(systemctl SCOPE list-units --type=service --all --plain --no-legend --no-pager 2>&1) || {
              printf 'e\t%s\n' "$(printf '%s' "$u" | head -n 1)"
              exit 0
            }
            printf '%s\n' "$u" | while IFS= read -r l; do
              [ -n "$l" ] && printf 'u\t%s\n' "$l"
            done

            exit 0
            """;

        // One round trip per scope, in UserAccountService.LoadScript's shape: a tag in field 0, real
        // tab characters, LC_ALL=C so the words below are the ones systemd prints, every
        // best-effort half fenced with 2>/dev/null so it cannot take the exit status with it, and a
        // closing `exit 0`.
        //
        // <b>This is the expensive one, and it is run on demand rather than on a timer.</b> Measured
        // over SSH on a host with 204 service units: list-units 13 ms, list-unit-files <b>1150 ms</b>,
        // the batched show 460 ms. The two slow halves answer what a unit does at *boot*, which
        // changes only when somebody enables, disables, masks or unmasks one, so they are read on the
        // first look at a scope, on the Refresh button, and when a state pass reports a name this has
        // never described. Our own enable and disable do not need one either: ShowAsync reads
        // UnitFileState back for the units it touched.
        //
        // The version is printed first and separately, so "there is no systemd here" stays a
        // different answer from "this scope could not be listed". Only list-units can fail the
        // listing, and its failure is captured as an `e` record rather than an exit code, because a
        // user manager that is not running is an ordinary state.
        //
        // Five record kinds: `v` the version, `e` a stated listing failure, `u` a list-units line,
        // `f` a list-unit-files line, and `r` one `show` block per loaded unit. The show block is
        // reassembled into a single record on the host, so nothing on this side depends on the order
        // systemctl printed its properties in, and it is one round trip rather than one per row:
        // per-row round trips were the VM list's original latency problem.
        //
        // That `show` is handed the names list-units just gave rather than the obvious
        // `show '*.service'`, and the difference is not cosmetic: a pattern there expands over
        // *active* units only, so on this machine the glob answered for 80 units out of 204 and
        // every inactive one came back with no file state at all. Word splitting is what turns the
        // name list into arguments, which is safe because a unit name cannot contain whitespace
        // (systemd escapes it into \x20), with globbing switched off around it so a name is never
        // read as a pattern.
        private const string CatalogScript = Preamble + """
            v=$(systemctl --version 2>/dev/null | head -n 1)
            [ -n "$v" ] || exit 0
            printf 'v\t%s\n' "$v"

            u=$(systemctl SCOPE list-units --type=service --all --plain --no-legend --no-pager 2>&1) || {
              printf 'e\t%s\n' "$(printf '%s' "$u" | head -n 1)"
              exit 0
            }
            printf '%s\n' "$u" | while IFS= read -r l; do
              [ -n "$l" ] && printf 'u\t%s\n' "$l"
            done

            systemctl SCOPE list-unit-files --type=service --no-legend --no-pager 2>/dev/null |
              while IFS= read -r l; do
                [ -n "$l" ] && printf 'f\t%s\n' "$l"
              done

            set -f
            n=$(printf '%s\n' "$u" | while IFS= read -r l; do
                  [ -n "$l" ] && printf '%s\n' "${l%% *}"
                done)
            { [ -n "$n" ] && systemctl SCOPE show $n \
                --property=Id,CanReload,UnitFileState --no-pager 2>/dev/null; echo; } |
              while IFS= read -r l; do
                case "$l" in
                  Id=*)            i=${l#Id=} ;;
                  CanReload=*)     c=${l#CanReload=} ;;
                  UnitFileState=*) f=${l#UnitFileState=} ;;
                  '')              [ -n "$i" ] && printf 'r\t%s\t%s\t%s\n' "$i" "$c" "$f"; i=; c=; f= ;;
                esac
              done
            set +f

            exit 0
            """;

        /// <summary>
        /// Reads one scope in full, in one round trip, and caches it. Un-elevated in both scopes:
        /// listing needs no privilege, and asking for it would put a sudo prompt in front of a read.
        ///
        /// The expensive pass. See <see cref="CatalogScript"/> for what it costs and when it is worth
        /// paying; the poll runs <see cref="ReadStateAsync"/> instead.
        /// </summary>
        public async Task<UnitCatalog> LoadAsync(UnitScope scope, CancellationToken ct = default)
        {
            var catalog = await Task.Run(() => Fetch(scope), ct);
            _catalogs[scope] = catalog;
            return catalog;
        }

        private UnitCatalog Fetch(UnitScope scope)
        {
            var raw = _ssh.RunCommand(ShellScript.Wrap(For(CatalogScript, scope)));
            var catalog = Parse(raw, scope);
            Diagnostics.SpiceLog.Log(
                $"[systemd] {Flag(scope)} catalog: {catalog.Units.Count} services, tool='{catalog.ToolVersion}'" +
                (catalog.ListFailure.Length > 0 ? $", failed: {catalog.ListFailure}" : ""));
            return catalog;
        }

        /// <summary>
        /// The cheap read behind the poll: what every loaded unit is doing right now, over the file
        /// states the last catalog pass established. About 13 ms of host work against the catalog
        /// pass's 1.6 s, which is what makes a five second poll cost less than the ten second one it
        /// replaced.
        ///
        /// Un-elevated in both scopes, like <see cref="LoadAsync"/>.
        /// </summary>
        public async Task<StateReading> ReadStateAsync(UnitScope scope, CancellationToken ct = default)
        {
            var reading = await Task.Run(() => FetchState(scope), ct);
            _catalogs[scope] = reading.Catalog;
            return reading;
        }

        private StateReading FetchState(UnitScope scope)
        {
            var raw = _ssh.RunCommand(ShellScript.Wrap(For(StateScript, scope)));
            var fresh = Parse(raw, scope);

            // Nothing to merge onto, so the answer stands on its own and the module is told to go and
            // read a catalog. Same for a scope that could not be read at all: the failure is what has
            // to be drawn, and carrying stale units under it would be worse than an empty table.
            var cached = _catalogs[scope];
            if (cached.Units.Count == 0 || !fresh.Available || fresh.ListFailure.Length > 0)
                return new StateReading(fresh, UnknownUnits: true);

            var known = cached.Units.ToDictionary(u => u.Name, StringComparer.Ordinal);
            var unknown = false;

            foreach (var unit in fresh.Units)
            {
                if (known.Remove(unit.Name, out var old))
                {
                    // list-units answers what the unit is doing; only the catalog pass knows what it
                    // will do at boot, so those two fields come forward untouched.
                    unit.FileState = old.FileState;
                    unit.CanReload = old.CanReload;
                }
                else unknown = true;
            }

            // What is left in `known` was in the catalog and is not loaded now: either it never was
            // (most units on a host have a file and have never been looked at) or systemd has since
            // unloaded it. Both mean the same thing, and it is what an empty ActiveState says, so the
            // run-time half is cleared and the file half kept.
            foreach (var stale in known.Values)
            {
                stale.LoadState = string.Empty;
                stale.ActiveState = string.Empty;
                stale.SubState = string.Empty;
                fresh.Units.Add(stale);
            }

            fresh.Units.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new StateReading(fresh, unknown);
        }

        // ---- One unit at a time ---------------------------------------------

        // What a command's own refresh reads: every field SystemdUnit carries, for the handful of
        // units the command touched. About 10 ms for one unit, against a catalog pass's 1.6 s, which
        // is the difference between a row that answers as the command returns and one that waits for
        // a reload of the whole scope.
        //
        // UnitFileState is in the list on purpose: it is what enable, disable, mask and unmask move,
        // so our own file-state changes need no catalog pass either. The block is reassembled on the
        // host into one record per unit, exactly as the catalog script's `r` records are, so nothing
        // here depends on the order systemctl chose to print the properties in.
        private const string ShowScript = Preamble + """
            NAMES
            [ ${#n[@]} -gt 0 ] || exit 0
            { systemctl SCOPE show --no-pager \
                --property=Id,Description,LoadState,ActiveState,SubState,CanReload,UnitFileState \
                -- "${n[@]}" 2>/dev/null; echo; } |
              while IFS= read -r l; do
                case "$l" in
                  Id=*)            i=${l#Id=} ;;
                  Description=*)   d=${l#Description=} ;;
                  LoadState=*)     o=${l#LoadState=} ;;
                  ActiveState=*)   a=${l#ActiveState=} ;;
                  SubState=*)      s=${l#SubState=} ;;
                  CanReload=*)     c=${l#CanReload=} ;;
                  UnitFileState=*) f=${l#UnitFileState=} ;;
                  '')              [ -n "$i" ] && printf 's\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
                                     "$i" "$o" "$a" "$s" "$c" "$f" "$d"
                                   i=; d=; o=; a=; s=; c=; f= ;;
                esac
              done

            exit 0
            """;

        /// <summary>
        /// Reads a few named units and folds the answer into the cached catalog. What every command
        /// calls instead of reloading the scope it was issued in.
        ///
        /// Un-elevated in both scopes, like the two listings: <c>systemctl show</c> answers anybody.
        /// </summary>
        public async Task<IReadOnlyList<SystemdUnit>> ShowAsync(
            UnitScope scope, IReadOnlyList<string> names, CancellationToken ct = default)
        {
            foreach (var name in names) RequireUnit(name);
            if (names.Count == 0) return Array.Empty<SystemdUnit>();

            var script = For(ShowScript, scope).Replace("NAMES", ShellScript.ArrayFrom("n", names).TrimEnd('\n'));
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);
            return Fold(scope, raw);
        }

        /// <summary>
        /// Turns the <c>s</c> records into units and writes them back over the cached catalog, so the
        /// next state pass carries the new file states forward rather than the ones the command just
        /// changed.
        /// </summary>
        private IReadOnlyList<SystemdUnit> Fold(UnitScope scope, string raw)
        {
            var cached = _catalogs[scope].Units.ToDictionary(u => u.Name, StringComparer.Ordinal);
            var read = new List<SystemdUnit>();

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                if (!line.StartsWith("s\t", StringComparison.Ordinal)) continue;

                var f = line[2..].Split('\t', 7);
                if (f.Length < 7 || !Wanted(f[0])) continue;

                cached.TryGetValue(f[0], out var old);
                var unit = old ?? new SystemdUnit { Name = f[0] };
                unit.LoadState = f[1];
                unit.ActiveState = f[2];
                unit.SubState = f[3];
                unit.Description = f[6];

                // Only where the host answered. SystemdUnit.CanReload defaults true so a host too old
                // to have the property leaves Reload enabled and lets systemd refuse in its own words,
                // and an empty field here must not turn that into a silent no.
                if (f[4] is { Length: > 0 } can) unit.CanReload = can == "yes";

                // An empty answer is "this unit has no file", which is a real state for a transient
                // or generated unit but is also what show says about a name it could not resolve.
                // The catalog's word is the better one where there is one, and the same override rule
                // the catalog pass itself applies at the bottom of Parse.
                if (f[5] is { Length: > 0 } state) unit.FileState = state;

                if (old is null) _catalogs[scope].Units.Add(unit);
                read.Add(unit);
            }

            return read;
        }

        private static string For(string script, UnitScope scope) => script.Replace("SCOPE", Flag(scope));

        private static string Flag(UnitScope scope) => scope == UnitScope.User ? "--user" : "--system";

        internal static UnitCatalog Parse(string raw, UnitScope scope)
        {
            var catalog = new UnitCatalog { Scope = scope };
            var units = new Dictionary<string, SystemdUnit>(StringComparer.Ordinal);
            var reload = new Dictionary<string, bool>(StringComparer.Ordinal);
            var loaded = new HashSet<string>(StringComparer.Ordinal);

            // What `systemctl show` says about a loaded unit's file state, which overrides the
            // listing's. It is the only source that can answer for a template *instance*:
            // list-unit-files knows getty@.service and has never heard of getty@tty1.service, so
            // without this an instance would show no autostart state and refuse to be enabled,
            // while `systemctl enable getty@tty1.service` works perfectly well.
            var fileState = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "u":
                    {
                        // UNIT LOAD ACTIVE SUB DESCRIPTION, column-aligned. Split on whitespace with
                        // a cap of five, so the description is the unbounded remainder and a
                        // description containing runs of spaces survives. Same rule as
                        // RemoteFileService.Parse's unbounded-field-last.
                        var f = rest.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length < 4 || !Wanted(f[0])) continue;
                        var unit = At(units, f[0]);
                        unit.LoadState = f[1];
                        unit.ActiveState = f[2];
                        unit.SubState = f[3];
                        unit.Description = f.Length > 4 ? f[4] : "";
                        loaded.Add(f[0]);
                        break;
                    }

                    case "f":
                    {
                        // UNIT_FILE STATE [PRESET].
                        var f = rest.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length < 2 || !Wanted(f[0])) continue;

                        // An alias is the same unit under a second name; it is already listed under
                        // its real one, so showing it would offer two rows that are one service.
                        if (string.Equals(f[1], "alias", StringComparison.Ordinal)) continue;

                        At(units, f[0]).FileState = f[1];
                        break;
                    }

                    case "r":
                    {
                        // id, CanReload, UnitFileState. The block is reassembled on the host rather
                        // than here, so one self-contained record arrives per unit and nothing
                        // depends on the order systemctl chose to print the properties in.
                        var f = rest.Split('\t', 3);
                        if (f.Length < 3 || !Wanted(f[0])) continue;
                        reload[f[0]] = f[1].Trim() == "yes";
                        if (f[2].Trim() is { Length: > 0 } state) fileState[f[0]] = state;
                        break;
                    }

                    case "v":
                        catalog.ToolVersion = rest.Trim();
                        break;

                    case "e":
                        catalog.ListFailure = rest.Trim();
                        break;
                }
            }

            foreach (var unit in units.Values)
            {
                // Only loaded units have an answer; the rest keep the permissive default and let
                // systemd refuse a reload in its own words.
                if (reload.TryGetValue(unit.Name, out var can)) unit.CanReload = can;
                if (fileState.TryGetValue(unit.Name, out var state)) unit.FileState = state;

                // Two ways a name reaches here without being a service anybody can manage: something
                // referenced it and systemd could not resolve it (not-found), or it appeared in
                // neither listing's real content. Either way there is no file to enable and no unit
                // to start, so it is a dangling reference rather than a row.
                if (unit.FileState.Length == 0 &&
                    (!loaded.Contains(unit.Name) ||
                     string.Equals(unit.LoadState, "not-found", StringComparison.Ordinal)))
                    continue;

                catalog.Units.Add(unit);
            }

            catalog.Units.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return catalog;
        }

        private static SystemdUnit At(Dictionary<string, SystemdUnit> units, string name)
        {
            if (units.TryGetValue(name, out var unit)) return unit;
            return units[name] = new SystemdUnit { Name = name };
        }

        /// <summary>
        /// Whether a name from either listing is a service this module can act on.
        ///
        /// A template (<c>getty@.service</c>) is not a unit: it is the pattern instances are named
        /// from, and every command against one fails, because there is no instance. Its instances
        /// (<c>getty@tty1.service</c>) are listed in their own right and are what somebody actually
        /// starts.
        /// </summary>
        private static bool Wanted(string name) =>
            name.EndsWith(".service", StringComparison.Ordinal) &&
            !name.EndsWith("@.service", StringComparison.Ordinal);

        // ---- Names ---------------------------------------------------------

        /// <summary>
        /// The floor every unit name reaching an argv has to clear: something, on one line, that
        /// cannot be mistaken for an option, and that is a service.
        ///
        /// Deliberately permissive about the rest of it, the way
        /// <c>UserAccountService.RequireSafe</c> is. Nothing here ever creates a unit, so there is
        /// only the addressing question, and a name already on the host may hold almost anything:
        /// systemd escapes device paths into unit names, so
        /// <c>systemd-fsck@dev-disk-by\x2duuid-3083\x2dE537.service</c> is an ordinary unit on an
        /// ordinary machine. Addressing is safe without a strict rule anyway, because
        /// <see cref="ShellScript.Argv"/> means a name is never read as syntax and every vector
        /// below carries a literal <c>--</c> before it, so it cannot be read as an option either.
        /// </summary>
        private static void RequireUnit(string name)
        {
            if (name.Length == 0)
                throw new ArgumentException("No unit was given.");
            if (name.IndexOfAny(new[] { '\0', '\n', '\r' }) >= 0)
                throw new ArgumentException("A unit name cannot contain a line break.");
            if (name[0] == '-')
                throw new ArgumentException("A unit name cannot start with a hyphen.");
            if (!name.EndsWith(".service", StringComparison.Ordinal))
                throw new ArgumentException($"'{name}' is not a service unit.");
        }

        // ---- Writing -------------------------------------------------------

        /// <summary>
        /// The one place the scope decides how a command runs. The system manager needs root; the
        /// user manager needs the user, and putting sudo in front of it would address root's.
        /// </summary>
        private string RunArgv(UnitScope scope, IReadOnlyList<string> argv)
        {
            // A user-scope command finds its manager the same way the listing does, and has to bring
            // its own bash with it: RunCommand gives no shell and no locale, where RunSudoCommand
            // rewraps in bash -c and exports one itself. That is why only this half is wrapped.
            var script = ShellScript.Argv(argv);
            return scope == UnitScope.User
                ? _ssh.RunCommand(ShellScript.Wrap(Preamble + script))
                : _ssh.RunSudoCommand(script);
        }

        /// <summary>
        /// The <c>systemctl</c> vector for one verb over one unit. Pure and separate, so it can be
        /// read without an SSH connection, exactly as <c>UserAccountService.BuildCreateArgv</c> is.
        /// </summary>
        internal static List<string> BuildArgv(UnitScope scope, string verb, string unit) =>
            new() { "systemctl", Flag(scope), "--no-pager", verb, "--", unit };

        private Task RunAsync(UnitScope scope, string verb, string unit, CancellationToken ct)
        {
            RequireUnit(unit);
            Diagnostics.SpiceLog.Log($"[systemd] {Flag(scope)} {verb} {unit}");
            return Task.Run(() => RunArgv(scope, BuildArgv(scope, verb, unit)), ct);
        }

        public Task StartAsync(UnitScope scope, string unit, CancellationToken ct = default) =>
            RunAsync(scope, "start", unit, ct);

        public Task StopAsync(UnitScope scope, string unit, CancellationToken ct = default) =>
            RunAsync(scope, "stop", unit, ct);

        public Task RestartAsync(UnitScope scope, string unit, CancellationToken ct = default) =>
            RunAsync(scope, "restart", unit, ct);

        public Task ReloadAsync(UnitScope scope, string unit, CancellationToken ct = default) =>
            RunAsync(scope, "reload", unit, ct);

        /// <summary>
        /// Changes what the unit does at boot and nothing else: no <c>--now</c>, so enabling never
        /// starts anything and disabling never stops anything that is running. The two questions are
        /// separate commands here because they are separate questions.
        /// </summary>
        public Task SetEnabledAsync(UnitScope scope, string unit, bool enabled, CancellationToken ct = default) =>
            RunAsync(scope, enabled ? "enable" : "disable", unit, ct);

        /// <summary>
        /// Masking points the unit at <c>/dev/null</c>, so it cannot be started at all, by anything,
        /// including as a dependency of something else. Stronger than disable and far easier to
        /// forget, which is why the module confirms it.
        /// </summary>
        public Task SetMaskedAsync(UnitScope scope, string unit, bool masked, CancellationToken ct = default) =>
            RunAsync(scope, masked ? "mask" : "unmask", unit, ct);

        // ---- Events --------------------------------------------------------

        /// <summary>
        /// Raised on a background thread when a unit in that scope started, stopped, failed or
        /// reloaded. Carries no payload beyond the scope: like <c>VirshService.DomainEventReceived</c>,
        /// the only thing a listener does with it is refresh.
        /// </summary>
        public event Action<UnitScope>? UnitEventReceived;

        private readonly Dictionary<UnitScope, CancellationTokenSource> _tails = new();

        // The journal, not D-Bus, and the reason is that neither D-Bus route works from a CLI.
        // `busctl monitor` needs BecomeMonitor, which the system bus grants to root alone, and a bus
        // monitor is not a Manager.Subscribe() subscriber, so systemd need not emit the unit signals
        // for it to see in the first place. journalctl ships with systemd, needs no subscription, and
        // PID 1 logs every unit job under a stable MESSAGE_ID.
        //
        // The seven ids are the whole of the unit job vocabulary, verified against a live systemd 255
        // host along with the `+` disjunction syntax: start begun, start finished, stop begun, stop
        // finished, start failed, reload begun, reload finished.
        //
        // stdbuf -oL for the reason `virsh event` needs it and `docker events` does not: journalctl
        // is C and writes through stdio, which block-buffers into a pipe.
        private const string TailScript =
            "stdbuf -oL journalctl SCOPE --follow --lines=0 --output=cat --no-pager " +
            "MESSAGE_ID=7d4958e842da4a758f6c1cdc7b36dcc5 + " +
            "MESSAGE_ID=39f53479d3a045ac8e11786248231fbf + " +
            "MESSAGE_ID=de5b426a63be47a7b6ac3eaac82e2f6f + " +
            "MESSAGE_ID=9d1aaa27d60140bd96365438aad20286 + " +
            "MESSAGE_ID=be02cf6855d2428ba40df7e9d022f03d + " +
            "MESSAGE_ID=d34d037fff1847e6ae669a370e694725 + " +
            "MESSAGE_ID=7b05ebc668384222baa8881179cfda54";

        /// <summary>
        /// Starts the journal tail for one scope, on a connection of its own so it never holds the
        /// shared command lock. Idempotent, and self-healing: if the tail dies it is restarted after
        /// three seconds, because events are an optimisation and the poll is still the guarantee.
        ///
        /// <b>The system scope is elevated and the user scope must never be.</b> Not because reading
        /// the journal is privileged in principle, but because it is not guaranteed: an account
        /// outside <c>adm</c> and <c>systemd-journal</c> gets its own entries and nothing else from
        /// <c>journalctl --system</c>, and it gets them <i>silently</i>, so an unprivileged system
        /// tail would look exactly like a host on which nothing ever happens. The user scope is the
        /// same rule <see cref="RunArgv"/> lives by from the other end: <c>sudo journalctl --user</c>
        /// would read root's journal, not the logged-in user's.
        /// </summary>
        public void StartEventListener(UnitScope scope)
        {
            if (_tails.ContainsKey(scope)) return;

            var cts = new CancellationTokenSource();
            _tails[scope] = cts;
            var ct = cts.Token;
            var command = ShellScript.SudoWrap(Preamble + TailScript.Replace("SCOPE", Flag(scope)));

            Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (scope == UnitScope.User) _ssh.RunCommandStreaming(command, OnLine, ct);
                        else _ssh.RunSudoCommandStreaming(command, OnLine, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[systemd] {Flag(scope)} tail: {ex.Message}");
                    }

                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);

            Diagnostics.SpiceLog.Log($"[systemd] {Flag(scope)} tail started");
            return;

            // The payload is ignored except for one substring test. Under LC_ALL=C the message names
            // the unit ("Started ssh.service - OpenBSD Secure Shell server."), and every SSH
            // connection VirtDeck opens logs a session-N.scope line through these same message ids,
            // so without this our own connections would each trigger a refresh.
            void OnLine(string line)
            {
                if (line.Contains(".service", StringComparison.Ordinal)) UnitEventReceived?.Invoke(scope);
            }
        }

        /// <summary>
        /// Ends every tail. Called from the module's <c>Shutdown</c> and not from its
        /// <c>Deactivate</c>: a tail holds its own connection, so a hidden module costs nothing by
        /// leaving it up, and reconnecting one on every module switch would cost far more than the
        /// events are worth. The same exception the virsh and docker tails already take.
        /// </summary>
        public void StopEventListener()
        {
            foreach (var cts in _tails.Values)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
            _tails.Clear();
        }

        // ---- Capability ----------------------------------------------------

        /// <summary>
        /// What the right-hand status slot says. An empty tool version is the "not installed"
        /// answer and is what makes the module explain an empty list rather than just showing one.
        ///
        /// Not per scope, because <c>systemctl --version</c> is the same binary answering either
        /// way: whichever scope has been read knows it, and asking about one that has not been would
        /// say "not found" about a host that plainly has systemd on it.
        /// </summary>
        public string CapabilityText =>
            _catalogs.Values.FirstOrDefault(c => c.Available)?.ToolVersion ?? "systemd not found";
    }
}
