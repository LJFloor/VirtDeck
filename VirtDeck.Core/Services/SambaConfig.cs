using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The grammar of the host's samba config files, both directions, with no SSH anywhere in it.
    ///
    /// <para>Separate from <see cref="SambaService"/> for the reason <c>CronSchedule</c> is separate
    /// from <c>CronService</c>: this is the part that can be read and argued about without a host on
    /// the other end, and it is the part a bug would be expensive in, because it rewrites a file the
    /// host then obeys.</para>
    ///
    /// <para><b>This module edits the host's own configuration, not a VirtDeck-owned subset.</b>
    /// Nothing in this app puts a marker in smb.conf, so there is no "ours versus theirs" to draw,
    /// and every share on the page is a foreign share. That is what makes the round trip below
    /// load-bearing rather than a nicety, and it is the same stance the cron module takes towards
    /// every crontab on the machine.</para>
    ///
    /// <para><b>A line nothing touched is written back byte for byte</b>, indentation, comment
    /// markers, blank lines and parameter spelling included. Saving a share rewrites the parameters
    /// of that share's section and nothing else: a value already there is edited in place, keeping
    /// its indent and the way its key was spelled; one that is needed and absent is inserted after
    /// the section's last parameter; one that is no longer needed is removed. Everything between is
    /// left exactly as it was found, which is what lets somebody keep their comments and their
    /// <c>hosts allow</c> and still use this window.</para>
    /// </summary>
    public static class SambaConfig
    {
        /// <summary>The file samba reads first, and where a new share goes unless somebody picks another.</summary>
        public const string SmbConfPath = "/etc/samba/smb.conf";

        // The parameters this app writes and therefore rewrites inside a section it is saving.
        // Anything else found there belongs to whoever put it there and is never touched. Compared
        // normalised, because samba ignores case and inner whitespace in a parameter name.
        private static readonly HashSet<string> OwnedShareKeys = new(StringComparer.Ordinal)
        {
            "comment", "path", "available", "browseable", "read only", "writeable",
            "write list", "read list", "valid users", "guest ok",
            "follow symlinks", "veto files", "delete veto files",
            "vfs objects", "recycle:repository", "recycle:keeptree", "recycle:versions",
        };

        // Written only while the share manages its own folder permissions, because these are the
        // half of that promise that lives in samba rather than in the filesystem. Cleared by the
        // Advanced page's escape hatch, which then leaves a foreign share's own mode settings alone.
        private static readonly HashSet<string> OwnedMaskKeys = new(StringComparer.Ordinal)
        {
            "create mask", "force create mode", "directory mask", "force directory mode",
            "inherit acls",
        };

        /// <summary>
        /// Sections that are samba's own machinery rather than somebody's shared folder.
        /// <c>[homes]</c> maps every account to its home directory, and the two printer sections are
        /// the print system's. None of them has a <c>path</c> anybody chose, and the whole
        /// permissions model here is about a directory somebody picked, so they are left out of the
        /// table entirely rather than shown as rows that half work.
        /// </summary>
        public static readonly HashSet<string> SpecialSections = new(StringComparer.OrdinalIgnoreCase)
        {
            "global", "homes", "printers", "print$", "IPC$", "ADMIN$",
        };

        private static readonly Regex ValidName = new(@"^[A-Za-z0-9_][A-Za-z0-9 _.\-]{0,79}$");

        // ---- Names ---------------------------------------------------------

        /// <summary>
        /// Why this name will not do, or null. One reason at a time, because a form that reports
        /// three problems at once is read as three problems and fixed as one.
        /// </summary>
        public static string? NameProblem(string name)
        {
            if (name.Length == 0) return "Give the shared folder a name.";
            if (SpecialSections.Contains(name)) return $"“{name}” is a name Samba keeps for itself.";
            if (name.EndsWith(' ')) return "A name cannot end with a space.";
            if (!ValidName.IsMatch(name))
                return "A name may hold letters, digits, spaces, dots, hyphens and underscores.";
            return null;
        }

        /// <summary>
        /// The caret-preserving fold the name box runs on every keystroke: one illegal character
        /// becomes one legal one, so the caret does not move. Idempotent rather than flagged, for the
        /// reason <c>CreateVmWizard.SanitizeName</c> is.
        /// </summary>
        public static string SanitizeName(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                              or ' ' or '_' or '.' or '-'
                    ? c
                    : '_');
            return sb.ToString();
        }

        // ---- Reading -------------------------------------------------------

        /// <summary>
        /// Reads one file into lines. Tolerant by design: anything this grammar does not recognise
        /// becomes <see cref="SambaLineKind.Unknown"/> and is carried through untouched, because a
        /// file this app cannot fully read is still a file it must not corrupt.
        /// </summary>
        public static SambaFile Read(string path, string text, bool isMain)
        {
            var trailing = text.EndsWith('\n');
            var body = trailing ? text[..^1] : text;

            var raw = body.Length == 0 && trailing
                ? new List<string>()
                : [.. body.Split('\n').Select(l => l.TrimEnd('\r'))];

            return new SambaFile
            {
                Path = path,
                Text = text,
                Digest = Sha256(text),
                TrailingNewline = trailing || text.Length == 0,
                IsMain = isMain,
                Lines = [.. raw.Select(ReadLine)],
            };
        }

        private static SambaLine ReadLine(string raw)
        {
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
                return new SambaLine { Kind = SambaLineKind.Blank, Raw = raw };

            if (trimmed[0] is '#' or ';')
                return new SambaLine { Kind = SambaLineKind.Comment, Raw = raw };

            if (trimmed[0] == '[' && trimmed[^1] == ']')
                return new SambaLine
                {
                    Kind = SambaLineKind.Section,
                    Raw = raw,
                    Section = trimmed[1..^1].Trim(),
                };

            // A line continued onto the next with a trailing backslash is not a parameter this app
            // can safely rewrite, so it is not read as one.
            var eq = trimmed.IndexOf('=');
            if (eq <= 0 || raw.EndsWith('\\'))
                return new SambaLine { Kind = SambaLineKind.Unknown, Raw = raw };

            var rawKey = trimmed[..eq].TrimEnd();
            return new SambaLine
            {
                Kind = SambaLineKind.Parameter,
                Raw = raw,
                RawKey = rawKey,
                Key = Normalise(rawKey),
                Value = trimmed[(eq + 1)..].Trim(),
                Indent = raw[..(raw.Length - raw.TrimStart().Length)],
            };
        }

        /// <summary>The whole file back, byte for byte where nothing changed.</summary>
        public static string Render(SambaFile file)
        {
            var body = string.Join('\n', file.Lines.Select(l => l.Raw));
            return file.TrailingNewline && (body.Length > 0 || file.Lines.Count > 0)
                ? body + "\n"
                : body;
        }

        /// <summary>
        /// The <c>include</c> targets in a file, and the ones that could not be followed.
        ///
        /// <para>A path holding a <c>%</c> macro resolves per connection, per client or per user, so
        /// there is no one file behind it to read or write; it is reported rather than guessed at.
        /// A relative path is resolved against the including file's own directory, which is what
        /// samba does.</para>
        /// </summary>
        public static (List<string> Paths, List<string> Skipped) Includes(SambaFile file)
        {
            var paths = new List<string>();
            var skipped = new List<string>();

            foreach (var line in file.Lines)
            {
                if (line.Kind != SambaLineKind.Parameter) continue;
                if (!string.Equals(line.Key, "include", StringComparison.Ordinal)) continue;

                var target = line.Value.Trim();
                if (target.Length == 0) continue;

                if (target.Contains('%'))
                {
                    skipped.Add(target);
                    continue;
                }

                paths.Add(target.StartsWith('/')
                    ? target
                    : System.IO.Path.GetDirectoryName(file.Path) is { Length: > 0 } dir
                        ? dir + "/" + target
                        : target);
            }

            return (paths, skipped);
        }

        /// <summary>
        /// The shares a file defines. <see cref="SpecialSections"/> are left out, and so is a
        /// section with no <c>path</c>, which is not something the rest of this module can act on.
        /// </summary>
        public static List<SambaShare> SharesOf(SambaFile file, string guestAccount)
        {
            var shares = new List<SambaShare>();

            foreach (var (name, start, end) in Sections(file))
            {
                if (SpecialSections.Contains(name)) continue;

                var raw = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = start + 1; i < end; i++)
                    if (file.Lines[i] is { Kind: SambaLineKind.Parameter } p)
                        raw[p.Key] = p.Value;

                if (!raw.TryGetValue("path", out var path) || path.Trim().Length == 0) continue;

                var share = new SambaShare { Name = name, FilePath = file.Path };
                Fill(share, raw, guestAccount);
                shares.Add(share);
            }

            return shares;
        }

        /// <summary>Every section in a file as (name, headerIndex, endExclusive).</summary>
        private static List<(string Name, int Start, int End)> Sections(SambaFile file)
        {
            var found = new List<(string, int, int)>();
            var starts = new List<(string Name, int Index)>();

            for (var i = 0; i < file.Lines.Count; i++)
                if (file.Lines[i] is { Kind: SambaLineKind.Section } s)
                    starts.Add((s.Section, i));

            for (var i = 0; i < starts.Count; i++)
                found.Add((starts[i].Name, starts[i].Index,
                           i + 1 < starts.Count ? starts[i + 1].Index : file.Lines.Count));

            return found;
        }

        private static void Fill(SambaShare share, Dictionary<string, string> raw, string guestAccount)
        {
            string Get(string key) => raw.TryGetValue(key, out var v) ? v : string.Empty;

            share.Path = Get("path");
            share.Comment = Get("comment");
            share.Available = Truth(Get("available"), true);
            share.Browseable = Truth(Get("browseable"), true);
            share.FollowSymlinks = Truth(Get("follow symlinks"), true);

            // `writeable` is samba's inverse spelling of `read only` and either may be what the file
            // uses. The positive form wins where both are present, which is samba's own rule.
            var readOnly = raw.ContainsKey("writeable") ? !Truth(Get("writeable"), false)
                         : Truth(Get("read only"), true);

            var guestOk = Truth(Get("guest ok"), false);
            share.Guest = !guestOk ? GuestAccess.None
                        : readOnly ? GuestAccess.ReadOnly
                        : GuestAccess.ReadWrite;

            var writeList = Names(Get("write list"));
            var readList = Names(Get("read list"));

            foreach (var entry in Names(Get("valid users")))
            {
                var isGroup = entry.StartsWith('@') || entry.StartsWith('+') || entry.StartsWith('&');
                var name = isGroup ? entry[1..] : entry;
                if (name.Length == 0) continue;

                // The guest account is in `valid users` only because a non-empty list would otherwise
                // shut guests out of the very share that allows them. It is not a permission row.
                if (guestOk && !isGroup && string.Equals(name, guestAccount, StringComparison.Ordinal))
                    continue;

                var access = readOnly
                    ? (Contains(writeList, entry) ? ShareAccess.ReadWrite : ShareAccess.ReadOnly)
                    : (Contains(readList, entry) ? ShareAccess.ReadOnly : ShareAccess.ReadWrite);

                share.Permissions.Add(new SharePermission { IsGroup = isGroup, Name = name, Access = access });
            }

            // A share whose write list names somebody the valid users list does not is still granting
            // them write access, because an empty valid users lets everybody in. Reading them as
            // permission rows is what keeps the editor from silently dropping the grant.
            foreach (var entry in writeList.Concat(readList))
            {
                var isGroup = entry.StartsWith('@') || entry.StartsWith('+') || entry.StartsWith('&');
                var name = isGroup ? entry[1..] : entry;
                if (name.Length == 0) continue;
                if (share.Permissions.Any(p => p.IsGroup == isGroup &&
                                               string.Equals(p.Name, name, StringComparison.Ordinal)))
                    continue;

                share.Permissions.Add(new SharePermission
                {
                    IsGroup = isGroup,
                    Name = name,
                    Access = Contains(writeList, entry) ? ShareAccess.ReadWrite : ShareAccess.ReadOnly,
                });
            }

            if (Get("vfs objects").Contains("recycle", StringComparison.OrdinalIgnoreCase))
            {
                share.Recycle = true;
                share.RecycleRepository = Get("recycle:repository") is { Length: > 0 } r ? r : ".recycle/%U";
                share.RecycleKeepTree = Truth(Get("recycle:keeptree"), true);
                share.RecycleVersions = Truth(Get("recycle:versions"), true);
            }

            share.VetoFiles = Get("veto files")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();

            // A foreign share that sets none of the mask parameters is one this app has not taken
            // over, and saving it should not silently impose them. A share that already carries the
            // full set is one it has.
            share.ManagedHere = OwnedMaskKeys.All(raw.ContainsKey);
            share.ManageFolderPermissions = share.ManagedHere || !raw.Keys.Any(OwnedMaskKeys.Contains);
        }

        // ---- Writing -------------------------------------------------------

        /// <summary>
        /// Writes a share into its file, in place.
        ///
        /// <para>Only the parameters this app owns inside that one section are touched. A value
        /// already there is rewritten keeping its indent and the spelling of its key; one that is
        /// needed and missing is inserted after the section's last parameter; one no longer needed
        /// is removed. A section that is not there yet is appended to the end of the file.</para>
        /// </summary>
        public static void ApplyShare(SambaFile file, SambaShare share, string guestAccount,
                                      string? renamedFrom = null)
        {
            var wanted = Parameters(share, guestAccount);
            var target = renamedFrom ?? share.Name;
            var section = Sections(file).FirstOrDefault(s =>
                string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));

            if (section.Name is null)
            {
                Append(file, share, wanted);
                return;
            }

            var (_, start, end) = section;

            // The indent this file uses inside a section, so an inserted line matches its
            // neighbours rather than the habits of whoever wrote this method.
            var indent = Enumerable.Range(start + 1, end - start - 1)
                .Select(i => file.Lines[i])
                .FirstOrDefault(l => l.Kind == SambaLineKind.Parameter)?.Indent ?? "\t";

            var owned = OwnedKeys(share);
            var rebuilt = new List<SambaLine>();
            var written = new HashSet<string>(StringComparer.Ordinal);

            // The header, renamed if the share was.
            rebuilt.Add(string.Equals(share.Name, target, StringComparison.Ordinal)
                ? file.Lines[start]
                : new SambaLine { Kind = SambaLineKind.Section, Raw = "[" + share.Name + "]", Section = share.Name });

            for (var i = start + 1; i < end; i++)
            {
                var line = file.Lines[i];

                if (line.Kind != SambaLineKind.Parameter || !owned.Contains(line.Key))
                {
                    rebuilt.Add(line);
                    continue;
                }

                if (wanted.TryGetValue(line.Key, out var value) && written.Add(line.Key))
                {
                    rebuilt.Add(new SambaLine
                    {
                        Kind = SambaLineKind.Parameter,
                        Raw = line.Indent + line.RawKey + " = " + value,
                        RawKey = line.RawKey,
                        Key = line.Key,
                        Value = value,
                        Indent = line.Indent,
                    });
                }

                // Anything else owned goes: a key no longer wanted, and a second copy of one that
                // has already been written. Samba takes the last of a repeated parameter, so leaving
                // a duplicate behind would leave the file saying something the editor does not.
            }

            // Whatever was wanted and was not already in the section, after its last parameter so it
            // lands among the settings rather than under a trailing comment.
            var missing = wanted.Where(kv => !written.Contains(kv.Key)).ToList();
            if (missing.Count > 0)
            {
                var at = rebuilt.FindLastIndex(l => l.Kind == SambaLineKind.Parameter);
                if (at < 0) at = 0;

                rebuilt.InsertRange(at + 1, missing.Select(kv => new SambaLine
                {
                    Kind = SambaLineKind.Parameter,
                    Raw = indent + kv.Key + " = " + kv.Value,
                    RawKey = kv.Key,
                    Key = kv.Key,
                    Value = kv.Value,
                    Indent = indent,
                }));
            }

            file.Lines.RemoveRange(start, end - start);
            file.Lines.InsertRange(start, rebuilt);
        }

        private static void Append(SambaFile file, SambaShare share, Dictionary<string, string> wanted)
        {
            if (file.Lines.Count > 0 && file.Lines[^1].Kind != SambaLineKind.Blank)
                file.Lines.Add(new SambaLine { Kind = SambaLineKind.Blank, Raw = string.Empty });

            file.Lines.Add(new SambaLine
            {
                Kind = SambaLineKind.Section,
                Raw = "[" + share.Name + "]",
                Section = share.Name,
            });

            foreach (var (key, value) in wanted)
                file.Lines.Add(new SambaLine
                {
                    Kind = SambaLineKind.Parameter,
                    Raw = "\t" + key + " = " + value,
                    RawKey = key,
                    Key = key,
                    Value = value,
                    Indent = "\t",
                });
        }

        /// <summary>
        /// Takes a share's whole section out of a file, header and all.
        ///
        /// <para>Comments above the header are left alone: this app did not write them and cannot
        /// tell what they are about. A trailing run of blank lines inside the section goes with it,
        /// so removing a share does not leave a growing gap behind.</para>
        /// </summary>
        public static void RemoveShare(SambaFile file, string name)
        {
            var section = Sections(file).FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (section.Name is null) return;

            var (_, start, end) = section;

            // Keep one blank as the separator the next section already relied on.
            while (end - 1 > start && file.Lines[end - 1].Kind == SambaLineKind.Blank &&
                   end < file.Lines.Count)
                end--;

            file.Lines.RemoveRange(start, end - start);
        }

        /// <summary>
        /// Sets one parameter in a file's <c>[global]</c> section, adding the section if it has
        /// none. Used for exactly one thing, <c>map to guest</c>, which a guest share needs and
        /// which lives nowhere else.
        /// </summary>
        public static bool SetGlobal(SambaFile file, string key, string value) =>
            SetParameter(file, "global", key, value, createSection: true);

        /// <summary>
        /// Sets exactly one parameter in one section and touches nothing else.
        ///
        /// <para>This is what Enable and Disable use, rather than <see cref="ApplyShare"/>. The
        /// difference matters on a share this app did not write: <c>ApplyShare</c> rewrites every
        /// parameter it owns, which is right when somebody has been editing that share in the
        /// dialog, and wrong for a toggle, where it would restyle <c>read only = Yes</c> to
        /// <c>yes</c> and replace a <c>writeable</c> line with a <c>read only</c> one for no reason
        /// anybody asked for. Answers whether anything actually changed.</para>
        /// </summary>
        public static bool SetParameter(SambaFile file, string sectionName, string key, string value,
                                        bool createSection = false)
        {
            var section = Sections(file).FirstOrDefault(s =>
                string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase));

            if (section.Name is null)
            {
                if (!createSection) return false;

                file.Lines.Insert(0, new SambaLine
                {
                    Kind = SambaLineKind.Section, Raw = "[" + sectionName + "]", Section = sectionName,
                });
                file.Lines.Insert(1, new SambaLine
                {
                    Kind = SambaLineKind.Parameter, Raw = "\t" + key + " = " + value,
                    RawKey = key, Key = key, Value = value, Indent = "\t",
                });
                return true;
            }

            var (_, start, end) = section;

            for (var i = start + 1; i < end; i++)
            {
                if (file.Lines[i] is not { Kind: SambaLineKind.Parameter } p) continue;
                if (!string.Equals(p.Key, key, StringComparison.Ordinal)) continue;
                if (string.Equals(p.Value, value, StringComparison.Ordinal)) return false;

                file.Lines[i] = new SambaLine
                {
                    Kind = SambaLineKind.Parameter,
                    Raw = p.Indent + p.RawKey + " = " + value,
                    RawKey = p.RawKey, Key = p.Key, Value = value, Indent = p.Indent,
                };
                return true;
            }

            var indent = Enumerable.Range(start + 1, end - start - 1)
                .Select(i => file.Lines[i])
                .FirstOrDefault(l => l.Kind == SambaLineKind.Parameter)?.Indent ?? "\t";

            var at = start;
            for (var i = start + 1; i < end; i++)
                if (file.Lines[i].Kind == SambaLineKind.Parameter) at = i;

            file.Lines.Insert(at + 1, new SambaLine
            {
                Kind = SambaLineKind.Parameter, Raw = indent + key + " = " + value,
                RawKey = key, Key = key, Value = value, Indent = indent,
            });
            return true;
        }

        /// <summary>The keys this app will rewrite or remove in a share's section.</summary>
        private static HashSet<string> OwnedKeys(SambaShare share)
        {
            var keys = new HashSet<string>(OwnedShareKeys, StringComparer.Ordinal);
            if (share.ManageFolderPermissions) keys.UnionWith(OwnedMaskKeys);
            return keys;
        }

        /// <summary>
        /// What a share's section should say, in the order a new one is written. A key absent from
        /// this is a key removed from the section, which is how "no guests" and "no recycle bin" are
        /// expressed.
        /// </summary>
        private static Dictionary<string, string> Parameters(SambaShare share, string guestAccount)
        {
            var p = new Dictionary<string, string>(StringComparer.Ordinal);

            if (share.Comment.Trim() is { Length: > 0 } comment) p["comment"] = comment;
            p["path"] = share.Path;
            p["available"] = share.Available ? "yes" : "no";
            p["browseable"] = share.Browseable ? "yes" : "no";

            var guest = share.Guest != GuestAccess.None;
            var writers = share.Permissions.Where(x => x.Access == ShareAccess.ReadWrite).ToList();
            var readers = share.Permissions.Where(x => x.Access == ShareAccess.ReadOnly).ToList();

            var valid = share.Permissions
                .Where(x => x.Access != ShareAccess.None)
                .Select(x => x.ConfigName)
                .ToList();
            if (guest) valid.Add(guestAccount);

            // Read-write guests invert the base, and the reason is not symmetry. `write list`
            // matches the authenticated user name, and a guest session's is the mapped guest
            // account, which is a fragile thing to hang write access on. So a guest-writable share
            // is writable by default and names its read-only users instead.
            if (share.Guest == GuestAccess.ReadWrite)
            {
                p["read only"] = "no";
                if (readers.Count > 0) p["read list"] = string.Join(", ", readers.Select(x => x.ConfigName));
            }
            else
            {
                p["read only"] = "yes";
                if (writers.Count > 0) p["write list"] = string.Join(", ", writers.Select(x => x.ConfigName));
            }

            if (valid.Count > 0) p["valid users"] = string.Join(", ", valid);
            p["guest ok"] = guest ? "yes" : "no";

            if (share.ManageFolderPermissions)
            {
                // The masks and the ACLs are one answer and not two. These stop a file created
                // through the share from landing with closed group bits, which is the same trap the
                // default ACL cannot prevent on a file created by anything else. `inherit
                // permissions` is deliberately absent: it overrides every one of these.
                p["create mask"] = "0770";
                p["force create mode"] = "0660";
                p["directory mask"] = "0770";
                p["force directory mode"] = "0770";
                p["inherit acls"] = "yes";
            }

            p["follow symlinks"] = share.FollowSymlinks ? "yes" : "no";

            var veto = share.VetoFiles.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (veto.Count > 0)
            {
                // samba's own separator is '/', and it wants one at each end as well.
                p["veto files"] = "/" + string.Join("/", veto) + "/";
                p["delete veto files"] = "yes";
            }

            if (share.Recycle)
            {
                p["vfs objects"] = "recycle";
                p["recycle:repository"] = share.RecycleRepository.Trim() is { Length: > 0 } r ? r : ".recycle/%U";
                p["recycle:keeptree"] = share.RecycleKeepTree ? "yes" : "no";
                p["recycle:versions"] = share.RecycleVersions ? "yes" : "no";
            }

            return p;
        }

        // ---- Plumbing ------------------------------------------------------

        /// <summary>
        /// samba ignores case and inner whitespace in a parameter name, so <c>readonly</c>,
        /// <c>read only</c> and <c>Read  Only</c> are one parameter. Matching that is what stops a
        /// value spelled one way being left behind while another is written.
        /// </summary>
        private static string Normalise(string key)
        {
            var sb = new StringBuilder(key.Length);
            var space = false;
            foreach (var c in key.Trim().ToLowerInvariant())
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }

            return sb.ToString() switch
            {
                "browsable" => "browseable",
                "public" => "guest ok",
                "writable" => "writeable",
                _ => sb.ToString(),
            };
        }

        private static bool Contains(List<string> list, string entry) =>
            list.Any(e => string.Equals(e, entry, StringComparison.Ordinal));

        private static List<string> Names(string value) =>
            value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                 .Select(n => n.Trim())
                 .Where(n => n.Length > 0)
                 .ToList();

        /// <summary>samba's own set of words for yes. Anything else is no, which is what samba does too.</summary>
        private static bool Truth(string value, bool fallback) => value.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "on" => true,
            "no" or "false" or "0" or "off" => false,
            _ => fallback,
        };

        internal static string Sha256(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
