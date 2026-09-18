namespace VirtDeck.RemoteDesktop
{
    /// <summary>What an X display on the host is being used for.</summary>
    public enum X11SessionKind
    {
        /// <summary>Somebody's desktop session, as logind names it.</summary>
        Session,

        /// <summary>The display manager's greeter: the login screen.</summary>
        LoginScreen,

        /// <summary>An X server logind knows nothing about (startx from a console, Xvnc, Xvfb).</summary>
        Server,
    }

    /// <summary>
    /// One X display on the host that Remote Control can attach to.
    /// </summary>
    /// <param name="Display">":0", as x11vnc's -display wants it.</param>
    /// <param name="User">Whose session it is, or empty when nothing says (a root-owned server with no session on it).</param>
    /// <param name="Kind">Session, login screen or bare server.</param>
    /// <param name="Active">The session on screen at the seat right now.</param>
    /// <param name="AuthFile">The Xauthority file the server was started with, or empty.</param>
    public sealed record X11Session(string Display, string User, X11SessionKind Kind, bool Active, string AuthFile)
    {
        /// <summary>What the session picker shows: ":0 alice", ":0 login screen".</summary>
        public string Label => Kind switch
        {
            X11SessionKind.LoginScreen => $"{Display} login screen",
            _ when User.Length > 0 => $"{Display} {User}",
            _ => $"{Display} X server",
        } + (Active ? " (active)" : "");

        public override string ToString() => Label;
    }

    /// <summary>
    /// What one look at the host found: its architecture, who VirtDeck is logged in as, the X
    /// displays there are, and which agent builds are already in its cache. A failure is a value,
    /// because the module has to draw it.
    /// </summary>
    public sealed record RemoteDesktopProbe(
        string Arch,
        string LoginUser,
        string Home,
        IReadOnlyList<X11Session> Sessions,
        string WaylandUser,
        IReadOnlySet<string> CachedAgents)
    {
        /// <summary>Why the host could not be asked, or null.</summary>
        public string? Failure { get; init; }

        public static RemoteDesktopProbe Failed(string reason) =>
            new("", "", "", [], "", new HashSet<string>()) { Failure = reason };

        /// <summary>
        /// The session worth connecting to without being asked: the login user's own, on screen.
        /// Anyone else's desktop, and the login screen, are an explicit choice.
        /// </summary>
        public X11Session? OwnActiveSession =>
            Sessions.FirstOrDefault(s => s.Kind == X11SessionKind.Session && s.Active && s.User == LoginUser);
    }
}
