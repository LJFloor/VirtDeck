using System.Text;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The permission model turned into POSIX ACL specs. Pure, for the reason
    /// <see cref="SambaConfig"/> is pure: this is the part that would be expensive to get wrong, and
    /// the exact command sequence is worth being able to read on one screen.
    ///
    /// <para><b>There are two traps here and they are not the same one.</b></para>
    ///
    /// <para>The first is the one everybody hits: <b>mode bits are not inherited</b>.
    /// <c>chmod -R 777</c> sets what is in the folder at that moment, and a file created afterwards
    /// gets the creating process's own uid, gid and umask instead. So <c>sudo touch</c> in a
    /// world-writable folder lands a <c>root:root 0644</c> file that nobody else can write, and the
    /// folder's 777 had nothing to say about it. A <b>default ACL</b> is what fixes that, because it
    /// is inherited: every file made from then on carries the named entries. <c>g+s</c> is the other
    /// half, so the file also inherits the folder's group rather than the creator's.</para>
    ///
    /// <para>The second is subtler and the default ACL does <b>not</b> cover it. Where a default
    /// ACL exists the umask is ignored entirely, and the new file's <i>mask</i> is computed from the
    /// mode the creating program asked for. A program that asks for 0666, which is shell
    /// redirection and <c>touch</c>, lands <c>mask::rw-</c> and everything works. A program that
    /// asks for or restores a restrictive mode lands <c>mask::r--</c>, and then every named entry
    /// collapses to read-only however generous it is: <c>user:alice:rwx</c> with
    /// <c>#effective:r--</c>. <c>tar</c> restoring an archived 0644 does this, and so do
    /// <c>rsync -a</c>, <c>cp -p</c> and a good many daemons and editors. Measured, not assumed:
    /// a tar extraction into a folder with a correct default ACL produces exactly that.</para>
    ///
    /// <para>Nothing at share level can prevent the second one, because it is the creating
    /// process's own mode. What fixes it is <c>setfacl --set</c> run over the file afterwards, which
    /// recomputes the mask from the entries it was given rather than from a create mode. That is the
    /// whole of "Reapply permissions", and it is load-bearing rather than a convenience. The
    /// matching half for files created <i>through</i> the share is samba's own <c>create mask</c>
    /// and <c>force create mode</c> in <see cref="SambaConfig"/>, which open the group bits before
    /// the kernel ever computes a mask.</para>
    ///
    /// <para><c>chmod -R 777</c>, which is what everybody reaches for, is the opposite of a fix: on
    /// a file that has an ACL, <c>chmod</c> rewrites the <b>mask</b> from its group bits, collapsing
    /// every named entry at once, and it leaves the next file root creates in exactly the same
    /// state. Nothing here ever runs a plain <c>chmod</c> over a tree.</para>
    /// </summary>
    public static class SambaAcl
    {
        /// <summary>The two spec strings one share needs. Separate because they differ, see <see cref="Build"/>.</summary>
        public sealed record AclSpecs(string Access, string Default);

        /// <summary>
        /// The access ACL and the default ACL for a share.
        ///
        /// <para>The two differ in one character class and it matters. The access spec uses capital
        /// <c>X</c>, which means "execute only where it is already set, or the target is a
        /// directory", so a recursive pass does not make every document on the share executable. The
        /// default spec uses literal <c>x</c>, because the thing a default ACL is attached to is
        /// always a directory and <c>X</c> there would be a riddle with one answer.</para>
        ///
        /// <para><b>No mask entry is written</b>, and that is the point rather than an omission:
        /// left to compute it, <c>setfacl --set</c> makes the mask the union of the named entries
        /// and the group entry, which is precisely the behaviour that defeats the create-mode
        /// trap.</para>
        ///
        /// <para><c>o::---</c> is the one revoking act here. It is what makes "No access" mean
        /// something on a host where other people have accounts, and it is why the path deny list in
        /// <see cref="PathProblem"/> exists.</para>
        /// </summary>
        public static AclSpecs Build(SambaShare share, string guestAccount)
        {
            var access = new StringBuilder("u::rwX,g::rwX,o::---");
            var dflt = new StringBuilder("u::rwx,g::rwx,o::---");

            void Add(bool isGroup, string name, bool write)
            {
                var prefix = isGroup ? "g:" : "u:";
                access.Append(',').Append(prefix).Append(name).Append(write ? ":rwX" : ":r-X");
                dflt.Append(',').Append(prefix).Append(name).Append(write ? ":rwx" : ":r-x");
            }

            foreach (var entry in share.Permissions)
            {
                // No access emits nothing at all. Absence is the encoding, in the ACL exactly as in
                // `valid users`: POSIX ACLs have no deny entry, so there is nothing else to write.
                if (entry.Access == ShareAccess.None) continue;
                if (entry.Name.Trim().Length == 0) continue;
                Add(entry.IsGroup, entry.Name.Trim(), entry.Access == ShareAccess.ReadWrite);
            }

            // A guest session hits the filesystem as the host's guest account, so it gets a named
            // entry for that account rather than an open `other` class. Naming it says what is
            // actually happening and keeps `o::---` doing its job for everybody else.
            if (share.Guest != GuestAccess.None && guestAccount.Trim().Length > 0)
                Add(false, guestAccount.Trim(), share.Guest == GuestAccess.ReadWrite);

            return new AclSpecs(access.ToString(), dflt.ToString());
        }

        // Directories where `o::---` plus a recursive rewrite would break the host rather than
        // secure a share. Not a security boundary (VirtDeck runs as root and the user asked for
        // this); a guard against a typo in a path box costing somebody their afternoon.
        //
        // Two lists, because the two questions are different. These are refused as *exact* paths and
        // their children are fine: /srv/media and /home/anna/Share are the ordinary answers, and
        // /var/lib/something is a real place to share from.
        private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
        {
            "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib32", "/lib64", "/libx32",
            "/proc", "/root", "/run", "/sbin", "/srv", "/sys", "/tmp", "/usr", "/var",
        };

        // These are refused with everything under them. The whole tree belongs to the distribution
        // and its package manager, so there is no folder inside one that is somebody's to share and
        // every one of them would be rewritten by the next upgrade anyway. /usr/share was the case
        // that named this: it clears the two-component floor below and is emphatically not shareable.
        private static readonly string[] SystemTrees =
            ["/bin/", "/boot/", "/dev/", "/etc/", "/lib/", "/lib32/", "/lib64/", "/libx32/",
             "/proc/", "/root/", "/sbin/", "/sys/", "/usr/"];

        // Where a shared folder normally lives. A path outside these is allowed and confirmed once,
        // rather than refused: /export/media and /zpool/photos are perfectly ordinary answers and no
        // list of ours is going to contain every one of them.
        private static readonly string[] Conventional =
            ["/srv/", "/mnt/", "/media/", "/export/", "/data/", "/share", "/tank/", "/pool", "/volume"];

        /// <summary>
        /// Why this path must not be a shared folder, or null. Checked in C# before anything runs,
        /// because the cheapest place to refuse is the one with no round trip in it.
        /// </summary>
        public static string? PathProblem(string path)
        {
            var trimmed = path.TrimEnd('/');
            if (trimmed.Length == 0) trimmed = "/";

            if (path.Trim().Length == 0) return "Choose a folder to share.";
            if (!path.StartsWith('/')) return "Give the folder as an absolute path.";
            if (path.IndexOfAny(['\n', '\r', '\0']) >= 0) return "A path cannot contain a line break.";
            if (Forbidden.Contains(trimmed)) return $"{trimmed} is part of the host itself and cannot be shared.";

            if (SystemTrees.FirstOrDefault(root => trimmed.StartsWith(root, StringComparison.Ordinal)) is { } tree)
                return $"{trimmed} is inside {tree.TrimEnd('/')}, which is part of the host itself.";

            // One component below the root is either in the list above or something like /data that
            // somebody made on purpose. Two is where ordinary shared folders start.
            if (trimmed.Count(c => c == '/') < 2 && !trimmed.StartsWith("/data", StringComparison.Ordinal))
                return $"{trimmed} is a top-level directory. Share a folder inside one instead.";

            return null;
        }

        /// <summary>
        /// Whether this path is somewhere shared folders usually live. False is not a refusal, it is
        /// the cue for the one confirmation that names what <c>o::---</c> will do to whatever else
        /// was reaching the folder.
        /// </summary>
        public static bool IsConventional(string path) =>
            Conventional.Any(root => path.StartsWith(root, StringComparison.Ordinal));
    }
}
