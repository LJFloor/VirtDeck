using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Whether the container reports itself well.
///
/// The three states are docker's own rather than this page's invention: a container inherits the
/// image's check, turns it off, or declares one, and inspect tells the three apart. Logging used to
/// share this page and is the <see cref="LoggingTab"/> now.
/// </summary>
public partial class HealthTab : UserControl, IContainerTab
{
    /// <summary>
    /// Docker's own defaults, which the boxes open on rather than describing in a note. A zero read
    /// back means the container never said, so it gets the same number docker would apply; the
    /// grace period is absent from this list because 0 is docker's answer there.
    /// </summary>
    private const int DefaultInterval = 30;
    private const int DefaultTimeout = 30;
    private const int DefaultRetries = 3;

    public HealthTab()
    {
        InitializeComponent();

        foreach (var radio in new[] { HealthInheritBox, HealthDisabledBox, HealthCommandBox })
            radio.IsCheckedChanged += (_, _) => UpdateHealth();

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        HealthInheritBox.IsChecked = spec.Health.Mode == HealthMode.Inherit;
        HealthDisabledBox.IsChecked = spec.Health.Mode == HealthMode.Disabled;
        HealthCommandBox.IsChecked = spec.Health.Mode == HealthMode.Command;

        HealthCommandText.Text = spec.Health.Command;
        IntervalBox.Value = spec.Health.IntervalSeconds > 0 ? spec.Health.IntervalSeconds : DefaultInterval;
        TimeoutBox.Value = spec.Health.TimeoutSeconds > 0 ? spec.Health.TimeoutSeconds : DefaultTimeout;
        StartPeriodBox.Value = spec.Health.StartPeriodSeconds;
        RetriesBox.Value = spec.Health.Retries > 0 ? spec.Health.Retries : DefaultRetries;

        UpdateHealth();
    }

    public void Apply(ContainerSpec spec)
    {
        spec.Health = new HealthSpec
        {
            Mode = HealthDisabledBox.IsChecked == true ? HealthMode.Disabled
                 : HealthCommandBox.IsChecked == true ? HealthMode.Command
                 : HealthMode.Inherit,
            // Kept whichever mode is selected, so flipping to "none" and back does not mean typing
            // the command again.
            Command = HealthCommandText.Text?.Trim() ?? string.Empty,
            IntervalSeconds = (int)(IntervalBox.Value ?? 0),
            TimeoutSeconds = (int)(TimeoutBox.Value ?? 0),
            StartPeriodSeconds = (int)(StartPeriodBox.Value ?? 0),
            Retries = (int)(RetriesBox.Value ?? 0),
        };
    }

    /// <summary>Nothing here comes off the host: the three states are docker's own.</summary>
    public void SetCatalog(DockerCatalog catalog) { }

    public string? Validate()
    {
        if (HealthCommandBox.IsChecked == true &&
            (HealthCommandText.Text?.Trim() ?? string.Empty).Length == 0)
            return "The health check has no command to run. Give it one, or pick one of the other " +
                   "two options.";

        var timeout = (int)(TimeoutBox.Value ?? 0);
        var interval = (int)(IntervalBox.Value ?? 0);
        if (HealthCommandBox.IsChecked == true && interval > 0 && timeout > interval)
            return $"The check gives up after {timeout} seconds but runs every {interval}, so one " +
                   "run would still be going when the next starts. Shorten the timeout or lengthen " +
                   "the interval.";

        return null;
    }

    private void UpdateHealth() =>
        HealthCommandPanel.IsEnabled = HealthCommandBox.IsChecked == true;
}
