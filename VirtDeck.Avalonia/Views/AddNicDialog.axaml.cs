using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>Builds a <see cref="NicAddOp"/> for a bridge, virtual-network or direct (macvtap) adapter.</summary>
public partial class AddNicDialog : Window
{
    private readonly VirshService _virsh;
    private List<string> _bridges = new();
    private List<string> _networks = new();
    private List<string> _directs = new();

    public NicAddOp? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public AddNicDialog() : this(null!) { }

    public AddNicDialog(VirshService virsh)
    {
        _virsh = virsh;
        InitializeComponent();

        ModelBox.ItemsSource = new[] { "virtio", "e1000e", "rtl8139" };
        ModelBox.SelectedIndex = 0;
        ModeBox.ItemsSource = VirshService.DirectModes;
        ModeBox.SelectedIndex = 0;

        BridgeRadio.IsCheckedChanged += (_, _) => PopulateSource();
        NetworkRadio.IsCheckedChanged += (_, _) => PopulateSource();
        DirectRadio.IsCheckedChanged += (_, _) => PopulateSource();

        OkButton.Click += async (_, _) => await AcceptAsync();
        CancelButton.Click += (_, _) => Close();

        Opened += async (_, _) =>
        {
            try
            {
                var (bridges, networks, directs) = await Task.Run(() =>
                    (_virsh.ListBridges(), _virsh.ListNetworks(), _virsh.ListDirectSources()));
                _bridges = bridges;
                _networks = networks;
                _directs = directs;
            }
            catch { /* leave the box free-text */ }
            PopulateSource();
        };
    }

    private string Kind => BridgeRadio.IsChecked == true ? "bridge"
                         : DirectRadio.IsChecked == true ? "direct" : "network";

    private void PopulateSource()
    {
        var (items, fallback) = Kind switch
        {
            "bridge" => (_bridges, "br0"),
            "direct" => (_directs, "eth0"),
            _ => (_networks, "default"),
        };
        SourceBox.ItemsSource = items;
        SourceBox.Text = items.Count > 0 ? items[0] : fallback;
        ModePanel.IsVisible = Kind == "direct";
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
            Type = Kind,
            Source = source,
            Model = (string)ModelBox.SelectedItem!,
            Mode = (string)ModeBox.SelectedItem!,
        };
        Close(true);
    }
}
