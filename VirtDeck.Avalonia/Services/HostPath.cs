namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The check a path picked on the host passes before a window builds a device out of it.
///
/// <para>It is in one place because it used to be in three. Two windows each declared their own
/// copy of the same allow-list regex, and a third, the console's Insert media, declared nothing and
/// checked nothing. Duplicated rules do not stay in step, and the copy that goes missing is never
/// the one anybody notices: the console was the window where a filename went straight to
/// <c>virsh change-media</c>.</para>
///
/// <para>What it is no longer is the thing keeping a filename out of root's shell. That is
/// <c>VirshService</c>'s job now, done by argument vector at the boundary where the value arrives,
/// which is the only layer that can promise it for every caller. What is left here is the job a
/// window should have been doing all along: saying that a path cannot work, in this app's words,
/// before the host says so in libvirt's.</para>
///
/// <para>Which is why a space is allowed now. The old allow-list refused one, so an ordinary
/// <c>/mnt/isos/Windows 11.iso</c> was rejected by the two windows that checked and accepted by the
/// one that did not, and the rejection was reported as invalid characters. A path that the browser
/// itself just listed should not be refused for being spelled the way its owner spelled it.</para>
/// </summary>
internal static class HostPath
{
    // NUL and the line breaks cannot survive the trip to the host as part of one argument, and a
    // leading hyphen is read as an option rather than as a path by every tool this app runs.
    private static readonly char[] Impossible = { '\0', '\n', '\r' };

    /// <summary>Whether the path can be used as it stands.</summary>
    public static bool IsUsable(string path) =>
        path.Length > 0 && path[0] == '/' && path.IndexOfAny(Impossible) < 0;

    /// <summary>Why it could not, in one sentence, for the dialog that has to say so.</summary>
    public const string Unusable =
        "That is not a usable path on the server. It has to be a full path starting with /, " +
        "and it cannot contain a line break.";
}
