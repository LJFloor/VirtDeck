using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Where the container's output goes: the logging driver and the options that driver takes.
///
/// It was half of the Health page, on the reading that both are about what a container says about
/// itself. They are not one subject: a health check is a thing the container runs and logging is
/// where its output is written, and the driver options are a table whose length is the driver's
/// business rather than this page's, which is what a page of its own is for.
///
/// The driver is the whole of what this page decides, so it stands outside any group box and the
/// options it governs are the box, which is the Network page's shape.
/// </summary>
public partial class LoggingTab : UserControl, IContainerTab
{
    /// <summary>The bare phrase, for a host that has not said which driver it logs through.</summary>
    private const string DefaultLabel = "Host default";

    /// <summary>
    /// The entry meaning "write no --log-driver at all", which is not a driver name. It names the
    /// host's own answer once the catalog has given it, and says "host" rather than "docker" for
    /// the reason the Resources page's runtime entry does: the answer is whatever this daemon is
    /// configured for, so it is journald on a host whose daemon.json says so.
    /// </summary>
    private string _defaultLabel = DefaultLabel;

    private readonly ObservableCollection<LogOptionRow> _options = new();

    /// <summary>
    /// The answer as it stands, kept beside the box because the catalog arrives after
    /// <see cref="Load"/> and the box is empty until it does. Same shape as the Resources page's
    /// runtime.
    /// </summary>
    private string _driver = string.Empty;

    public LoggingTab()
    {
        InitializeComponent();

        LogOptionTools.Describe("Add a driver option", "Remove the selected option");
        RowList.Bind(LogOptionList, LogOptionTools, _options, () => new LogOptionRow());

        LogDriverBox.SelectionChanged += (_, _) =>
            _driver = LogDriverBox.SelectedItem as string is { } pick && pick != _defaultLabel
                ? pick
                : string.Empty;

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _driver = spec.LogDriver;
        SelectDriver();

        _options.Clear();
        foreach (var option in spec.LogOptions) _options.Add(new LogOptionRow(option));
    }

    public void Apply(ContainerSpec spec)
    {
        spec.LogDriver = _driver;
        spec.LogOptions = _options.Where(r => !r.IsEmpty).Select(r => r.ToOption()).ToList();
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        _defaultLabel = catalog.DefaultLogDriver.Length > 0
            ? $"{DefaultLabel} ({catalog.DefaultLogDriver})"
            : DefaultLabel;

        // inspect reports the effective driver on every container, set or not, so a value that only
        // matches the daemon's default was a report rather than a choice.
        if (string.Equals(_driver, catalog.DefaultLogDriver, StringComparison.Ordinal))
            _driver = string.Empty;

        var drivers = new List<string> { _defaultLabel };
        drivers.AddRange(catalog.LogDrivers);
        LogDriverBox.ItemsSource = drivers;
        SelectDriver();
    }

    /// <summary>
    /// Nothing to refuse: which options a driver takes is docker's rule and it changes with the
    /// driver, so a check written here would eventually reject something valid.
    /// </summary>
    public string? Validate() => null;

    private void SelectDriver()
    {
        if (LogDriverBox.ItemsSource is not IEnumerable<string> items) return;

        // An unknown name is appended rather than dropped, the way the network picker does it: the
        // container names a driver this daemon no longer lists, and silently moving it to the
        // default would be a change nobody asked for.
        var list = items.ToList();
        if (_driver.Length > 0 && !list.Contains(_driver))
        {
            list.Add(_driver);
            LogDriverBox.ItemsSource = list;
        }
        LogDriverBox.SelectedItem = _driver.Length == 0 ? _defaultLabel : _driver;
    }
}
