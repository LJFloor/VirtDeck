using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The host this window is connected to, and the saved hosts it can switch to: the flat cell with
/// a chevron that JetBrains puts a project or branch name in, at the left-hand end of the status
/// bar.
///
/// It is the shell's widget, not an <see cref="IModule.StatusWidget"/>. That slot is for a fact one
/// module owns and takes away with it; which host the whole window is about belongs to the shell,
/// like the throughput readout further along the same bar.
///
/// It knows nothing about SSH. <see cref="MainWindow"/> does the connecting, exactly as the
/// containers module runs the docker commands behind
/// <see cref="Containers.HubAccountMenu"/>, because the shell owns the status slot this has to
/// report progress and failure through and a cell with a name in it has nowhere to say that
/// something is happening.
///
/// It is a status bar cell, and <see cref="StatusBarMode"/> is what makes it one rather than an
/// ordinary toolbar cell. That is the reason its metrics are not written in the markup the way
/// <see cref="Containers.HubAccountMenu"/>'s are. The other mode is what it wore on the login form,
/// before the connect window absorbed that job and grew a host list of its own; nothing sets it
/// today, and the property stays because which of the two a cell is remains a property of where it
/// hangs rather than of this control.
/// </summary>
public partial class HostSwitcherMenu : UserControl
{
    /// <summary>What the cell says with no connection behind it.</summary>
    private const string NoHost = "Saved hosts";

    private bool _statusBar;
    private string _currentKey = "";

    public HostSwitcherMenu()
    {
        InitializeComponent();
        HostItem.TemplateApplied += (_, e) => ApplyPlacement(e.NameScope);
    }

    /// <summary>A saved host was picked. Never raised for the one already connected.</summary>
    public event Action<HostProfile>? HostSelected;

    /// <summary>"Add host" was picked: the caller opens the connect window on a blank host.</summary>
    public event Action? AddHostClicked;

    /// <summary>"Forget" was picked for this host.</summary>
    public event Action<HostProfile>? ForgetHostClicked;

    /// <summary>"Manage hosts" was picked: the caller opens the host manager.</summary>
    public event Action? ManageHostsClicked;

    /// <summary>
    /// Whether this instance hangs in the shell's status bar (the default) or sits in an ordinary
    /// form. The three things that make a toolbar cell a status bar cell are the popup opening
    /// upward, the hover being a square block that fills the strip rather than an inset rounded
    /// pill, and the chevron pointing the way the popup opens; a cell on a form wants none of them
    /// and keeps <c>JbTopLevelMenuItem</c>'s own figures.
    ///
    /// Set before the control is shown. It is read when the template is applied, which is also why
    /// it is a plain property and not a styled one: nothing rebinds it.
    /// </summary>
    public bool StatusBarMode
    {
        get => _statusBar;
        set
        {
            _statusBar = value;

            // The status bar cell stretches to the whole height of the strip, which is what a
            // status bar widget does and what a toolbar's pill does not.
            MenuHost.VerticalAlignment = value ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            HostItem.VerticalAlignment = value ? VerticalAlignment.Stretch : VerticalAlignment.Center;

            // MinHeight is cleared rather than written back to a "default" value, because there is
            // no value that means unset here: it rejects NaN outright (measured: it throws on
            // startup), unlike Height, and writing 0 would be a local value outranking whatever
            // JbTopLevelMenuItem sets. ClearValue is what hands the property back to the theme.
            //
            // The 10px either side is the same gap the bar's border padding used to leave beside
            // this cell, moved **inside** the hover, so the highlight reaches the window edge
            // rather than stopping short of it with a dead strip next to it that looks like part
            // of the cell. MainWindow cancels its own padding under this cell and drops the
            // separator's margin on this side to pay for it, so the label sits where it did.
            if (value)
            {
                HostItem.MinHeight = 0;
                HostItem.Padding = new Thickness(10, 0);
            }
            else
            {
                HostItem.ClearValue(Layoutable.MinHeightProperty);
                HostItem.ClearValue(TemplatedControl.PaddingProperty);
            }
        }
    }

