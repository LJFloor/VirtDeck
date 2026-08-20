using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// The four add/remove list rows of <see cref="ContainerEditWindow"/>, together because they are one
/// idea repeated: unlike <c>VmEditRows</c>, which renders a model, each of these <b>is</b> the form
/// field, written to as well as read from, exactly as the answer-file window's rows are.
///
/// They all notify. Two of them need to (a mount's kind decides which source editor is on screen,
/// and the catalog arrives after the row was built); the other two are the same shape for the sake
/// of being the same shape.
/// </summary>
public abstract class EditRow : INotifyPropertyChanged
{
    /// <summary>A row nobody typed anything into. Dropped on Apply, but kept on the page so it can still be filled in.</summary>
    public abstract bool IsEmpty { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One volume or bind mount. The kind decides which source editor the template shows, which is why
/// this notifies: the two editors both live in the template and swap on visibility, so switching
/// kind keeps whatever was typed in the other one.
/// </summary>
public sealed class MountRow : EditRow
{
    public const string VolumeKind = "Volume";
    public const string BindKind = "Bind mount";

    private string _kind = VolumeKind;
    private string _volume = string.Empty;
    private string _hostPath = string.Empty;
    private string _target = string.Empty;
    private bool _readOnly;
    private IReadOnlyList<string> _catalogVolumes = Array.Empty<string>();
    private IReadOnlyList<string> _volumes = Array.Empty<string>();

    public MountRow() { }

    public MountRow(MountSpec mount)
    {
        _kind = mount.Kind == MountKind.Bind ? BindKind : VolumeKind;
        if (mount.Kind == MountKind.Bind) _hostPath = mount.Source; else _volume = mount.Source;
        _target = mount.Target;
        _readOnly = mount.ReadOnly;
        RebuildVolumes();
    }

    /// <summary>The kind dropdown's items. An instance property so the row's DataTemplate can bind it.</summary>
    public IReadOnlyList<string> Kinds { get; } = new[] { VolumeKind, BindKind };

    public string Kind
    {
        get => _kind;
        set
        {
            if (value == null || !Set(ref _kind, value)) return;
            Raise(nameof(IsVolume));
            Raise(nameof(IsBind));
        }
    }

    public bool IsVolume => _kind == VolumeKind;
    public bool IsBind => _kind == BindKind;

    /// <summary>The host's named volumes, handed down once the catalog lands.</summary>
    public IReadOnlyList<string> Volumes
    {
        get => _volumes;
        set
        {
            _catalogVolumes = value ?? Array.Empty<string>();
            RebuildVolumes();
        }
    }

    /// <summary>
    /// The picker's own items, which are the catalog plus whatever this mount already says.
    ///
    /// The addition is not cosmetic. The catalog arrives after the row is built, and a container can
    /// name a volume the host no longer lists; either way a closed dropdown would show the row as
    /// unanswered and saving would then quietly drop the mount. Coerced before anything is
    /// announced, the way <c>ScriptRow</c> coerces its language list, so the ComboBox never sees a
    /// moment where its selection is not among its items and clears itself.
    /// </summary>
    private void RebuildVolumes()
    {
        var items = _catalogVolumes.ToList();
        if (_volume.Length > 0 && !items.Contains(_volume)) items.Insert(0, _volume);
        if (items.SequenceEqual(_volumes)) return;

        _volumes = items;
        Raise(nameof(Volumes));
        Raise(nameof(Volume));
    }

    /// <summary>Kept apart from <see cref="HostPath"/> so switching kind does not discard either.</summary>
    public string Volume
    {
        get => _volume;
        set
        {
            // A ComboBox hands back null while its items are being replaced. That is the list
            // changing under the selection, not the user clearing it.
            if (value is null || !Set(ref _volume, value)) return;
            RebuildVolumes();
        }
    }

    public string HostPath
    {
        get => _hostPath;
        set => Set(ref _hostPath, value ?? string.Empty);
    }

    /// <summary>Where it appears inside the container.</summary>
    public string Target
    {
        get => _target;
        set => Set(ref _target, value ?? string.Empty);
    }

    public bool ReadOnly
    {
        get => _readOnly;
        set => Set(ref _readOnly, value);
    }

    public string Source => (IsBind ? _hostPath : _volume).Trim();

    public override bool IsEmpty => Source.Length == 0 && _target.Trim().Length == 0;

    public MountSpec ToMount() => new()
    {
        Kind = IsBind ? MountKind.Bind : MountKind.Volume,
        Source = Source,
        Target = _target.Trim(),
        ReadOnly = _readOnly,
    };
}

/// <summary>
/// One published port: which port inside the container, and which port on the host.
///
/// It also carries a host address the page does not show. Docker can bind a published port to one
/// interface (<c>127.0.0.1:8080:80</c>), and an edit recreates the container from these rows, so
/// dropping the field rather than keeping it would quietly turn a port that was on loopback into one
/// on every interface the host has. Not editable, but not thrown away either.
/// </summary>
public sealed class PortRow : EditRow
{
    private string _hostIp = string.Empty;
    private string _hostPort = string.Empty;
    private string _containerPort = string.Empty;
    private string _protocol = "tcp";

    public PortRow() { }

    public PortRow(PortSpec port)
    {
        _hostIp = port.HostIp;
        _hostPort = port.HostPort;
        _containerPort = port.ContainerPort;
        _protocol = port.Protocol.Length == 0 ? "tcp" : port.Protocol;
    }

    public IReadOnlyList<string> Protocols { get; } = new[] { "tcp", "udp" };

    public string HostIp
    {
        get => _hostIp;
        set => Set(ref _hostIp, value ?? string.Empty);
    }

    public string HostPort
    {
        get => _hostPort;
        set => Set(ref _hostPort, value ?? string.Empty);
    }

    public string ContainerPort
    {
        get => _containerPort;
        set => Set(ref _containerPort, value ?? string.Empty);
    }

    public string Protocol
    {
        get => _protocol;
        set { if (value != null) Set(ref _protocol, value); }
    }

    /// <summary>Only the two fields the page shows decide this, so clearing a row empties it outright.</summary>
    public override bool IsEmpty =>
        _hostPort.Trim().Length == 0 && _containerPort.Trim().Length == 0;

    public PortSpec ToPort() => new()
    {
        HostIp = _hostIp.Trim(),
        HostPort = _hostPort.Trim(),
        ContainerPort = _containerPort.Trim(),
        Protocol = _protocol,
    };
}

/// <summary>One environment variable.</summary>
public sealed class EnvRow : EditRow
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public EnvRow() { }

    public EnvRow(EnvSpec env)
    {
        _key = env.Key;
        _value = env.Value;
    }

    public string Key
    {
        get => _key;
        set => Set(ref _key, value ?? string.Empty);
    }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }

