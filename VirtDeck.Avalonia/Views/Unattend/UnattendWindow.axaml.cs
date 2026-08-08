using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// Collects everything that goes into the guest's <c>autounattend.xml</c>, one tab per section of the
/// reference generator at schneegans.de, whose library actually writes the file (see
/// <see cref="UnattendXml"/>). The tab strip is on the left because that list of sections is long.
///
/// The window edits a clone and hands it back through <see cref="Result"/>, so Cancel discards
/// everything without the caller needing to keep its own copy. It never names its pages; see
/// <see cref="IUnattendTab"/>.
///
/// All three file buttons exchange a file with <b>this PC</b>, not with the host: unlike an install
/// ISO, which the VM has to read and which is therefore worth keeping server-side, this is authored
/// here and reaches the host only as the generated disc.
/// </summary>
public partial class UnattendWindow : Window
{
    private const string XmlFilter = "Answer files (*.xml)|*.xml|All files (*.*)|*.*";
    private const string PresetFilter = "Answer file presets (*.json)|*.json|All files (*.*)|*.*";
    private const string PresetFileName = "unattend-preset.json";

    /// <summary>The edited config, or null while the window has not been accepted.</summary>
    public UnattendConfig? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public UnattendWindow() : this(null) { }

    public UnattendWindow(UnattendConfig? config)
    {
        InitializeComponent();

        LoadTabs((config ?? new UnattendConfig()).Clone());

        OkButton.Click += async (_, _) => await AcceptAsync();
        CancelButton.Click += (_, _) => Close(false);
        ExportButton.Click += async (_, _) => await ExportXmlAsync();
        SavePresetButton.Click += async (_, _) => await SavePresetAsync();
        LoadPresetButton.Click += async (_, _) => await LoadPresetAsync();
    }

    /// <summary>Every section page, in tab order. Anything in the strip that is not one is skipped.</summary>
    private IEnumerable<IUnattendTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IUnattendTab>();

    private void LoadTabs(UnattendConfig config)
    {
        foreach (var tab in Tabs) tab.Load(config);
    }

    /// <summary>What the pages describe right now. Read by OK, Save preset and Export alike, so an
    /// exported file is the same bytes the disc would carry.</summary>
    private UnattendConfig CurrentConfig()
    {
        var config = new UnattendConfig();
        foreach (var tab in Tabs) tab.Apply(config);
        return config;
    }

    /// <summary>
    /// Accepts the window, but only once the generator has agreed to build what the pages say.
    ///
    /// Validating here rather than at VM creation is the point: the generator refuses some
    /// combinations (a table of accounts with no administrator in it, a lockout window longer than the
    /// lockout duration), and the alternative is a wizard that defines the VM, creates its disks, and
    /// only then reports that the answer disc could not be written.
    /// </summary>
    private async Task AcceptAsync()
    {
        var config = CurrentConfig();

        try
        {
            // Off the UI thread: the first call also parses the generator's data tables.
            await Task.Run(() => UnattendXml.Build(config));
        }
        catch (UnattendBuildException ex)
        {
            await MessageDialog.Info(this, "Windows setup", ex.Message);
            return;
        }

        Result = config;
        Close(true);
    }

    private async Task ExportXmlAsync()
    {
        var path = await FileDialogs.SaveFileAsync(this, "Export answer file", XmlFilter,
                                                   UnattendMedia.FileName, "xml");
        if (path == null) return;

        try
        {
            var xml = await Task.Run(() => UnattendXml.Build(CurrentConfig()));
            await File.WriteAllBytesAsync(path, xml);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Export failed", ex.Message);
        }
    }

    private async Task SavePresetAsync()
    {
        var path = await FileDialogs.SaveFileAsync(this, "Save preset", PresetFilter,
                                                   PresetFileName, "json");
        if (path == null) return;

        try
        {
            await File.WriteAllBytesAsync(path, UnattendPreset.Serialize(CurrentConfig()));
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Save failed", $"Could not write {path}:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Replaces every page from a preset. A preset is this window's own file and round-trips exactly,
    /// so unlike reading back an answer file there is nothing to warn about and nothing to drop.
    /// </summary>
    private async Task LoadPresetAsync()
    {
        var path = await FileDialogs.OpenFileAsync(this, "Load preset", PresetFilter);
        if (path == null) return;

        UnattendConfig loaded;
        try
        {
            loaded = UnattendPreset.Parse(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            // Nothing on the pages is touched, so a wrong file costs the user only this dialog.
            await MessageDialog.Info(this, "Load failed", $"Could not read {path}:\n\n{ex.Message}");
            return;
        }

        LoadTabs(loaded);
    }
}
