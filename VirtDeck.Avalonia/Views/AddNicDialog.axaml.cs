using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>Builds a <see cref="NicAddOp"/> for a bridge or virtual-network adapter.</summary>
public partial class AddNicDialog : Window
{
    private readonly VirshService _virsh;
    private List<string> _bridges = new();
    private List<string> _networks = new();

    public NicAddOp? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public AddNicDialog() : this(null!) { }

    public AddNicDialog(VirshService virsh)
    {
        _virsh = virsh;
        InitializeComponent();

        ModelBox.ItemsSource = new[] { "virtio", "e1000e", "rtl8139" };
        ModelBox.SelectedIndex = 0;

        BridgeRadio.IsCheckedChanged += (_, _) => PopulateSource();
        NetworkRadio.IsCheckedChanged += (_, _) => PopulateSource();

        OkButton.Click += async (_, _) => await AcceptAsync();
        CancelButton.Click += (_, _) => Close();

        Opened += async (_, _) =>
        {
            try
            {
                var (bridges, networks) = await Task.Run(() => (_virsh.ListBridges(), _virsh.ListNetworks()));
                _bridges = bridges;
                _networks = networks;
            }
            catch { /* leave the box free-text */ }
            PopulateSource();
        };
    }

    private void PopulateSource()
    {
        bool bridge = BridgeRadio.IsChecked == true;
        var items = bridge ? _bridges : _networks;
        SourceBox.ItemsSource = items;
        SourceBox.Text = items.Count > 0 ? items[0] : (bridge ? "br0" : "default");
    }

    private async Task AcceptAsync()
    {
        var source = SourceBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(source))
        {
            await MessageDialog.Info(this, "Add Adapter", "Choose or enter a source.");
            return;
        }
        Result = new NicAddOp
        {
            Type = BridgeRadio.IsChecked == true ? "bridge" : "network",
            Source = source,
            Model = (string)ModelBox.SelectedItem!,
        };
        Close(true);
    }
}
