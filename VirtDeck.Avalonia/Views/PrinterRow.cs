using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the printers table.
///
/// <para>Holds the whole <see cref="Models.Printer"/> rather than only its cells, so a command has
/// the name and the device it needs without reading text back out of the table.</para>
/// </summary>
public sealed class PrinterRow : INotifyPropertyChanged
{
    /// <summary>The merge key. A CUPS queue name is unique on a host and never changes, because
    /// CUPS has no rename: the name is the queue.</summary>
    public string Key { get; }

    public PrinterRow(Printer printer)
    {
        Key = printer.Name;
        Printer = printer;
        Update(printer);
    }

    public Printer Printer { get; private set; }

    public void Update(Printer printer)
    {
        // Cleared first, so the host's answer always wins over what the client last hoped for.
        Pending = "";

        Printer = printer;
        Name = printer.Name;
        Location = printer.Location;
        MakeAndModel = printer.MakeAndModel;
        Connection = printer.DeviceUri;
        Description = printer.Description;

        AcceptingText = printer.Accepting ? "Yes" : "No";
        SharedText = printer.Shared ? "Yes" : "";
        DefaultText = printer.IsDefault ? "Default" : "";

        Raise(nameof(StateBrush));
        Raise(nameof(StateText));
        Raise(nameof(RowOpacity));
        RaiseCommands();
    }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _location = "";
    public string Location { get => _location; private set => Set(ref _location, value); }

    private string _makeAndModel = "";
    public string MakeAndModel { get => _makeAndModel; private set => Set(ref _makeAndModel, value); }

    private string _connection = "";
    public string Connection { get => _connection; private set => Set(ref _connection, value); }

    private string _description = "";
    public string Description { get => _description; private set => Set(ref _description, value); }

    private string _acceptingText = "";
    public string AcceptingText { get => _acceptingText; private set => Set(ref _acceptingText, value); }

    private string _sharedText = "";
    public string SharedText { get => _sharedText; private set => Set(ref _sharedText, value); }

    private string _defaultText = "";
    public string DefaultText { get => _defaultText; private set => Set(ref _defaultText, value); }

    /// <summary>
    /// The verb of a command this row has in flight ("Enabling", "Deleting"), or empty.
    ///
    /// <para><b>The one thing on this row the client writes rather than reads.</b> Every
    /// <c>Can</c> below is false while it is set, which is a row that genuinely cannot take another
    /// command rather than one that happens to coincide with a background read.
    /// <see cref="Update"/> clears it.</para>
    /// </summary>
    private string _pending = "";
    public string Pending
    {
        get => _pending;
        set
        {
            if (!Set(ref _pending, value)) return;
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
            RaiseCommands();
        }
    }

    public bool IsPending => _pending.Length > 0;

    /// <summary>
    /// Green prints, amber is on its way somewhere or holding, grey is stopped.
    ///
    /// <para>A queue that is enabled but <b>rejecting</b> new jobs is amber too: it will print what
    /// it already has and nothing anybody sends it now, which is not a green state and is the
    /// combination this column exists to make visible.</para>
    /// </summary>
    public IBrush StateBrush =>
        IsPending ? StateBrushes.Transient
        : Printer.State == PrinterState.Stopped ? StateBrushes.Stopped
        : Printer.State == PrinterState.Unknown ? StateBrushes.Stopped
        : Printer.State == PrinterState.HoldingJobs ? StateBrushes.Transient
        : !Printer.Accepting ? StateBrushes.Transient
        : StateBrushes.Running;

    public string StateText
    {
        get
        {
            if (IsPending) return _pending;

            return Printer.State switch
            {
                PrinterState.Idle => "Idle",
                PrinterState.Printing => "Printing",
                PrinterState.HoldingJobs => "Holding jobs",
                PrinterState.Stopped => "Stopped",
                _ => "Unknown",
            };
        }
    }

    /// <summary>The sentence the state dot carries, which is where a stopped queue's reason goes.</summary>
    public string StateHint
    {
        get
        {
            if (IsPending) return _pending + "...";

            var reason = Printer.StateReason.Length > 0 ? ": " + Printer.StateReason : ".";

            return Printer.State switch
            {
                PrinterState.Idle when Printer.Accepting => "Ready.",
                PrinterState.Idle => "Enabled, but not taking new jobs" + Reject(),
                PrinterState.Printing => "Printing.",
                PrinterState.HoldingJobs => "Enabled, but every new job is held.",
                PrinterState.Stopped => "Stopped, so nothing is printing" + reason,
                _ => "CUPS did not say what this queue is doing.",
            };

            string Reject() =>
                Printer.RejectReason.Length > 0 ? ": " + Printer.RejectReason : ".";
        }
    }

    /// <summary>A stopped queue is dimmed as well as dotted, because the dot is 9px and the row is
    /// a line somebody is scanning.</summary>
    public double RowOpacity => Printer.State == PrinterState.Stopped ? 0.55 : 1.0;

    // ---- What a command may do to this row ------------------------------

    public bool CanEnable => !IsPending && Printer.State == PrinterState.Stopped;
    public bool CanDisable => !IsPending && Printer.State != PrinterState.Stopped;
    public bool CanAccept => !IsPending && !Printer.Accepting;
    public bool CanReject => !IsPending && Printer.Accepting;
    public bool CanSetDefault => !IsPending && !Printer.IsDefault;
    public bool CanDelete => !IsPending;
    public bool CanEdit => !IsPending;

    /// <summary>A stopped queue would only swallow a test page, so the command says so instead.</summary>
    public bool CanTest => !IsPending && Printer.State != PrinterState.Stopped && Printer.Accepting;

    private void RaiseCommands()
    {
        Raise(nameof(IsPending));
        Raise(nameof(StateHint));
        Raise(nameof(CanEnable));
        Raise(nameof(CanDisable));
        Raise(nameof(CanAccept));
        Raise(nameof(CanReject));
        Raise(nameof(CanSetDefault));
        Raise(nameof(CanDelete));
        Raise(nameof(CanEdit));
        Raise(nameof(CanTest));
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
