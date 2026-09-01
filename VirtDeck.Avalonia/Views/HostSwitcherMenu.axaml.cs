using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
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
    /// Draws the menu, with <paramref name="current"/> as the label and ticked in the list.
    ///
    /// Three sections, separated: what this machine is, which host to be on, and the one command.
    /// The first is one <b>disabled item used as a fact rather than a command</b>, which is what a
    /// menu has instead of a heading: this is the one cell in the shell that is about the machine
    /// at the far end, so what it is running belongs in it, and it is the thing the cell's own
    /// label cannot say, that label being the saved host's name or the address it was reached at.
    ///
    /// <paramref name="osName"/> comes from the shell's own module probe, so it costs no round trip
    /// of its own; it is empty until that has answered, and a row with nothing to say is <b>left
    /// out</b> rather than drawn saying so. That is not the absent-tooling rule being broken:
    /// nothing here is a command somebody could go looking for, and a host with no os-release is an
    /// ordinary host rather than one missing something.
    ///
    /// Called on every change rather than merged, unlike every table in the app: this list is a
    /// handful of items with no selection to drop and nothing polls it, so rebuilding is both
    /// cheaper and impossible to get out of step.
    ///
    /// <paramref name="disabledReason"/> is what the cell says on hover when it cannot be used
    /// (a switch already in flight); passing one is what disables it. Disabled with a reason
    /// rather than hidden, because a command that comes and goes reads as a bug.
    /// </summary>
    public void Show(HostProfile? current, IReadOnlyList<HostProfile> hosts,
                     string osName = "", string? disabledReason = null)
    {
        // A profile with no host in it is not a host: the design-time shell produces one, and it is
        // not connected to anything.
        if (current is { Host.Length: 0 }) current = null;

        _currentKey = current?.Key ?? "";
        HostLabel.Text = current?.DisplayName ?? NoHost;

        // Built as sections and joined with separators afterwards, so an empty one (no probe answer
        // yet, nothing saved yet) takes its rule with it rather than leaving the menu opening or
        // closing on a hairline.
        var sections = new List<List<Control>>();

        if (osName.Length > 0) sections.Add([Fact(osName)]);

        var saved = new List<Control>();
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

            saved.Add(item);
        }
        if (saved.Count != 0) sections.Add(saved);

        // The one command, and it is the only one: adding a host, renaming one, repointing one at
        // a different key and forgetting one are all things the manager window does, and it is the
        // window that owns the whole story of each. Offering shortcuts to two of them from here was
        // two more routes into the same window and a menu that grew a row per host management verb.
        var manage = Command("Manage hosts", () => ManageHostsClicked?.Invoke());
        ToolTip.SetTip(manage, "Add, name, edit, reorder and remove the saved hosts.");
        sections.Add([manage]);

        var items = new List<Control>();
        foreach (var section in sections)
        {
            if (items.Count != 0) items.Add(new Separator());
            items.AddRange(section);
        }

        HostItem.ItemsSource = items;

        HostItem.IsEnabled = disabledReason is null;
        // The tip hangs off the Menu, which stays enabled, so it is still read when the item is not.
        ToolTip.SetTip(MenuHost, disabledReason ?? (current != null
            ? $"Connected to {current.Label}. Pick another saved host to switch."
            : "Hosts you have connected to before."));
    }

    /// <summary>
    /// The row at the top: something the host said about itself, drawn as a disabled item.
    ///
    /// Disabled is the whole of what makes it a fact: it greys, it does not highlight under the
    /// pointer and it cannot be pressed, which is exactly the reading wanted. No icon: the greying,
    /// sitting above the first rule and the words themselves are what say this is not a host, and
    /// the theme reserves the 18px gutter whether or not anything is in it, so the label lines up
    /// with the host names below regardless. Nothing is hung on it on hover either, because a
    /// disabled control is not hit-testable and a tip there would never be read.
    /// </summary>
    private static Control Fact(string text) => new MenuItem { Header = text, IsEnabled = false };

    private static MenuItem Command(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>
    /// The tick beside the host already connected, and the only art in this menu. A Path rather
    /// than a glyph character so it is drawn in the app's own stroked house style, the way every
    /// other piece of vector art here is.
    ///
    /// <b>The stroke binds to the row's own Foreground, never to a brush key.</b> Bound to the
    /// brush it would stay at full strength on a row that is disabled, which this one always is;
    /// it is the same reason the file explorer's toolbar glyphs bind to their button's foreground
    /// rather than to <c>JbButtonForeground</c>, which is what this used to do.
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
        tick[!global::Avalonia.Controls.Shapes.Shape.StrokeProperty] = new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor)
            {
                AncestorType = typeof(MenuItem),
            },
        };
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
