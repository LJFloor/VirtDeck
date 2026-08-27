using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// The Docker Hub account the host is logged in as, as a status-bar widget: the flat cell with a
/// chevron that JetBrains puts a branch name in at the bottom right of the window, naming the
/// account and offering the one command that applies to it.
///
/// It is a control of its own rather than markup in the module because the shell is what draws the
/// status bar. The containers module holds one instance for its lifetime and hands it over through
/// <see cref="IModule.StatusWidget"/>; the shell hangs it in the bar while that module is on
/// screen and takes it out again on the way to the next one. One <see cref="Show"/> call repaints
/// it, so the module keeps one method rather than three named fields.
///
/// It knows nothing about docker. The module runs the commands, because the module owns the two
/// status slots either side of this and this has nowhere to say that something is happening.
/// </summary>
public partial class HubAccountMenu : UserControl
{
    /// <summary>What the cell says when the host has no Docker Hub credential.</summary>
    private const string LoggedOut = "Not logged in";

    /// <summary>
    /// What it says when the probe could not ask at all, which is a different answer from being
    /// logged out and must not be drawn as one: a host whose dockerd is down would otherwise read
    /// as signed out to somebody who would then go and sign in again for nothing.
    /// </summary>
    private const string Unknown = "Login state unknown";

    public HubAccountMenu()
    {
        InitializeComponent();
        LoginItem.Click += (_, _) => LoginClicked?.Invoke();
        LogoutItem.Click += (_, _) => LogoutClicked?.Invoke();
        AccountItem.TemplateApplied += (_, e) => FitToStatusBar(e.NameScope);
    }

    public event Action? LoginClicked;
    public event Action? LogoutClicked;

    /// <summary>
    /// The three things that make a toolbar cell a status bar cell. This one sits in the shell's
    /// bar, at the bottom right of the window: the dropdown has to open **upward**, aligned to the
    /// right edge it is already against, the chevron has to point the way it opens, and the hover
    /// has to be a square block filling the strip rather than the inset rounded pill a toolbar
    /// wants (the cell stretches to the bar's full height, so a radius would leave the corners of
    /// the strip unpainted).
    ///
    /// All three are done here rather than as styles, and that is measured rather than assumed: a
    /// property written literally in a ControlTemplate lands on the template child as a **local
    /// value**, which outranks a style setter, so a `/template/ Popup#PART_Popup` style setting
    /// Placement is accepted, applied and silently loses (verified against Avalonia 12.1.1: the
    /// popup still read BottomEdgeAlignedLeft). Writing them from here is another local value,
    /// applied after the template built the child, so these are the ones that stand. The chevron
    /// carries no transform from the template and so could have been a style; it is set here
    /// anyway, because which way this thing opens is one fact and belongs in one place.
    ///
    /// Editing JbTopLevelMenuItem instead is not the alternative it looks like: it would turn every
    /// toolbar dropdown in the app upside down and square to place one widget.
    /// </summary>
    private static void FitToStatusBar(INameScope template)
    {
        if (template.Find<Popup>("PART_Popup") is { } popup)
            popup.Placement = PlacementMode.TopEdgeAlignedRight;

        if (template.Find<Border>("Root") is { } root)
            root.CornerRadius = default;

        // Fully qualified from the root: inside this namespace "Avalonia" is VirtDeck's own, and
        // Path unqualified would be System.IO's.
        if (template.Find<global::Avalonia.Controls.Shapes.Path>("Chevron") is { } chevron)
            chevron.RenderTransform = new RotateTransform(180);
    }

    /// <summary>
    /// Draws the state the host reported. <paramref name="known"/> false means nobody could ask,
    /// whatever <paramref name="user"/> says.
    ///
    /// <paramref name="disabledReason"/> is what the cell says on hover when it cannot be used;
    /// passing one is what disables it. Disabled with a reason rather than hidden, because a
    /// command that comes and goes with something the user cannot see reads as a bug.
    /// </summary>
    public void Show(bool known, string user, string? disabledReason = null)
    {
        var loggedIn = known && user.Length > 0;

        AccountLabel.Text = loggedIn ? user : known ? LoggedOut : Unknown;

        // Only ever one of the two: there is no logging out of an account nobody has, and no
        // logging in over one already there without logging out first.
        LoginItem.IsVisible = !loggedIn;
        LogoutItem.IsVisible = loggedIn;

        AccountItem.IsEnabled = disabledReason is null;

        // The tip hangs off the Menu, which stays enabled, so it is still read when the item is
        // not. When the item is usable it says which registry this is about, since the cell itself
        // has room for a name and nothing else.
        ToolTip.SetTip(MenuHost, disabledReason ?? (loggedIn
            ? $"Signed in to Docker Hub as {user}"
            : known
                ? "Not signed in to Docker Hub. Private images will not pull."
                : "The host did not say whether it is signed in to Docker Hub."));
    }
}