    /// <summary>
    /// Draws the saved hosts, with <paramref name="current"/> as the label and ticked in the list.
    ///
    /// Called on every change rather than merged, unlike every table in the app: this list is a
    /// handful of items with no selection to drop and nothing polls it, so rebuilding is both
    /// cheaper and impossible to get out of step.
    ///
    /// <paramref name="disabledReason"/> is what the cell says on hover when it cannot be used
    /// (a switch already in flight); passing one is what disables it. Disabled with a reason
    /// rather than hidden, because a command that comes and goes reads as a bug.
    /// </summary>
    public void Show(HostProfile? current, IReadOnlyList<HostProfile> hosts, string? disabledReason = null)
    {
        // A profile with no host in it is not a host: the design-time shell produces one, and it is
        // not connected to anything.
        if (current is { Host.Length: 0 }) current = null;

        _currentKey = current?.Key ?? "";
        HostLabel.Text = current?.DisplayName ?? NoHost;

        var items = new List<Control>();
        foreach (var host in hosts)
        {
            bool isCurrent = host.Key == _currentKey;
            var item = new MenuItem { Header = host.DisplayName };
            // A name hides the address, so the address goes on hover. Set for every row rather
            // than only the named ones, so the tip is somewhere to look rather than somewhere it
            // sometimes is.
            ToolTip.SetTip(item, host.Label);

            if (isCurrent)
            {
                // Ticked and inert: picking the host already connected is not a command, and
                // disabling it is what says so without taking the row out of the list, where its
                // absence would read as the current host not being saved.
                item.Icon = Tick();
                item.IsEnabled = false;
            }
            else
            {
                var target = host;
                item.Click += (_, _) => HostSelected?.Invoke(target);
            }

            items.Add(item);
        }

        if (items.Count != 0) items.Add(new Separator());
        items.Add(Command("Add host", () => AddHostClicked?.Invoke()));

        // Offered whatever the list holds, including nothing: it is where a host is defined
        // deliberately, rather than being created as a side effect of connecting to it. "Add host"
        // stays beside it as the connect-now route; neither replaces the other.
        var manage = Command("Manage hosts", () => ManageHostsClicked?.Invoke());
        ToolTip.SetTip(manage, "Name, edit, reorder and remove the saved hosts.");
        items.Add(manage);

        // Only for a host that is actually in the list. The shell goes on naming the host it is
        // connected to after that host has been forgotten, and offering to forget it twice would
        // be offering to do nothing.
        if (current != null && hosts.Any(h => h.Key == current.Key))
        {
            var target = current;
            var forget = Command($"Forget {target.DisplayName}", () => ForgetHostClicked?.Invoke(target));
            ToolTip.SetTip(forget, "Removes it from this list and deletes its saved passwords. " +
                                   "The connection you are on now stays open.");
            items.Add(forget);
        }

        HostItem.ItemsSource = items;

        HostItem.IsEnabled = disabledReason is null;
        // The tip hangs off the Menu, which stays enabled, so it is still read when the item is not.
        ToolTip.SetTip(MenuHost, disabledReason ?? (current != null
            ? $"Connected to {current.Label}. Pick another saved host to switch."
            : "Hosts you have connected to before."));
    }

    private static MenuItem Command(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>
    /// The tick beside the host already connected. A Path rather than a glyph character so it is
    /// drawn in the app's own stroked house style and takes the menu's foreground with it, the way
    /// every other piece of vector art here does.
    /// </summary>
    private static Control Tick()
    {
        var tick = new global::Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M2,8 L6,12 L14,4"),
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
        };
        // Themed, never a literal: this has to read on both faces, which is the rule every brush
        // outside the two deliberately unthemed pairs follows.
        tick[!global::Avalonia.Controls.Shapes.Shape.StrokeProperty] =
            new DynamicResourceExtension("JbButtonForeground");
        return tick;
    }

    /// <summary>
    /// The status bar adaptations, applied from the template rather than as styles, and that is
    /// measured rather than assumed: a property written literally in a ControlTemplate lands on the
    /// template child as a **local value**, which outranks a style setter, so a
    /// <c>/template/ Popup#PART_Popup</c> style setting Placement is accepted, applied and silently
    /// loses. Writing them here is another local value, applied after the template built the child,
    /// so these are the ones that stand. See <see cref="Containers.HubAccountMenu"/>, which found
    /// this first and records the version it was verified against.
    ///
    /// TopEdgeAligned**Left** here where that one uses Right: this cell is docked against the left
    /// edge of the bar, so it aligns to the edge it is already against.
    /// </summary>
    private void ApplyPlacement(INameScope template)
    {
        if (!_statusBar) return;

        if (template.Find<Popup>("PART_Popup") is { } popup)
            popup.Placement = PlacementMode.TopEdgeAlignedLeft;

        // The cell stretches to the bar's full height, so a radius would leave the corners of the
        // strip unpainted.
        if (template.Find<Border>("Root") is { } root)
            root.CornerRadius = default;

        // Fully qualified from the root: inside this namespace "Avalonia" is VirtDeck's own, and
        // Path unqualified would be System.IO's.
        if (template.Find<global::Avalonia.Controls.Shapes.Path>("Chevron") is { } chevron)
            chevron.RenderTransform = new RotateTransform(180);
    }
}
