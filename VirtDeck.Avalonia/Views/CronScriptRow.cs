using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One script in an <c>/etc/cron.&lt;period&gt;</c> directory. These have no schedule of their own:
/// the directory they sit in is the schedule, and run-parts runs whichever of them is executable.
/// </summary>
public sealed class CronScriptRow : INotifyPropertyChanged
{
    public string Key { get; }

    public PeriodicScript Script { get; private set; }

    public CronScriptRow(PeriodicScript script)
    {
        Key = script.Key;
        Script = script;
        Update(script);
    }

    public void Update(PeriodicScript script)
    {
        Script = script;
        Name = script.Name;
        Period = script.Period;
        Mode = script.Mode;
        SizeBytes = script.Size;
        Enabled = script.Enabled;
        Eligible = script.Eligible;
        IsSymlink = script.IsSymlink;
    }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _period = "";
    public string Period { get => _period; private set => Set(ref _period, value); }

    private string _mode = "";
    public string Mode { get => _mode; private set => Set(ref _mode, value); }

    /// <summary>What the Size column sorts on, beside the phrase it is rendered as.</summary>
    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        private set { if (Set(ref _sizeBytes, value)) Raise(nameof(SizeText)); }
    }

    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:0.#} KiB";

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        private set
        {
            if (!Set(ref _enabled, value)) return;
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
            Raise(nameof(RowOpacity));
        }
    }

    private bool _eligible = true;
    public bool Eligible
    {
        get => _eligible;
        private set
        {
            if (!Set(ref _eligible, value)) return;
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
            Raise(nameof(RowOpacity));
        }
    }

    private bool _isSymlink;
    public bool IsSymlink
    {
        get => _isSymlink;
        private set { if (Set(ref _isSymlink, value)) Raise(nameof(StateText)); }
    }

    /// <summary>
    /// Amber rather than grey for a name run-parts will not look at. It is neither on nor off: the
    /// file is executable and looks ready, and the reason it never runs is its name. Every one of
    /// these directories ships a <c>.placeholder</c> to make exactly that point.
    /// </summary>
    public IBrush StateBrush =>
        !Eligible ? StateBrushes.Transient : Enabled ? StateBrushes.Running : StateBrushes.Stopped;

    public string StateText
    {
        get
        {
            var state = !Eligible
                ? "run-parts ignores this name: a dot anywhere in it means the file never runs"
                : Enabled
                    ? $"Executable, so run-parts runs it {Period}"
                    : "Not executable, so run-parts skips it";

            return IsSymlink ? state + "\nA symbolic link: enabling or disabling changes what it points at." : state;
        }
    }

    public double RowOpacity => Enabled && Eligible ? 1.0 : 0.6;

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
