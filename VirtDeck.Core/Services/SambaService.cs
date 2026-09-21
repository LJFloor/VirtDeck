using System.Globalization;
using System.Text;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// A write refused because the file moved under it. Carries the host's current text so the
    /// module can offer to reload rather than only say that something changed.
    /// </summary>
    public sealed class SambaConflictException(string path, string hostText)
        : Exception($"{path} changed on the host since it was read.")
    {
        public string Path { get; } = path;
        public string HostText { get; } = hostText;
    }

    /// <summary>
    /// A config samba's own parser rejected. Carries testparm's words, which are the useful half.
    /// The file has already been put back by the time this is thrown.
    /// </summary>
    public sealed class SambaConfigException(string detail)
        : Exception("Samba would not accept this configuration.")
    {
        public string Detail { get; } = detail;
    }

    /// <summary>How far a permissions pass has got, so the status slot can say something true.</summary>
    public readonly record struct AclProgress(long Done, long Total, long Failed, bool Counting);

    /// <summary>
    /// The host's Samba, over the shared SSH connection.
    ///
    /// <para><b>This module edits the host's own configuration, not a VirtDeck-owned subset.</b>
    /// Nothing in this app puts a marker in smb.conf, so there is no marker of ours to recognise and
    /// no "ours versus theirs" to draw: every share on the page is a foreign share. That is what
    /// makes the byte-exact round trip in <see cref="SambaConfig"/> the load-bearing part of this
    /// module, exactly as <c>CronFile</c>'s is of the cron one.</para>
    ///
    /// <para><b>smb.conf and every file it includes are read and written as separate files</b>, each
    /// with its own digest, the way the cron module holds <c>/etc/crontab</c>, the <c>cron.d</c>
    /// drop-ins and each user crontab. An include naming a path with a <c>%</c> macro in it resolves
    /// per connection, per client or per user, so there is no one file behind it; that is reported
    /// rather than guessed at.</para>
    ///
    /// <para><b>Always elevated</b>, the way <see cref="CronService"/> and
    /// <see cref="UserAccountService"/> are. <c>/etc/samba</c> is root-owned, <c>pdbedit</c> reads a
    /// 0600 tdb, and every mutation here needs root whatever happens. One privileged path, no
    /// retry-as-root to carry.</para>
    /// </summary>
    public class SambaService(SshConnectionManager ssh)
    {
        private readonly SshConnectionManager _ssh = ssh;

        /// <summary>How much of one config file the listing will carry. An smb.conf is a few
        /// kilobytes; anything past this is not a file anybody is editing in a dialog.</summary>
        private const int ListingCap = 512 * 1024;

        /// <summary>How many paths one <c>setfacl</c> exec is given. A million-file tree is then a few
        /// thousand processes rather than a million, and a batch is still small enough that a failure
        /// names a useful number.</summary>
        private const int BatchSize = 200;

        /// <summary>The last listing, so the module and its dialogs share one answer and a dialog costs
        /// no round trip of its own.</summary>
        public SambaCatalog Catalog { get; private set; } = new();

        /// <summary>
        /// What the ACL probe has already answered, carried across refreshes.
        ///
        /// <para>The probe is the only honest test there is: ext4 and xfs carry ACLs with no mount
        /// option to read back, zfs carries them only with <c>acltype=posixacl</c>, and nfs, exfat
        /// and ntfs-3g answer <c>getfacl</c> perfectly well while supporting nothing. So it creates a
        /// dot file, runs <c>setfacl</c> on it and removes it. Doing that on every Refresh would
        /// churn a directory something else may be watching with inotify, so the answer is kept for
        /// the session and re-asked only after a save or a reapply.</para>
        /// </summary>
        private readonly Dictionary<string, AclVerdict> _aclCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fsCache = new(StringComparer.Ordinal);

        // ---- Watching --------------------------------------------------------

        private readonly HostFileWatcher _watcher = new(ssh, "samba", elevated: true);

        /// <summary>
        /// Raised when smb.conf or one of its includes moved on the host, whoever moved it. Nothing
        /// in samba announces a configuration change, so this is a loop on the host rather than an
        /// event tail: see <see cref="HostFileWatcher"/>.
        /// </summary>
        public event Action? ConfigChanged
        {
            add => _watcher.Changed += value;
            remove => _watcher.Changed -= value;
        }

        /// <summary>
        /// Watches the directory samba's configuration lives in, plus any included file outside it.
        ///
        /// <para>The <b>directory</b> rather than smb.conf alone, because an editor that writes by
        /// renaming a temp file over the original leaves the name pointing at a new inode, and
        /// because a new include appearing is a change worth noticing. Called after every load, so
        /// the set follows the includes; asking for the set already in hand is a no-op.</para>
        /// </summary>
        public void StartWatching()
        {
            var paths = new List<string> { "/etc/samba" };

            foreach (var file in Catalog.Files)
                if (System.IO.Path.GetDirectoryName(file.Path) is { Length: > 0 } dir &&
                    !dir.StartsWith("/etc/samba", StringComparison.Ordinal))
                    paths.Add(dir);

            _watcher.Watch(paths);
        }

        public void StopWatching() => _watcher.Stop();

        // ---- Reading -------------------------------------------------------

        // One round trip for the lot, in the shape CronService.LoadScript uses: a tag in field 0,
        // real tab characters, every best-effort half fenced with 2>/dev/null so it cannot take the
        // exit status with it, the unbounded field last, and a closing `exit 0`.
        //
        // The include walk is a worklist rather than recursion, because a shell function cannot add
        // to a list its caller reads when the loop that drives it is a pipeline. IFS is set to a
        // newline for the inner loop so a path with a space in it is one path.
        //
        // awk rather than sed wherever a real tab has to be emitted: sed's \t in a replacement is a
        // GNU extension and this script has to survive busybox.
        private const string LoadScript = """
            export LC_ALL=C

            command -v smbd >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(smbd --version 2>/dev/null | head -n 1)"

            if command -v systemctl >/dev/null 2>&1; then
              for u in smbd smb; do
                [ "$(systemctl show -p LoadState --value "$u".service 2>/dev/null)" = loaded ] || continue
                printf 'd\tsmbd\t%s\t%s\t%s\n' "$u" \
                  "$(systemctl show -p ActiveState --value "$u".service 2>/dev/null)" \
                  "$(systemctl show -p UnitFileState --value "$u".service 2>/dev/null)"
                break
              done
              for u in nmbd nmb; do
                [ "$(systemctl show -p LoadState --value "$u".service 2>/dev/null)" = loaded ] || continue
                printf 'd\tnmbd\t%s\t%s\t%s\n' "$u" \
                  "$(systemctl show -p ActiveState --value "$u".service 2>/dev/null)" \
                  "$(systemctl show -p UnitFileState --value "$u".service 2>/dev/null)"
                break
              done
            fi

            for t in setfacl getfacl smbpasswd pdbedit testparm smbcontrol useradd userdel usermod semanage restorecon findmnt; do
              command -v "$t" >/dev/null 2>&1 && printf 't\t%s\n' "$t"
            done

            # The shell a share account is created with. Asked rather than assumed: Debian keeps
            # nologin in /usr/sbin, RHEL in /sbin, and a host with neither still has /bin/false.
            for h in /usr/sbin/nologin /sbin/nologin /bin/false; do
              [ -x "$h" ] && { printf 'n\t%s\n' "$h"; break; }
            done

            command -v getenforce >/dev/null 2>&1 && printf 's\t%s\n' "$(getenforce 2>/dev/null)"

            # The effective [global] section, and only that section: what this module has to agree
            # with. Every share is read from the files themselves, not from here, because only the
            # files say which file a share is in and testparm's output is normalised beyond editing.
            testparm -s --section-name=global 2>/dev/null | awk '
              /=/ {
                k = $0; sub(/[[:space:]]*=.*/, "", k); sub(/^[[:space:]]+/, "", k)
                v = $0; sub(/^[^=]*=[[:space:]]?/, "", v)
                if (k != "") printf "o\t%s\t%s\n", k, v
              }'

            # smb.conf and everything it includes, one record each, plus the paths they share so the
            # ACL probe below needs no second round trip.
            list=/etc/samba/smb.conf
            paths=
            i=1
            while [ "$i" -le 64 ]; do
              f=$(printf '%s\n' "$list" | sed -n "${i}p")
              [ -n "$f" ] || break
              i=$((i + 1))

              if [ ! -f "$f" ]; then
                printf 'x\t%s\t%s\n' "$f" "is not there"
                continue
              fi

              z=$(stat -Lc %s -- "$f" 2>/dev/null || echo 0)
              if [ "${z:-0}" -gt CAP ]; then
                printf 'x\t%s\t%s\n' "$f" "is $z bytes, too big to edit here"
                continue
              fi

              printf 'f\t%s\t%s\n' "$f" "$(base64 < "$f" 2>/dev/null | tr -d '\n')"

              p=$(sed -n 's/^[[:space:]]*[Pp][Aa][Tt][Hh][[:space:]]*=[[:space:]]*//p' "$f" 2>/dev/null \
                    | sed 's/[[:space:]]*$//')
              [ -n "$p" ] && paths="$paths
            $p"

              incs=$(sed -n 's/^[[:space:]]*[Ii][Nn][Cc][Ll][Uu][Dd][Ee][[:space:]]*=[[:space:]]*//p' "$f" 2>/dev/null \
                       | sed 's/[[:space:]]*$//')
              [ -n "$incs" ] || continue

              oldifs=$IFS
              IFS='
            '
              for inc in $incs; do
                [ -n "$inc" ] || continue
                case "$inc" in
                  *%*) printf 'y\t%s\n' "$inc"; continue ;;
                  /*) ;;
                  *) inc="$(dirname -- "$f")/$inc" ;;
                esac
                case "
            $list
            " in
                  *"
            $inc
            "*) continue ;;
                esac
                list="$list
            $inc"
              done
              IFS=$oldifs
            done

            # `pdbedit -L -v` and never `-Lw`: the smbpasswd format carries the NT hash itself, and
            # nothing in this app has any business putting one on the wire.
            pdbedit -L -v 2>/dev/null | awk -F': *' '
              /^Unix username:/ { if (n != "") printf "u\t%s\t%s\n", n, fl; n = $2; fl = "" }
              /^Account Flags:/ { fl = $2 }
              END { if (n != "") printf "u\t%s\t%s\n", n, fl }'

            getent passwd 2>/dev/null | while IFS= read -r l; do
              [ -n "$l" ] && printf 'p\t%s\n' "$l"
            done
            getent group 2>/dev/null | while IFS= read -r l; do
              [ -n "$l" ] && printf 'r\t%s\n' "$l"
            done

            if [ -r /etc/login.defs ]; then
              while read -r k v _; do
                case "$k" in UID_MIN|UID_MAX|GID_MIN|GID_MAX) printf 'l\t%s\t%s\n' "$k" "$v" ;; esac
              done < /etc/login.defs
            fi

            # Can each share's path actually carry an ACL. The paths came out of the files above, so
            # this is still one round trip even on the very first load.
            oldifs=$IFS
            IFS='
            '
            for q in $paths; do
              [ -n "$q" ] || continue
              case "
            $done
            " in *"
            $q
            "*) continue ;; esac
              done="$done
            $q"

              for sk in "${skip[@]}"; do [ "$sk" = "$q" ] && continue 2; done

              if [ ! -d "$q" ]; then printf 'a\tmissing\t\t\t%s\n' "$q"; continue; fi

              fs=$(findmnt -n -o FSTYPE --target "$q" 2>/dev/null | head -n 1)
              [ -n "$fs" ] || fs=$(stat -f -c %T -- "$q" 2>/dev/null)
              cx=$(stat -c %C -- "$q" 2>/dev/null)

              if command -v setfacl >/dev/null 2>&1; then
                tf=$(mktemp -- "$q/.virtdeck-acl.XXXXXX" 2>/dev/null) || {
                  printf 'a\tunwritable\t%s\t%s\t%s\n' "$fs" "$cx" "$q"; continue; }
                if setfacl -m u:0:rwx -- "$tf" 2>/dev/null; then w=yes; else w=no; fi
                rm -f -- "$tf"
              else
                w=no
              fi
              printf 'a\t%s\t%s\t%s\t%s\n' "$w" "$fs" "$cx" "$q"
            done
            IFS=$oldifs

            exit 0
            """;

        /// <summary>Reads the whole picture in one round trip and caches it as <see cref="Catalog"/>.</summary>
        public async Task<SambaCatalog> LoadAsync(CancellationToken ct = default)
        {
            var catalog = await Task.Run(Fetch, ct);
            Catalog = catalog;
            return catalog;
        }

        /// <summary>
        /// Forgets what the ACL probe answered, so the next load asks the host again. Called after a
        /// save or a reapply, which are the two things that can have changed the answer.
        /// </summary>
        public void ForgetAclProbe(string? path = null)
        {
            if (path is null) { _aclCache.Clear(); _fsCache.Clear(); return; }
            _aclCache.Remove(path);
            _fsCache.Remove(path);
        }

        private SambaCatalog Fetch()
        {
            // The cap is substituted before the array blob is spliced in front, never after: base64
            // is [A-Za-z0-9+/=] and can perfectly well contain the three letters CAP.
            var script = LoadScript.Replace("CAP", ListingCap.ToString(CultureInfo.InvariantCulture));
            var full = ShellScript.ArrayFrom("skip", _aclCache.Keys) + script;

            var raw = _ssh.RunSudoCommand(ShellScript.Wrap(full));
            var catalog = Parse(raw);

            // What the probe skipped this time is what it answered last time.
            foreach (var (path, verdict) in _aclCache) catalog.AclByPath.TryAdd(path, verdict);
            foreach (var (path, fs) in _fsCache) catalog.FsByPath.TryAdd(path, fs);
            foreach (var (path, verdict) in catalog.AclByPath) _aclCache[path] = verdict;
            foreach (var (path, fs) in catalog.FsByPath) _fsCache[path] = fs;

            Diagnostics.SpiceLog.Log(
                $"[samba] {catalog.Shares.Count} shares in {catalog.Files.Count} files, " +
                $"{catalog.Users.Count} users, version='{catalog.ToolVersion}', " +
                $"unit='{catalog.SmbdUnit}' {catalog.SmbdState}, selinux='{catalog.SeLinux}'");

            return catalog;
        }

        internal static SambaCatalog Parse(string raw)
        {
            var catalog = new SambaCatalog();
            var bodies = new List<(string Path, string Text, string? Problem)>();
            var flags = new List<(string Name, string Flags)>();

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "v":
                        catalog.ToolVersion = Version(rest.Trim());
                        break;

                    case "d":
                    {
                        var f = rest.Split('\t', 4);
                        if (f.Length < 4) break;
                        if (f[0] == "smbd")
                        {
                            catalog.SmbdUnit = f[1];
                            catalog.SmbdState = f[2].Trim();
                            catalog.SmbdEnabled = f[3].Trim();
                        }
                        else
                        {
                            catalog.NmbdUnit = f[1];
                            catalog.NmbdState = f[2].Trim();
                        }
                        break;
                    }

                    case "t":
                        if (rest.Trim() is { Length: > 0 } tool) catalog.Tools.Add(tool);
                        break;

                    case "n":
                        catalog.NologinShell = rest.Trim();
                        break;

                    case "s":
                        catalog.SeLinux = rest.Trim();
                        break;

                    case "o":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2) break;
                        catalog.Globals[f[0].Trim()] = f[1].Trim();
                        break;
                    }

                    case "f":
                    {
                        var f = rest.Split('\t', 2);
                        bodies.Add((f[0], f.Length > 1 ? ShellScript.Decode(f[1]) : string.Empty, null));
                        break;
                    }

                    case "x":
                    {
                        // Queued with the readable files rather than added here, so the list stays
                        // in the order the host walked the includes: smb.conf, then what it pulls
                        // in, which is the order samba itself resolves a duplicate name in.
                        var f = rest.Split('\t', 2);
                        bodies.Add((f[0], string.Empty,
                                    f.Length > 1 ? $"{f[0]} {f[1]}" : $"{f[0]} could not be read"));
                        break;
                    }

                    case "y":
                        if (rest.Trim() is { Length: > 0 } macro) catalog.SkippedIncludes.Add(macro);
                        break;

                    case "u":
                    {
                        var f = rest.Split('\t', 2);
                        if (f[0].Trim() is { Length: > 0 } name)
                            flags.Add((name, f.Length > 1 ? f[1].Trim() : string.Empty));
                        break;
                    }

                    case "p":
                    {
                        // name:passwd:uid:gid:gecos:home:shell, exactly as UserAccountService reads
                        // it. No field of that file may contain a colon, so the split is exact.
                        var f = rest.Split(':', 7);
                        if (f.Length < 7 || !int.TryParse(f[2], out var uid)) break;
                        int.TryParse(f[3], out var gid);
                        catalog.Accounts.Add(new UserAccount
                        {
                            Name = f[0],
                            Uid = uid,
                            Gid = gid,
                            Gecos = f[4],
                            FullName = UserAccountService.GecosHead(f[4]),
                            Home = f[5],
                            Shell = f[6],
                        });
                        break;
                    }

                    case "r":
                    {
                        var f = rest.Split(':', 4);
                        if (f.Length < 4 || !int.TryParse(f[2], out var gid)) break;
                        catalog.Groups.Add(new UserGroup
                        {
                            Name = f[0],
                            Gid = gid,
                            Members = f[3].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(m => m.Trim()).Where(m => m.Length > 0).ToList(),
                        });
                        break;
                    }

                    case "l":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2 || !int.TryParse(f[1].Trim(), out var value)) break;
                        switch (f[0])
                        {
                            case "UID_MIN": catalog.UidMin = value; break;
                            case "UID_MAX": catalog.UidMax = value; break;
                            case "GID_MIN": catalog.GidMin = value; break;
                            case "GID_MAX": catalog.GidMax = value; break;
                        }
                        break;
                    }

                    case "a":
                    {
                        var f = rest.Split('\t', 4);
                        if (f.Length < 4) break;
                        var path = f[3];
                        catalog.AclByPath[path] = f[0] switch
                        {
                            "yes" => AclVerdict.Yes,
                            "missing" => AclVerdict.Missing,
                            "unwritable" => AclVerdict.Unwritable,
                            _ => AclVerdict.No,
                        };
                        if (f[1].Length > 0) catalog.FsByPath[path] = f[1];

                        // A context of '?' means the host has no SELinux at all, which is not the
                        // same as a label smbd may not serve.
                        var context = f[2];
                        if (context.Length > 0 && context != "?" &&
                            !context.Contains("samba_share_t", StringComparison.Ordinal) &&
                            !context.Contains("public_content", StringComparison.Ordinal))
                            catalog.UnlabelledPaths.Add(path);
                        break;
                    }

                    case "e":
                    {
                        var f = rest.Split('\t', 2);
                        var why = f.Length > 1 ? f[1] : "could not be read";
                        catalog.ListFailure = catalog.ListFailure.Length > 0
                            ? catalog.ListFailure + "; " + $"{f[0]} {why}"
                            : $"{f[0]} {why}";
                        break;
                    }
                }
            }

            // Second pass, once the globals are in: the guest account decides which name in
            // `valid users` is a permission row and which is only there to let guests past the list.
            // In the order the host walked them: smb.conf, then what it includes, which is the
            // order samba reads them and so the order a duplicate section name resolves in.
            foreach (var (path, text, problem) in bodies)
            {
                var isMain = path == SambaConfig.SmbConfPath;

                if (problem is not null)
                {
                    catalog.Files.Add(new SambaFile { Path = path, IsMain = isMain, Problem = problem });
                    continue;
                }

                var file = SambaConfig.Read(path, text, isMain);
                catalog.Files.Add(file);
                catalog.Shares.AddRange(SambaConfig.SharesOf(file, catalog.GuestAccount));
            }

            var accounts = catalog.Accounts.ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var (name, flag) in flags)
            {
                accounts.TryGetValue(name, out var account);
                catalog.Users.Add(new SambaUser
                {
                    Name = name,
                    Flags = flag,
                    Account = account,
                    CreatedHere = account is not null && IsOurs(account),
                });
            }

            catalog.Users.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            catalog.Accounts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            catalog.Groups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            return catalog;
        }

        /// <summary>
        /// The marker VirtDeck stamps on an account it created, in GECOS element 0. A marker is
        /// needed at all because deletion has to be asymmetric: an account this app made is its to
        /// remove, and one that was already on the host is not, whatever samba thinks of it. Nothing
        /// like it exists for a <i>share</i>, which is the whole point of this module.
        /// </summary>
        public const string AccountMarker = "VirtDeck share user";

        public static bool IsOurs(UserAccount account) =>
            string.Equals(UserAccountService.GecosHead(account.Gecos), AccountMarker, StringComparison.Ordinal);

        /// <summary>
        /// The version out of <c>smbd --version</c>, which answers "Version 4.19.5-Debian". The word
        /// "Version" is noise in a status slot two words wide.
        /// </summary>
        private static string Version(string line)
        {
            const string prefix = "Version ";
            var text = line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? line[prefix.Length..].Trim()
                : line.Trim();
            return text.Length > 0 ? text : "samba";
        }

        // ---- Writing ---------------------------------------------------------

        // The conflict check rides in the same round trip as the write, rather than being a read
        // followed by a write: the body goes to a temp file first, the target's digest is compared
        // against the one the client holds, and only then is anything installed. There is no window
        // of VirtDeck's own for somebody's edit at a terminal to be lost in.
        //
        // Validation is install-then-check-then-roll-back rather than check-then-install, and that
        // is forced by includes. A file pulled in by smb.conf is a fragment: on its own it has no
        // [global] and testparm's verdict on it would be about a config samba never sees. So the
        // candidate is put in place, the *whole* effective configuration is asked about, and the
        // original is put straight back if the answer is no. That validates strictly more than
        // checking a fragment could, and the window in which a rejected config is on disk is the few
        // milliseconds testparm takes, against smbd's own re-read interval of about a minute.
        //
        // Exit 9 is the staleness refusal and exit 8 is testparm's, both distinct from the exit 1
        // everything else here uses.
        //
        // The gate is testparm's literal "Loaded services file OK." line and *not* its exit code.
        // testparm also exits non-zero for do_global_checks complaints about things that have
        // nothing to do with us, an unwritable lock directory or a bad netbios name among them, and
        // keying off the code would leave the module unable to save on a host that was already
        // misconfigured. That line is printed the moment the parser accepts the file.
        private const string WriteScript = """
            f=${a[0]}
            want=${a[1]}

            [ -f "$f" ] || { cat > /dev/null; echo "$f is not there" >&2; exit 1; }
            d=$(dirname -- "$f")

            t=$(mktemp -- "$d/.virtdeck-smb.XXXXXX") || { cat > /dev/null; exit 1; }
            b=$(mktemp -- "$d/.virtdeck-bak.XXXXXX") || { cat > /dev/null; rm -f -- "$t"; exit 1; }
            trap 'rm -f -- "$t" "$b"' EXIT
            cat > "$t"

            if command -v sha256sum >/dev/null 2>&1; then
              [ "$(sha256sum -- "$f" | cut -d' ' -f1)" = "$want" ] || { echo stale >&2; exit 9; }
            fi

            cp -p -- "$f" "$b" || exit 1
            chmod --reference="$f" -- "$t" 2>/dev/null || chmod 0644 -- "$t"
            chown --reference="$f" -- "$t" 2>/dev/null || true
            mv -f -- "$t" "$f"

            if command -v testparm >/dev/null 2>&1; then
              out=$(testparm -s /etc/samba/smb.conf 2>&1)
              case "$out" in
                *"Loaded services file OK."*) ;;
                *)
                  cp -p -- "$b" "$f"
                  printf '%s\n' "$out" >&2
                  exit 8
                  ;;
              esac
            fi

            rm -f -- "$b"
            trap - EXIT
            """;

        /// <summary>
        /// Writes one config file back whole. The digest the file was read with is the claim about
        /// what is on the host, and a mismatch refuses the write rather than losing somebody's edit.
        /// </summary>
        public async Task WriteFileAsync(SambaFile file, CancellationToken ct = default)
        {
            if (!file.Writable) throw new InvalidOperationException(file.Problem);

            var text = SambaConfig.Render(file);
            var script = ShellScript.ArrayFrom("a", [file.Path, file.Digest]) + WriteScript;

            try
            {
                await _ssh.RunPipeInAsync(script, elevated: true, async (stdin, token) =>
                {
                    var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
                    await stdin.WriteAsync(bytes, token);
                    await stdin.FlushAsync(token);
                }, ct);
            }
            catch (Exception ex) when (ex.Message.Contains("exit 9", StringComparison.Ordinal))
            {
                var current = await ReadFileAsync(file.Path, ct);
                throw new SambaConflictException(file.Path, current);
            }
            catch (Exception ex) when (ex.Message.Contains("exit 8", StringComparison.Ordinal))
            {
                throw new SambaConfigException(Reason(ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new Exception(Reason(ex.Message), ex);
            }

            Diagnostics.SpiceLog.Log($"[samba] wrote {file.Path} ({text.Length} bytes)");
        }

        /// <summary>One file as it is on the host now, for showing what a rejected write collided with.</summary>
        public Task<string> ReadFileAsync(string path, CancellationToken ct = default)
        {
            var script = ShellScript.ArrayFrom("a", [path]) + """
                f=${a[0]}
                [ -f "$f" ] || exit 0
                base64 < "$f" 2>/dev/null | tr -d '\n'
                """;

            return Task.Run(() => ShellScript.Decode(_ssh.RunSudoCommand(ShellScript.Wrap(script)).Trim()), ct);
        }

        // ---- Telling smbd ----------------------------------------------------

        // smbcontrol first, because it reaches every smbd, nmbd and winbindd without systemd having
        // an opinion and without restarting anything. It is never treated as proof of anything: it
        // answers 0 even when no smbd is listening on the messaging socket, which is why the module
        // keys its "not running" notice off the unit's ActiveState instead.
        //
        // Always exits 0. A reload that did not happen is not a save that failed: smbd re-reads
        // smb.conf by itself about once a minute.
        private const string ReloadScript = """
            if command -v smbcontrol >/dev/null 2>&1 && smbcontrol all reload-config >/dev/null 2>&1; then
              exit 0
            fi
            if command -v systemctl >/dev/null 2>&1; then
              for u in smbd smb; do
                systemctl reload "$u".service >/dev/null 2>&1 && exit 0
              done
            fi
            exit 0
            """;

        public Task ReloadAsync(CancellationToken ct = default) =>
            Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(ReloadScript)), ct);

        /// <summary>
        /// Enables and starts the units this host actually has. The unmask is not paranoia: a Debian
        /// host that had samba removed and reinstalled, and every host where somebody masked it to
        /// stop it listening, needs it before enable will do anything.
        /// </summary>
        public Task StartServiceAsync(CancellationToken ct = default)
        {
            var units = new List<string>();
            if (Catalog.SmbdUnit.Length > 0) units.Add(Catalog.SmbdUnit);
            if (Catalog.NmbdUnit.Length > 0) units.Add(Catalog.NmbdUnit);
            if (units.Count == 0) throw new InvalidOperationException("This host has no smbd service unit.");

            var script = ShellScript.ArrayFrom("u", units) + """
                for n in "${u[@]}"; do
                  systemctl unmask "$n".service >/dev/null 2>&1
                done
                systemctl enable --now "${u[0]}".service || exit $?
                for n in "${u[@]:1}"; do
                  systemctl enable --now "$n".service >/dev/null 2>&1 || true
                done
                """;

            Diagnostics.SpiceLog.Log($"[samba] starting {string.Join(", ", units)}");
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
        }

        // ---- Permissions on the filesystem -----------------------------------

        // Two find passes and not one, because `setfacl -d` on a regular file is an error: the
        // default ACL only means anything on a directory.
        //
        // -xdev, so a share root with a backup target or an NFS mount under it is not walked into.
        // -type f -o -type d, so symlinks are skipped entirely: the -type test looks at the link and
        // not its target, and setfacl follows a link by default, which would otherwise reach out of
        // the share and change something else.
        //
        // chmod g+s runs after setfacl and is safe either way: it reads the current mode, whose
        // group bits are the ACL mask, adds S_ISGID and writes it back, so the mask is unchanged.
        // It is the only place this module touches mode bits directly, and there is no plain chmod
        // over a tree anywhere: chmod rewrites the mask from its group bits and would collapse every
        // named entry at once.
        private const string AclScript = """
            p=${a[0]}
            acc=${a[1]}
            def=${a[2]}
            deep=${a[3]}

            [ -d "$p" ] || { echo "there is no folder at $p" >&2; exit 1; }

            setfacl    --set="$acc" -- "$p" || exit 1
            setfacl -d --set="$def" -- "$p" || exit 1
            chmod g+s -- "$p" 2>/dev/null || true
            printf 'root\n'

            [ "$deep" = 1 ] || { printf 'done\n'; exit 0; }

            n=$(find "$p" -mindepth 1 -xdev \( -type f -o -type d \) -print0 2>/dev/null | tr -dc '\0' | wc -c)
            printf 'total %s\n' "$n"

            find "$p" -mindepth 1 -xdev \( -type f -o -type d \) -print0 2>/dev/null \
              | xargs -0 -r -n BATCH sh -c 'if setfacl --set="$0" -- "$@" 2>/dev/null; then printf "did %s\n" "$#"; else printf "bad %s\n" "$#"; fi' "$acc"

            find "$p" -mindepth 1 -xdev -type d -print0 2>/dev/null \
              | xargs -0 -r -n BATCH sh -c 'setfacl -d --set="$0" -- "$@" 2>/dev/null && chmod g+s -- "$@" 2>/dev/null' "$def"

            printf 'done\n'
            """;

        /// <summary>
        /// Writes a share's permissions onto the folder itself.
        ///
        /// <para>The share root is always done and costs three syscalls; without it a new share has
        /// no default ACL at all. <paramref name="deep"/> is what walks the tree, and it is what
        /// fixes a file that was created by something other than samba: see <see cref="SambaAcl"/>
        /// for why a default ACL alone does not.</para>
        ///
        /// <para>Streams, on a connection of its own, because a tree can take minutes and
        /// <c>RunSudoCommand</c> holds the shared connection's lock for the whole call.</para>
        /// </summary>
        public Task ApplyAclAsync(SambaShare share, bool deep, IProgress<AclProgress>? progress,
                                  CancellationToken ct = default)
        {
            if (SambaAcl.PathProblem(share.Path) is { } problem) throw new ArgumentException(problem);

            var specs = SambaAcl.Build(share, Catalog.GuestAccount);
            var script = ShellScript.ArrayFrom("a", [share.Path, specs.Access, specs.Default, deep ? "1" : "0"])
                       + AclScript.Replace("BATCH", BatchSize.ToString(CultureInfo.InvariantCulture));

            Diagnostics.SpiceLog.Log($"[samba] acl {share.Path} deep={deep} access='{specs.Access}'");

            return Task.Run(() =>
            {
                long done = 0, total = 0, failed = 0;
                var counting = deep;

                _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(script), line =>
                {
                    var space = line.IndexOf(' ');
                    var verb = space < 0 ? line : line[..space];
                    var count = space < 0 || !long.TryParse(line[(space + 1)..].Trim(), out var n) ? 0 : n;

                    switch (verb)
                    {
                        case "total": total = count; counting = false; break;
                        case "did": done += count; break;
                        case "bad": done += count; failed += count; break;
                        default: return;
                    }

                    progress?.Report(new AclProgress(done, total, failed, counting));
                }, ct);

                if (failed > 0)
                    throw new Exception(
                        $"{failed:N0} of {total:N0} items in {share.Path} would not take the new permissions.");
            }, ct);
        }

        /// <summary>
        /// Takes VirtDeck's ACLs back off a folder, for the tickbox on the delete confirmation.
        /// <c>-b</c> removes the named entries and <c>-k</c> the default ACL, leaving the ordinary
        /// owner, group and other bits, which is the state a folder nobody has shared is in.
        /// </summary>
        public Task StripAclAsync(string path, CancellationToken ct = default)
        {
            if (SambaAcl.PathProblem(path) is { } problem) throw new ArgumentException(problem);

            var script = ShellScript.ArrayFrom("a", [path]) + """
                p=${a[0]}
                [ -d "$p" ] || exit 0
                setfacl -R -b -k -- "$p" 2>/dev/null || true
                exit 0
                """;

            Diagnostics.SpiceLog.Log($"[samba] stripping acls from {path}");
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
        }

        /// <summary>Creates a share's folder. Run before the config write, so testparm never sees a
        /// share whose path is absent.</summary>
        public Task CreateFolderAsync(string path, CancellationToken ct = default)
        {
            if (SambaAcl.PathProblem(path) is { } problem) throw new ArgumentException(problem);
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Argv(["mkdir", "-p", "--", path])), ct);
        }

        /// <summary>
        /// Labels a folder so SELinux lets smbd serve it. Without this, a share outside the handful
        /// of paths the stock policy covers is denied whatever its ACLs say, and what the user sees
        /// is an unexplained "permission denied" from the client.
        ///
        /// <para>It sets a context on one path. The <c>samba_export_all_rw</c> boolean would be the
        /// blunt alternative and this app must not touch it: it is host-wide and would open every
        /// directory on the machine to smbd, which is a far larger thing than the user asked for.</para>
        /// </summary>
        public Task LabelForSambaAsync(string path, CancellationToken ct = default)
        {
            if (SambaAcl.PathProblem(path) is { } problem) throw new ArgumentException(problem);

            var script = ShellScript.ArrayFrom("a", [path]) + """
                p=${a[0]}
                p=${p%/}
                semanage fcontext -a -t samba_share_t "$p(/.*)?" 2>/dev/null \
                  || semanage fcontext -m -t samba_share_t "$p(/.*)?" || exit $?
                restorecon -R -- "$p"
                """;

            Diagnostics.SpiceLog.Log($"[samba] labelling {path} as samba_share_t");
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
        }

        // ---- Capability ------------------------------------------------------

        /// <summary>
        /// What the right-hand status slot says. An installed samba whose daemon is <b>not running</b>
        /// is the one thing here worth saying out loud, because every folder on the page is then a
        /// folder nothing is serving.
        /// </summary>
        public string CapabilityText
        {
            get
            {
                if (!Catalog.Installed) return "smbd not found";

                var name = "samba " + Catalog.ToolVersion;
                return Catalog.SmbdState is { Length: > 0 } state && state != "active"
                    ? $"{name} ({Catalog.SmbdUnit} {state})"
                    : name;
            }
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
