using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Network;

/// <summary>What a zone applies to: the interfaces and source networks bound to it.</summary>
public sealed record ZoneBinding(string Zone, IReadOnlyList<string> Interfaces, IReadOnlyList<string> Sources);

/// <summary>
/// Binds interfaces and sources to a firewalld zone. Cockpit's "Add zone" in the same sense: every
/// zone firewalld ships already exists, and adding one means giving it something to judge. With a
/// zone named it edits that zone's bindings instead, ticked as they stand, and the module applies
/// the difference.
/// </summary>
public partial class ZoneBindDialog : Window
{
    private readonly FirewallState _state;
    private readonly IReadOnlyList<string> _interfaces;
    private readonly List<CheckBox> _checks = new();

    public ZoneBinding? Result { get; private set; }

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public ZoneBindDialog() : this(new FirewallState(), [], null)
    {
    }

    public ZoneBindDialog(FirewallState state, IReadOnlyList<string> interfaces, string? zone)
    {
        InitializeComponent();
        _state = state;

        // The host's interfaces, plus any a zone still names that the host no longer has, so a
        // binding to a link that is gone can still be taken away.
        _interfaces = interfaces
            .Concat(state.Zones.SelectMany(z => z.Interfaces))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (zone != null)
        {
            Title = $"Zone - {zone}";
            ApplyButton.Content = "Apply";
            ZoneBox.ItemsSource = new[] { zone };
            ZoneBox.SelectedIndex = 0;
            ZoneBox.IsEnabled = false;
        }
        else
        {
            // The zones nothing is bound to first: those are the ones "adding" means anything for.
            var names = state.Zones
                .OrderBy(z => z.Active)
                .ThenBy(z => z.Name, StringComparer.Ordinal)
                .Select(z => z.Name)
                .ToList();
            ZoneBox.ItemsSource = names;
            ZoneBox.SelectedIndex = names.Count > 0 ? 0 : -1;
        }

        ZoneBox.SelectionChanged += (_, _) => Fill();
        CancelButton.Click += (_, _) => Close();
        ApplyButton.Click += (_, _) => Accept();

        Fill();
    }

    private FirewallZone? Current =>
        _state.Zones.FirstOrDefault(z => z.Name == ZoneBox.SelectedItem as string);

    private void Fill()
    {
        var zone = Current;
        InterfaceChecks.Children.Clear();
        _checks.Clear();

        foreach (var name in _interfaces)
        {
            var elsewhere = _state.Zones.FirstOrDefault(z => z.Name != zone?.Name && z.Interfaces.Contains(name));
            var box = new CheckBox
            {
                Content = elsewhere is null ? name : $"{name}  (now in {elsewhere.Name})",
                Tag = name,
                IsChecked = zone?.Interfaces.Contains(name) == true,
            };
            _checks.Add(box);
            InterfaceChecks.Children.Add(box);
        }

        if (_interfaces.Count == 0)
            InterfaceChecks.Children.Add(new TextBlock { Text = "No interfaces", Opacity = 0.6 });

        SourcesBox.Text = zone is null ? "" : string.Join("\n", zone.Sources);
    }

    private void Accept()
    {
        if (Current is not { } zone)
        {
            Fail("Pick a zone.", ZoneBox);
            return;
        }

        var sources = (SourcesBox.Text ?? "")
            .Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var source in sources)
        {
            if (Cidr.IsAddressOrNetwork(source)) continue;
            Fail($"“{source}” is not an address or a network.", SourcesBox);
            return;
        }

        var interfaces = _checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
        Result = new ZoneBinding(zone.Name, interfaces, sources);
        Close(true);
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }
}
