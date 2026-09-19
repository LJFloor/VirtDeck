using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One filesystem in the Overview module's Filesystems table. The same shape as <see cref="UpdateRow"/> and
/// <see cref="ServiceRow"/>: display-only formatting plus change notification, so the sixty-second
/// re-read updates in place rather than rebuilding rows underneath somebody reading them.
/// </summary>
public sealed class MountRow : INotifyPropertyChanged
{
    /// <summary>
    /// What the table merges on. A mount point is unique by definition, which is the one case in
    /// this app where the obvious field really is the key.
    /// </summary>
    public string Key { get; }

    public MountRow(MountUsage usage)
    {
        Key = usage.Mount;
        Update(usage);
    }

    public string Mount => Key;

    private long _size;
    public long SizeBytes
    {
        get => _size;
        private set { if (Set(ref _size, value)) { Raise(nameof(SizeText)); Raise(nameof(FreeText)); Raise(nameof(Summary)); } }
    }

    private long _used;
    public long UsedBytes
    {
        get => _used;
        private set { if (Set(ref _used, value)) { Raise(nameof(UsedText)); Raise(nameof(FreeText)); Raise(nameof(Summary)); } }
    }

    private double _percent;

    /// <summary>Sorted on and drawn from, so the bar and the figure can never disagree.</summary>
    public double Percent
    {
        get => _percent;
        private set { if (Set(ref _percent, value)) { Raise(nameof(PercentText)); Raise(nameof(Summary)); } }
    }

    public long FreeBytes => Math.Max(0, SizeBytes - UsedBytes);

    public string SizeText => Bytes(SizeBytes);
    public string UsedText => Bytes(UsedBytes);
    public string FreeText => Bytes(FreeBytes);
    public string PercentText => $"{Percent:0}%";

    public string Summary =>
        $"{Mount}: {Bytes(UsedBytes)} of {Bytes(SizeBytes)} used, " +
        $"{Bytes(SizeBytes - UsedBytes)} free";

    public void Update(MountUsage usage)
    {
        SizeBytes = usage.SizeBytes;
        UsedBytes = usage.UsedBytes;
        Percent = usage.Percent;
    }

    /// <summary>
    /// Binary units, which is what <c>df -h</c> prints and so what somebody comparing the two
    /// expects. The byte rates on the graphs above are the same function, so a figure means the
    /// same thing everywhere on this page.
    /// </summary>
    public static string Bytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024 * 1024):0.#} TiB",
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.#} GiB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MiB",
        >= 1024 => $"{b / 1024.0:0.#} KiB",
        _ => $"{b} B",
    };

    /// <summary>
    /// <see cref="Bytes"/> per second. Moved here from the Overview when the network module's rate
    /// columns needed it, so a throughput reads the same on both pages.
    /// </summary>
    public static string Rate(double bytesPerSecond) =>
        $"{Bytes((long)Math.Round(bytesPerSecond))}/s";

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
