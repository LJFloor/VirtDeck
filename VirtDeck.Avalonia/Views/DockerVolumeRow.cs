using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the docker volumes list, the sibling of <see cref="DockerNetworkRow"/>: display-only
/// formatting plus change notification, so a refresh updates in place instead of dropping the
/// selection.
///
/// <para>No state dot, for <see cref="ImageRow"/>'s reason: a volume is a fact. An anonymous volume
/// is dimmed whole the way a dangling layer is, because it is the same kind of leftover: nothing
/// chose its name, and it is what a plain prune is for.</para>
/// </summary>
public sealed class DockerVolumeRow : INotifyPropertyChanged
{
    /// <summary>The merge key, and what every command addresses: docker has no id for a volume.</summary>
    public string Name { get; }

    /// <summary>Named after 64 hex characters docker chose. Fixed, because the name is the key.</summary>
    public bool IsAnonymous { get; }

    /// <summary>What the Name cell draws: an anonymous volume's hex cut to 12, the way docker abbreviates an id.</summary>
    public string DisplayName { get; }

    /// <summary>The explorer's dimmed broken symlink and the images table's dangling layer, again.</summary>
    public double RowOpacity => IsAnonymous ? 0.6 : 1.0;

    private string _driver = "";
    public string Driver { get => _driver; private set => Set(ref _driver, value); }

    private string _stack = "";
    public string Stack { get => _stack; private set => Set(ref _stack, value); }

    /// <summary>The project in full on hover, and nothing at all over an empty cell.</summary>
    public string? StackTip => _stack.Length > 0 ? _stack : null;

    private string _created = "";
    public string Created { get => _created; private set => Set(ref _created, value); }

    /// <summary>Docker's own timestamp, the phrase <see cref="Created"/> was rendered from.</summary>
    public string? CreatedTip { get; private set; }

    /// <summary>
    /// What the Created column sorts on. Docker's RFC3339 string does not sort as text once two
    /// volumes carry different offsets (a host whose daylight saving changed between them), so it is
    /// read back into an instant. Unreadable sorts below everything readable.
    /// </summary>
    public long CreatedTicks { get; private set; } = long.MinValue;

    private string _mountpoint = "";
    public string Mountpoint { get => _mountpoint; private set => Set(ref _mountpoint, value); }

    private bool _hasOptions;

    private string _size = "";

    /// <summary>Blank until somebody measures it: a size is never part of the listing.</summary>
    public string Size { get => _size; private set => Set(ref _size, value); }

    /// <summary>What the Size column sorts on, and -1 for a volume nobody has measured, which sorts below every measured one.</summary>
    public long SizeBytes { get; private set; } = -1;

    /// <summary>Every container that mounts it, stopped ones included, because that is what rm and prune count.</summary>
    public IReadOnlyList<DockerVolumeUser> Users { get; private set; } = new List<DockerVolumeUser>();

    /// <summary>Whether <c>docker ps</c> could be asked at all, which is what separates "Unused" from nobody knowing.</summary>
    public bool UsersKnown { get; private set; }

    /// <summary>A volume docker would refuse to remove. False where nobody could look, so Remove still tries and docker answers.</summary>
    public bool IsInUse => UsersKnown && Users.Count > 0;

    /// <summary>The containers a copy of this volume would be racing against.</summary>
    public IReadOnlyList<DockerVolumeUser> RunningUsers => Users.Where(u => u.Running).ToList();

    /// <summary>
    /// "Unused", "2 containers", or nothing at all. The blank is the honest answer when the listing
    /// could not ask, and it is why this is not simply the absence of a count.
    /// </summary>
    public string Status => !UsersKnown
        ? string.Empty
        : Users.Count switch
        {
            0 => "Unused",
            1 => "1 container",
            var n => $"{n} containers",
        };

    /// <summary>
    /// Drives the amber, which on this table means what it means on the images table: this is what
    /// Prune's "all unused" would take.
    /// </summary>
    public bool IsUnused => UsersKnown && Users.Count == 0;

    /// <summary>The status cell alone, exactly as <see cref="ImageRow.StatusOpacity"/>.</summary>
    public double StatusOpacity => IsUnused ? 1.0 : 0.75;

    /// <summary>Who uses it, and which of them are stopped, since a stopped container still keeps it from being removed.</summary>
    public string? StatusTip
    {
        get
        {
            if (!UsersKnown) return "The host could not be asked which containers use this volume.";
            if (Users.Count == 0) return null;
            return string.Join(", ", Users.Select(u => u.Running ? u.Name : $"{u.Name} (stopped)"));
        }
    }

    /// <summary>
    /// Why this volume's files cannot be reached through a directory on the host, or null when they
    /// can. Browse, Measure, Export and Clone all hang off this, and a disabled menu item says it on
    /// hover.
    /// </summary>
    public string? HostPathReason =>
        !string.Equals(_driver, "local", StringComparison.Ordinal)
            ? $"A {_driver} volume keeps its files wherever that plugin puts them, not in a directory on this host."
        : _hasOptions
            ? "This volume mounts a share or a tmpfs that docker attaches only while a container uses it, so there is nothing on the host to read."
        : _mountpoint.Length == 0
            ? "The host has not said where this volume is yet."
        : null;

    public bool CanUseHostPath => HostPathReason is null;

    /// <summary>
    /// A file name to suggest when this volume is exported, <c>.tar.gz</c> for
    /// <see cref="ImageRow.SuggestedFileName"/>'s reason. A volume name is already
    /// <c>[a-zA-Z0-9][a-zA-Z0-9_.-]+</c>, so there is nothing in it a file system refuses.
    /// </summary>
    public string SuggestedFileName => DisplayName + ".tar.gz";

    public DockerVolumeRow(DockerVolumeInfo info)
    {
        Name = info.Name;
        IsAnonymous = DockerService.IsAnonymousVolume(info.Name);
        DisplayName = IsAnonymous ? info.Name[..12] : info.Name;
        Update(info);
    }

    public void Update(DockerVolumeInfo info)
    {
        Driver = info.Driver;
        Stack = info.Stack;
        Mountpoint = info.Mountpoint;
        _hasOptions = info.HasOptions;

        if (DateTimeOffset.TryParse(info.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            Created = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            CreatedTicks = at.UtcTicks;
        }
        else
        {
            Created = string.Empty;
            CreatedTicks = long.MinValue;
        }
        CreatedTip = info.CreatedAt.Length > 0 ? info.CreatedAt : null;

        // None of these has a backing property of its own, and they move together: creating or
        // removing a container elsewhere changes them without anything about the volume moving.
        Users = info.Users;
        UsersKnown = info.UsersKnown;

        Raise(nameof(StackTip));
        Raise(nameof(CreatedTip));
        Raise(nameof(Users));
        Raise(nameof(UsersKnown));
        Raise(nameof(Status));
        Raise(nameof(IsUnused));
        Raise(nameof(StatusOpacity));
        Raise(nameof(StatusTip));
        Raise(nameof(HostPathReason));
        Raise(nameof(CanUseHostPath));
    }

    /// <summary>Puts a measurement on the row, or takes it off again with null.</summary>
    public void ApplySize(long? bytes)
    {
        SizeBytes = bytes ?? -1;
        Size = bytes is { } b ? FormatBytes(b) : string.Empty;
    }

    /// <summary>
    /// <c>du -sb</c>'s byte count in the units the transfer strip already draws, so the same number
    /// reads the same wherever it appears in this module.
    /// </summary>
    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024 * 1024):0.#} TB",
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