    /// <summary>A value with no name is not a variable, so the key alone decides.</summary>
    public override bool IsEmpty => _key.Trim().Length == 0;

    public EnvSpec ToEnv() => new() { Key = _key.Trim(), Value = _value };
}

/// <summary>One passed-through device. Both paths are on the host.</summary>
public sealed class DeviceRow : EditRow
{
    private string _hostPath = string.Empty;
    private string _containerPath = string.Empty;
    private bool _canRead = true;
    private bool _canWrite = true;
    private bool _canMknod = true;
    private IReadOnlyList<string> _devices = Array.Empty<string>();

    /// <summary>The last host path this row copied across, so a container path of its own is never overwritten.</summary>
    private string _mirrored = string.Empty;

    public DeviceRow() { }

    public DeviceRow(DeviceSpec device)
    {
        _hostPath = device.HostPath;
        // Docker fills the guest path in with the host path when it was not given; showing that back
        // as a value the user typed would be a lie, so an echo of the host path reads as blank.
        _containerPath = device.ContainerPath == device.HostPath ? string.Empty : device.ContainerPath;

        var permissions = device.Permissions.Length == 0 ? "rwm" : device.Permissions;
        _canRead = permissions.Contains('r');
        _canWrite = permissions.Contains('w');
        _canMknod = permissions.Contains('m');
    }

    /// <summary>The host's own device nodes, handed down once the catalog lands. An open list: this is
    /// a text box with suggestions, because a node can appear after the window opened.</summary>
    public IReadOnlyList<string> Devices
    {
        get => _devices;
        set => Set(ref _devices, value ?? Array.Empty<string>());
    }

    public string HostPath
    {
        get => _hostPath;
        set => Set(ref _hostPath, value ?? string.Empty);
    }

    public string ContainerPath
    {
        get => _containerPath;
        set => Set(ref _containerPath, value ?? string.Empty);
    }

    /// <summary>
    /// Copies the host path into the container path, which is what the guest almost always wants: a
    /// device usually appears inside at the path it has outside. Called when the host box loses
    /// focus, so the value is filled in rather than left implied by a placeholder.
    ///
    /// It stops as soon as the container path is one the user typed. That is the same rule the
    /// Create-VM wizard follows when it re-seeds a NIC or a boot disk after the OS changes: a
    /// default may keep moving, an answer may not be taken back.
    /// </summary>
    public void MirrorHostPath()
    {
        var hostPath = _hostPath.Trim();
        if (hostPath.Length == 0) return;

        var containerPath = _containerPath.Trim();
        if (containerPath.Length != 0 && containerPath != _mirrored) return;

        _mirrored = hostPath;
        ContainerPath = hostPath;
    }

    // Three ticks rather than a text box holding "rwm". The set is closed and three members long,
    // which is a row of check boxes by the same rule the answer-file model uses to decide between an
    // id and a mirrored type, and it makes the two ways of writing nonsense (a letter that is not one
    // of these, and the same letter twice) unrepresentable instead of validated.
    public bool CanRead
    {
        get => _canRead;
        set { if (Set(ref _canRead, value)) Raise(nameof(Permissions)); }
    }

    public bool CanWrite
    {
        get => _canWrite;
        set { if (Set(ref _canWrite, value)) Raise(nameof(Permissions)); }
    }

    /// <summary>Whether the container may create the node itself, which is docker's <c>m</c>.</summary>
    public bool CanMknod
    {
        get => _canMknod;
        set { if (Set(ref _canMknod, value)) Raise(nameof(Permissions)); }
    }

    /// <summary>Docker's own spelling of the three ticks, and empty when none of them is set.</summary>
    public string Permissions =>
        (_canRead ? "r" : string.Empty) +
        (_canWrite ? "w" : string.Empty) +
        (_canMknod ? "m" : string.Empty);

    public override bool IsEmpty => _hostPath.Trim().Length == 0;

    public DeviceSpec ToDevice() => new()
    {
        HostPath = _hostPath.Trim(),
        ContainerPath = _containerPath.Trim(),
        Permissions = Permissions,
    };
}
