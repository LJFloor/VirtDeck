using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the Networks tab. Same shape as <see cref="VmRow"/> — display-only formatting plus
/// change notification, so a refresh updates in place instead of dropping the selection.
/// </summary>
public sealed class NetworkRow : INotifyPropertyChanged
{
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public string Name { get; }

    private string _state = "";
    public string State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StateBrush)); }
    }

    private bool _autostart;
    public bool IsAutostart
    {
        get => _autostart;
        private set { if (Set(ref _autostart, value)) Raise(nameof(Autostart)); }
    }

    public string Autostart => _autostart ? "Yes" : "No";
    public IBrush StateBrush => _state == "active" ? ActiveBrush : InactiveBrush;
    public bool IsActive => _state == "active";

    public NetworkRow(NetworkInfo info)
    {
        Name = info.Name;
        Update(info);
    }

    public void Update(NetworkInfo info)
    {
        State = info.State;
        IsAutostart = info.Autostart;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
