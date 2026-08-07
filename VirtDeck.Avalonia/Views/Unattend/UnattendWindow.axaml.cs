using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// Collects everything that goes into the guest's <c>autounattend.xml</c>, one tab per section of the
/// answer file. Only "User accounts" exists so far; the remaining sections (regional settings,
/// partitioning, bloatware removal, ...) are separate tabs to come, which is why the tab strip is on
/// the left where a long list of sections still reads.
///
/// The window edits a clone and hands it back through <see cref="Result"/>, so Cancel discards
/// everything without the caller needing to keep its own copy.
///
/// Import/Export exchange the real answer file with a file on <b>this PC</b>, not on the host: unlike
/// an install ISO, which the VM has to read and which is therefore worth keeping server-side, an
/// answer file is authored here and lands on the host only as the generated disc.
/// </summary>
public partial class UnattendWindow : Window
{
    private const string XmlFilter = "Answer files (*.xml)|*.xml|All files (*.*)|*.*";

    /// <summary>The edited config, or null while the window has not been accepted.</summary>
    public UnattendConfig? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public UnattendWindow() : this(null) { }

    public UnattendWindow(UnattendConfig? config)
    {
        InitializeComponent();

        AccountsTab.Load((config ?? new UnattendConfig()).Clone().UserAccounts);

        OkButton.Click += (_, _) =>
        {
            Result = CurrentConfig();
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        ExportButton.Click += async (_, _) => await ExportAsync();
        ImportButton.Click += async (_, _) => await ImportAsync();
    }

    /// <summary>What the pages describe right now. Read by OK and by Export alike, so an exported file
    /// is the same bytes the disc would carry.</summary>
    private UnattendConfig CurrentConfig()
    {
        var config = new UnattendConfig();
        AccountsTab.Apply(config.UserAccounts);
        return config;
    }

    private async Task ExportAsync()
    {
        var path = await FileDialogs.SaveFileAsync(this, "Export answer file", XmlFilter,
                                                   UnattendMedia.FileName, "xml");
        if (path == null) return;

        try
        {
            await File.WriteAllBytesAsync(path, UnattendXmlBuilder.Build(CurrentConfig()));
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Export failed", $"Could not write {path}:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Replaces the pages from an existing answer file. The window models one section of one pass, so
    /// a file can easily say more than it can show; the reader lists what it had to drop and that list
    /// is put to the user as a question, because importing is also the moment that content stops
    /// existing anywhere the window will write.
    /// </summary>
    private async Task ImportAsync()
    {
        var path = await FileDialogs.OpenFileAsync(this, "Import answer file", XmlFilter);
        if (path == null) return;

        UnattendImportResult imported;
        try
        {
            imported = UnattendXmlReader.Parse(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            // Nothing on the pages is touched, so a wrong file costs the user only this dialog.
            await MessageDialog.Info(this, "Import failed", $"Could not read {path}:\n\n{ex.Message}");
            return;
        }

        if (imported.Warnings.Count > 0)
        {
            var message = "Some of this file cannot be shown here and will not be in what this window " +
                          "writes back:\n\n" +
                          string.Join("\n", imported.Warnings.Select(w => "- " + w)) +
                          "\n\nImport the rest?";
            if (!await MessageDialog.Confirm(this, "Import answer file", message)) return;
        }

        AccountsTab.Load(imported.Config.UserAccounts);
    }
}
