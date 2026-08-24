using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of a services table. Same shape as <see cref="ContainerRow"/> and <see cref="UserRow"/>:
/// display-only formatting plus change notification, so a refresh updates in place instead of
/// dropping the selection. That matters more here than anywhere else in the app, because a 10 second
/// poll refreshes whether or not anybody asked it to.
/// </summary>
public sealed class ServiceRow : INotifyPropertyChanged
{
    // The same three colours the VM, container and account lists use, so a state dot means the same
    // thing app-wide: green is running, amber is on its way somewhere, grey is not going anywhere.
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    private static readonly IBrush TransientBrush = new SolidColorBrush(Color.FromRgb(0xd6, 0x8f, 0x00));
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>
    /// The fourth colour, and the one state this module has that no other list in the app does. A
    /// unit that failed is the single most important thing on the page and drawing it in the same
    /// grey as one that was never started would hide exactly what somebody came to find.
    /// </summary>
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.FromRgb(0xc7, 0x54, 0x50));

    /// <summary>The unit name, which every command addresses it by and the merge keys on.</summary>
    public string Name { get; }

    /// <summary>The unit this row was built from, so a command has the whole record rather than the cells.</summary>
    public SystemdUnit Unit { get; private set; }

    private string _description = "";
    public string Description { get => _description; private set => Set(ref _description, value); }

    private string _activeState = "";
    public string ActiveState
    {
        get => _activeState;
        private set
        {
            if (!Set(ref _activeState, value)) return;
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
            Raise(nameof(CanStart));
            Raise(nameof(CanStop));
            Raise(nameof(CanRestart));
            Raise(nameof(CanReload));
        }
    }

    private string _subState = "";
    public string SubState
    {
        get => _subState;
        private set { if (Set(ref _subState, value)) Raise(nameof(StateText)); }
    }

    private string _fileState = "";
    public string FileState
    {
        get => _fileState;
        private set
        {
            if (!Set(ref _fileState, value)) return;
            Raise(nameof(AutostartOn));
            Raise(nameof(AutostartChangeable));
            Raise(nameof(AutostartHint));
            Raise(nameof(IsMasked));
            Raise(nameof(CanStart));
        }
    }

    private string _pending = "";

    /// <summary>
    /// The verb of a command this row has in flight ("starting", "stopping", ...), or empty.
    ///
    /// <b>This is the one thing on the row the client writes rather than reads.</b> It says a command
    /// is on its way to the host, never what the host will answer, which is what keeps it inside the
    /// rule the autostart tick lives by: the UI is not allowed to lead the host. systemctl blocks
    /// until its job finishes, so without this a slow daemon leaves the row looking frozen for as
    /// long as it takes to come up. <see cref="Update"/> clears it, so the host's answer always wins.
    /// </summary>
    public string Pending
    {
        get => _pending;
        set
        {
            if (!Set(ref _pending, value)) return;
            Raise(nameof(IsPending));
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
            Raise(nameof(CanStart));
            Raise(nameof(CanStop));
            Raise(nameof(CanRestart));
            Raise(nameof(CanReload));
            Raise(nameof(AutostartChangeable));
        }
    }

    public bool IsPending => _pending.Length > 0;

    private bool _canReloadUnit = true;

    /// <summary>
    /// Whether Reload applies: the unit has to declare a reload command <b>and</b> be running, since
    /// there is nothing to reload in a service that is not up. <see cref="SystemdUnit.CanReload"/>
    /// defaults true, so a host that would not answer the batched query leaves the command enabled
    /// and lets systemd refuse in its own words rather than greying out something that would work.
    /// </summary>
    public bool CanReload => !IsPending && _canReloadUnit && _activeState == "active";

    /// <summary>What the dot is painted with. Failure is the one thing here worth its own colour.</summary>
    public IBrush StateBrush => IsPending ? TransientBrush : _activeState switch
    {
        "active" => ActiveBrush,
        "failed" => FailedBrush,
        "activating" or "deactivating" or "reloading" => TransientBrush,
        _ => InactiveBrush,
    };

    /// <summary>
    /// The state as systemd words it, sub-state and all ("active (running)"). The tooltip on the
    /// dot as well as the text in the column: a dot with no name is decoration, and the sub-state is
    /// what separates a one-shot that finished from a daemon that is up.
    /// </summary>
    public string StateText => IsPending
        ? _pending
        : _activeState.Length == 0
            ? "not loaded"
            : _subState.Length == 0 || _subState == _activeState
                ? _activeState
                : $"{_activeState} ({_subState})";

    public bool IsMasked => _fileState == "masked" || _fileState == "masked-runtime";

    /// <summary>Whether the unit comes up at boot. What the autostart tick shows.</summary>
    public bool AutostartOn => _fileState is "enabled" or "enabled-runtime";

    /// <summary>
    /// Whether Enable and Disable mean anything for this unit. A static unit has no
    /// <c>[Install]</c> section and so nothing to enable; a masked one has to be unmasked first; a
    /// generated or transient one has no file on disk to change; an indirect one is enabled through
    /// something else.
    /// </summary>
    public bool AutostartChangeable =>
        !IsPending && _fileState is "enabled" or "enabled-runtime" or "disabled";

    /// <summary>
    /// Why the tick is dead, on hover. Empty when it is not, so the tooltip only ever appears where
    /// there is something to explain.
    /// </summary>
    public string AutostartHint => _fileState switch
    {
        "enabled" or "enabled-runtime" or "disabled" => "",
        "static" => "This unit has no [Install] section, so there is nothing to enable: it is started by whatever needs it.",
        "masked" or "masked-runtime" => "This unit is masked. Unmask it before changing whether it starts at boot.",
        "indirect" => "This unit is enabled through another unit, not on its own.",
        "generated" => "This unit was generated at boot, so there is no file on disk to enable.",
        "transient" => "This unit exists only in memory, so there is nothing to enable.",
        "alias" => "This name is an alias for another unit; change that one instead.",
        "" => "This unit has no file on disk, so there is nothing to enable.",
        _ => $"This unit is {_fileState}, which cannot be enabled or disabled.",
    };

    /// <summary>
    /// What each command applies to. A masked unit cannot be started at all, which is the point of
    /// masking, so Start is off rather than left to fail, and a row with a command already in flight
    /// takes no second one. That per-row rule is what replaced a module-wide busy flag, which used to
    /// grey every command on every list while a background read was in the air.
    /// </summary>
    public bool CanStart =>
        !IsPending && !IsMasked && _activeState is not ("active" or "activating" or "reloading");

    public bool CanStop =>
        !IsPending && _activeState is "active" or "activating" or "reloading" or "failed";

    public bool CanRestart => !IsPending && !IsMasked;

    public ServiceRow(SystemdUnit unit)
    {
        Name = unit.Name;
        Unit = unit;
        Update(unit);
    }

    public void Update(SystemdUnit unit)
    {
        Unit = unit;
        Pending = "";
        Description = unit.Description;
        SubState = unit.SubState;
        ActiveState = unit.ActiveState;
        FileState = unit.FileState;
        Set(ref _canReloadUnit, unit.CanReload, nameof(CanReload));
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
