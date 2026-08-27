using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Creates a container, and edits one.
///
/// One window for both, unlike VMs, where <c>CreateVmWizard</c> and <c>VmEditWindow</c> are separate
/// screens. A VM is created once and then adjusted piece by piece for years, so the two really are
/// different jobs; a container is not adjustable at all. Docker fixes its image, mounts, network,
/// ports, environment and devices at creation, and <c>docker update</c> reaches only resource limits
/// and the restart policy. Editing therefore <b>is</b> creating, with an old container in the way, so
/// splitting the two would be two screens over one operation.
///
/// The window never names its pages; see <see cref="IContainerTab"/>. It edits a spec of its own and
/// touches the host only from <see cref="SaveAsync"/>, so Cancel costs nothing.
/// </summary>
public partial class ContainerEditWindow : Window
{
    private readonly DockerService? _docker;

    /// <summary>The container being replaced, or null when this is a plain create.</summary>
    private readonly string? _replacingId;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Design-time only.</summary>
    public ContainerEditWindow() : this(null, null, null) { }

    public ContainerEditWindow(DockerService? docker, ContainerSpec? existing, string? replacingId)
    {
        InitializeComponent();

        _docker = docker;
        _replacingId = replacingId;

        // Keyed on replacingId, not on `existing`: the Images tab opens this with a spec that is
        // nothing but a prefilled image, and a window titled "Edit container " with no name after it
        // would be describing something that does not exist yet.
        Title = replacingId is null ? "New container" : $"Edit container {existing?.Name}";
        // A new container is started; an existing one is put back the way it was found, which is a
        // restart because saving replaced it.
        SaveStartButton.Content = replacingId is null ? "Save and start" : "Save and restart";

        LoadTabs(existing ?? new ContainerSpec());

        SaveButton.Click += async (_, _) => await SaveAsync(start: false);
        SaveStartButton.Click += async (_, _) => await SaveAsync(start: true);
        CancelButton.Click += (_, _) => Close(false);
        Opened += async (_, _) => await LoadCatalogAsync();
        Closed += (_, _) =>
        {
            try { _cts.Cancel(); } catch { /* nothing to cancel */ }
            _cts.Dispose();
        };
    }

    private DockerService Docker => _docker ?? throw new InvalidOperationException("Window not attached.");

    /// <summary>Every page, in tab order. Anything in the strip that is not one is skipped.</summary>
    private IEnumerable<IContainerTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IContainerTab>();

    private void LoadTabs(ContainerSpec spec)
    {
        foreach (var tab in Tabs) tab.Load(spec);
    }

    /// <summary>What the pages describe right now.</summary>
    private ContainerSpec CurrentSpec()
    {
        var spec = new ContainerSpec();
        foreach (var tab in Tabs) tab.Apply(spec);
        return spec;
    }

    /// <summary>
    /// The first thing the user has to change, having selected the page that says so. Walking the
    /// TabItems rather than their contents is what lets it select the page: a message about a field
    /// on a page nobody is looking at would have to name the page to be usable.
    /// </summary>
    private string? FirstProblem()
    {
        foreach (var item in SectionTabs.Items.OfType<TabItem>())
            if (item.Content is IContainerTab tab && tab.Validate() is { } problem)
            {
                SectionTabs.SelectedItem = item;
                return problem;
            }
        return null;
    }

    /// <summary>
    /// Fills the pickers in from the host. Deliberately not awaited before the window shows: the
    /// pages are all typeable, so a slow or failed listing costs discoverability and nothing else.
    /// </summary>
    private async Task LoadCatalogAsync()
    {
        if (_docker is null) return;
        try
        {
            var catalog = await Docker.LoadCatalogAsync();
            foreach (var tab in Tabs) tab.SetCatalog(catalog);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The host's images, volumes and networks could not be listed: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves what the pages describe. <paramref name="start"/> is the difference between the two
    /// save buttons and nothing else: both write the same container.
    /// </summary>
    private async Task SaveAsync(bool start)
    {
        if (_docker is null) return;

        if (FirstProblem() is { } problem)
        {
            await MessageDialog.Info(this, Title ?? "Container", problem);
            return;
        }

        var spec = CurrentSpec();

        // Said before anything is touched, because the cost is real and not obvious: docker has no
        // in-place edit, so saving means the container is replaced rather than changed. Keyed on
        // there being an existing container rather than on which button was pressed, since a plain
        // Save replaces it just as thoroughly; a new container has nothing to replace and so asks
        // nothing.
        if (_replacingId is not null && !await MessageDialog.Confirm(this, "Replace container",
                $"{spec.Name} is replaced by a new container. Named volumes and bind mounts survive; " +
                "anything written inside the container itself does not."))
            return;

        SetBusy(true);
        try
        {
            await Docker.SaveAsync(spec, _replacingId, start, Report, _cts.Token);
        }
        catch (Exception ex)
        {
            // Staying open is the point: everything typed is still here to correct.
            SetBusy(false);
            StatusText.Text = string.Empty;
            await MessageDialog.Info(this, Title ?? "Container", ex.Message);
            return;
        }

        Close(true);
    }

    /// <summary>Progress arrives on a pool thread, and on the pull's own SSH read thread.</summary>
    private void Report(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        SaveStartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        SectionTabs.IsEnabled = !busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }
}
