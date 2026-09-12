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

    /// <summary>
    /// The scheduled restart or shutdown row, and what it is about. Kept so the countdown in it is
    /// recomputed when the menu is opened: the menu is redrawn only when something changes, which
    /// is what keeps a four second poll from rebuilding it forever, and a line saying "in 20 min"
    /// written half an hour ago would be the one row here where that shows.
    /// </summary>
    private MenuItem? _scheduledFact;
    private ScheduledPower? _scheduled;

    public HostSwitcherMenu()
    {
        InitializeComponent();
        HostItem.TemplateApplied += (_, e) => ApplyPlacement(e.NameScope);
        HostItem.SubmenuOpened += (_, _) =>
        {
            if (_scheduledFact != null && _scheduled != null) _scheduledFact.Header = _scheduled.Summary();
        };
    }

    /// <summary>A saved host was picked. Never raised for the one already connected.</summary>
    public event Action<HostProfile>? HostSelected;

    /// <summary>"Manage hosts" was picked: the caller opens the host manager.</summary>
    public event Action? ManageHostsClicked;

    /// <summary>
    /// Restart or Shut down was picked: true for a restart. The delay and the message to logged in
    /// users are asked for by the caller, not here; this cell only says which of the two was
    /// wanted, the same way it only says which host was picked.
    /// </summary>
    public event Action<bool>? PowerRequested;

    /// <summary>The scheduled restart or shutdown should be called off.</summary>
    public event Action? CancelPowerRequested;

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
    /// Four sections, separated: what this machine is, what to do to it, which host to be on, and
    /// the one host-list command.
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
    ///
    /// <paramref name="scheduled"/> is what the host has been told to do and has not done yet, from
    /// anywhere: this app, another VirtDeck, a person at a terminal. It is drawn as a second fact
    /// with the one command that undoes it under it, and both are <b>absent when nothing is
    /// scheduled</b> rather than present and disabled: a call-off for a schedule that does not
    /// exist is not a command somebody goes looking for, the same call the updates module's restart
    /// panel makes.
    /// </summary>
    public void Show(HostProfile? current, IReadOnlyList<HostProfile> hosts,
                     string osName = "", string? disabledReason = null,
                     ScheduledPower? scheduled = null)
    {
        // A profile with no host in it is not a host: the design-time shell produces one, and it is
        // not connected to anything.
        if (current is { Host.Length: 0 }) current = null;

        _currentKey = current?.Key ?? "";
        _scheduled = scheduled;
        _scheduledFact = null;
        HostLabel.Text = current?.DisplayName ?? NoHost;

        // Built as sections and joined with separators afterwards, so an empty one (no probe answer
        // yet, nothing saved yet) takes its rule with it rather than leaving the menu opening or
        // closing on a hairline.
        var sections = new List<List<Control>>();

        if (osName.Length > 0) sections.Add([Fact(osName)]);

        // Under the name, because they are about that machine rather than about which machine to be
        // on. Both open a dialog: how long from now, and what to tell whoever is logged in. Only
        // with a host on the other end: with none there is nothing to restart, and that is the
        // design-time cell rather than a state the shell is ever in.
        if (current != null)
        {
            var restart = Command("Restart", () => PowerRequested?.Invoke(true));
            restart.Icon = PowerGlyph(true);
            ToolTip.SetTip(restart, "Restart the host, after a delay you choose.");
            var off = Command("Shut down", () => PowerRequested?.Invoke(false));
            off.Icon = PowerGlyph(false);
            ToolTip.SetTip(off, "Power the host off, after a delay you choose.");
            sections.Add([restart, off]);

            if (scheduled != null)
            {
                var cancel = Command($"Cancel the {scheduled.Noun}", () => CancelPowerRequested?.Invoke());
                ToolTip.SetTip(cancel, scheduled.Message.Length > 0
                    ? $"Message to logged in users: {scheduled.Message}"
                    : "Call it off. The host stays up.");
                _scheduledFact = Fact(scheduled.Summary());
                sections.Add([_scheduledFact, cancel]);
            }
        }

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
    private static MenuItem Fact(string text) => new() { Header = text, IsEnabled = false };

    private static MenuItem Command(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>
    /// The two power glyphs, and the only colour in this menu: the app's refresh glyph in amber for
    /// restart, the power symbol in red for shut down. Colour rather than the row's own foreground,
    /// which is what the tick below binds to, because these two are the only rows here that do
    /// something to the machine rather than to what this window is looking at, and the pair reads
    /// as a pair at a glance.
    ///
    /// <para>Drawn in the app's 16x16 stroked house style like every other piece of vector art
    /// here, and resolved from <c>JbIconRestart</c> and <c>JbIconRemove</c> with a literal fallback,
    /// which is what every code-built brush lookup in the app does.</para>
    /// </summary>
    private Control PowerGlyph(bool restart)
    {
        // Restart is the app's own refresh glyph, the `IconRefresh` every module's toolbar carries,
        // byte for byte: a circle open at the top-right closed by a right-angle arrowhead. Restart
        // and refresh are the same gesture at two scales, so drawing a second circular arrow for it
        // was one arrow too many. Shut down is the power symbol, a 300 degree arc under a stem
        // through the gap it leaves.
        string data = restart
            ? "M13,8 A5,5 0 1 1 11.54,4.46 L13,5.78 M13,3 V5.78 H10.22"
            : "M 8,1.6 V 7.4 M 5.6,4.34 A 4.8,4.8 0 1 0 10.4,4.34";

        return new global::Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(data),
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            Stroke = Brush(restart ? "JbIconRestart" : "JbIconRemove",
                           restart ? Color.FromRgb(0xD6, 0x8F, 0x00) : Color.FromRgb(0xC7, 0x54, 0x50)),
        };
    }

    /// <summary>A themed brush by key, or the literal it should have been. The app's usual shape.</summary>
    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, out var found) && found is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

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
