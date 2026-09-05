using System.ComponentModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Views.Unattend;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// How confined the container is: the capability set, and the two switches around it.
///
/// The tick list is the <b>whole</b> setting rather than a pair of override lists, which is the
/// answer-file window's bloatware page applied to a different table. A tick means the container
/// will have that capability, docker's own fourteen start ticked, and turning the set back into
/// <c>--cap-add</c> and <c>--cap-drop</c> is <c>BuildCreateArgv</c>'s job.
/// </summary>
public partial class SecurityTab : UserControl, IContainerTab
{
    /// <summary>
    /// The answer, kept beside the rows rather than read out of them. The rows are rebuilt when the
    /// catalog lands and says which capabilities this kernel reaches, and a rebuild that read its
    /// ticks off the rows it is replacing would lose them.
    /// </summary>
    private HashSet<string> _capabilities = new(LinuxCapabilities.DockerDefault, StringComparer.Ordinal);

    private int _lastCapability = LinuxCapabilities.All.Count - 1;

    /// <summary>
    /// Any other security option the container carried, kept and written back untouched although
    /// nothing draws it. See <c>ContainerSpec.OtherSecurityOptions</c>.
    /// </summary>
    private List<string> _otherOptions = new();

    /// <summary>
    /// The three confinement settings this page no longer draws, carried from <see cref="Load"/> to
    /// <see cref="Apply"/> untouched. That is <c>_otherOptions</c>'s rule and <c>PortSpec.HostIp</c>'s:
    /// a setting this window does not model is still a setting, and editing a container recreates it,
    /// so dropping one here would quietly take it off the replacement.
    /// </summary>
    private bool _noNewPrivileges;

    private string _seccomp = string.Empty;

    private string _apparmor = string.Empty;

    public SecurityTab()
    {
        InitializeComponent();

        SelectAllButton.Click += (_, _) => SetAll(true);
        SelectNoneButton.Click += (_, _) => SetAll(false);
        SelectDefaultsButton.Click += (_, _) =>
        {
            _capabilities = new HashSet<string>(LinuxCapabilities.DockerDefault, StringComparer.Ordinal);
            BuildRows();
        };

        // Privileged means the full set whatever the list says, so the list greys out to say so.
        // The spec still keeps what was ticked, the way the Network page keeps its port mappings
        // under host networking, and the argv builder is what drops the flags.
        PrivilegedBox.IsCheckedChanged += (_, _) => UpdatePrivileged();

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _capabilities = new HashSet<string>(spec.Capabilities.Select(LinuxCapabilities.Normalise),
                                            StringComparer.Ordinal);
        BuildRows();

        PrivilegedBox.IsChecked = spec.Privileged;
        ReadOnlyBox.IsChecked = spec.ReadOnlyRootfs;
        _noNewPrivileges = spec.NoNewPrivileges;
        _seccomp = spec.Seccomp;
        _apparmor = spec.Apparmor;
        _otherOptions = spec.OtherSecurityOptions.ToList();

        UpdatePrivileged();
    }

    public void Apply(ContainerSpec spec)
    {
        Harvest();
        spec.Capabilities = _capabilities.ToList();

        spec.Privileged = PrivilegedBox.IsChecked == true;
        spec.ReadOnlyRootfs = ReadOnlyBox.IsChecked == true;
        spec.NoNewPrivileges = _noNewPrivileges;
        spec.Seccomp = _seccomp;
        spec.Apparmor = _apparmor;
        spec.OtherSecurityOptions = _otherOptions.ToList();
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        _lastCapability = catalog.LastCapability;
        Harvest();
        BuildRows();
    }

    /// <summary>
    /// Nothing on this page can be typed into, so there is nothing to refuse. The seccomp profile
    /// used to be checked here, and is not any more: the value now only ever comes off the container
    /// itself, so a rule applied to it would refuse a container the host is already running.
    /// </summary>
    public string? Validate() => null;

    /// <summary>
    /// Reads the ticks back into the set. Called before anything that replaces the rows, and before
    /// <see cref="Apply"/>, because <c>CheckRow</c> owns its own tick and nothing here is told when
    /// one moves.
    /// </summary>
    private void Harvest()
    {
        if (CapabilityList.ItemsSource is not IEnumerable<CheckRow> rows) return;
        foreach (var row in rows)
        {
            if (row.IsChecked) _capabilities.Add(row.Id);
            else _capabilities.Remove(row.Id);
        }
    }

    /// <summary>
    /// The table, plus anything the container named that this build does not know about, so a
    /// capability from a newer kernel is shown and kept rather than silently dropped on the next
    /// save.
    /// </summary>
    private void BuildRows()
    {
        var rows = new List<CheckRow>();

        for (var bit = 0; bit < LinuxCapabilities.All.Count; bit++)
        {
            var capability = LinuxCapabilities.All[bit];
            var reachable = bit <= _lastCapability;
            rows.Add(new CheckRow(
                capability.Name,
                capability.Name,
                reachable && _capabilities.Contains(capability.Name),
                reachable,
                reachable
                    ? capability.Summary
                    : "This host's kernel does not have this capability, so it cannot be granted here."));
        }

        var known = LinuxCapabilities.All.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in _capabilities.Where(c => !known.Contains(c)).OrderBy(c => c, StringComparer.Ordinal))
            rows.Add(new CheckRow(extra, extra, true, true,
                "Read off this container. VirtDeck's own table does not name it, which usually means " +
                "a kernel newer than this build."));

        foreach (var row in rows) row.PropertyChanged += OnRowChanged;
        CapabilityList.ItemsSource = rows;
        UpdateCount();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CheckRow.IsChecked)) return;
        Harvest();
        UpdateCount();
    }

    private void SetAll(bool ticked)
    {
        if (CapabilityList.ItemsSource is not IEnumerable<CheckRow> rows) return;
        // A row the kernel does not reach is left alone: ticking it would ask for something that
        // cannot be granted.
        foreach (var row in rows.Where(r => r.IsEnabled)) row.IsChecked = ticked;
    }

    private void UpdateCount()
    {
        var total = CapabilityList.ItemsSource is IEnumerable<CheckRow> rows ? rows.Count(r => r.IsEnabled) : 0;
        var ticked = CapabilityList.ItemsSource is IEnumerable<CheckRow> all ? all.Count(r => r.IsChecked) : 0;

        SelectedCount.Text = ticked == LinuxCapabilities.DockerDefault.Count &&
                             LinuxCapabilities.DockerDefault.All(_capabilities.Contains)
            ? $"{ticked} of {total}, which is docker's own set"
            : $"{ticked} of {total}";
    }

    private void UpdatePrivileged()
    {
        var privileged = PrivilegedBox.IsChecked == true;
        CapabilityPanel.IsEnabled = !privileged;
        ToolTip.SetTip(CapabilityHost, privileged
            ? "A privileged container has every capability, so this list has nothing to decide. " +
              "What is ticked here is kept and comes back when privileged is turned off."
            : null);
    }
}
