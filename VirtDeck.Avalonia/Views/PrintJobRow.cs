using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the jobs table.
///
/// <para><b>The State column says only what CUPS will actually tell a command line.</b> lpstat
/// asks cupsd for <c>job-state</c> and uses it to decide which jobs to list, but it never prints
/// it, and there is no other CLI that does; so a job is Queued or Completed here, and a held job
/// is not distinguishable from a waiting one. Hold and Release are therefore both offered on every
/// queued job rather than one of them being greyed out on a guess, and CUPS's own refusal is what
/// the user sees if the job was not in the state they assumed. See <c>docs/printing.md</c>.</para>
/// </summary>
public sealed class PrintJobRow : INotifyPropertyChanged
{
    /// <summary>The merge key: CUPS's own <c>NAME-JOBID</c> token, which is unique per host.</summary>
    public string Key { get; }

    public PrintJobRow(PrintJob job)
    {
        Key = job.Id;
        Job = job;
        Update(job);
    }

    public PrintJob Job { get; private set; }

    public void Update(PrintJob job)
    {
        Pending = "";

        Job = job;
        Id = job.Id;
        Printer = job.Printer;
        User = job.User;

        // The number CUPS printed is bytes (it multiplies job-k-octets up before printing), so it
        // is exact to the kilobyte and no better. -1 is "it would not parse", never 0.
        SizeText = job.SizeBytes >= 0 ? FormatBytes(job.SizeBytes) : job.SizeText;
        SubmittedText = job.SubmittedText;
        StateText = job.Completed ? "Completed" : "Queued";

        Raise(nameof(StateBrush));
        RaiseCommands();
    }

    private string _id = "";
    public string Id { get => _id; private set => Set(ref _id, value); }

    private string _printer = "";
    public string Printer { get => _printer; private set => Set(ref _printer, value); }

    private string _user = "";
    public string User { get => _user; private set => Set(ref _user, value); }

    private string _sizeText = "";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    private string _submittedText = "";
    public string SubmittedText { get => _submittedText; private set => Set(ref _submittedText, value); }

    private string _stateText = "";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    /// <summary>The byte count the Size column sorts on, never its text. -1 where there was none.</summary>
    public long SizeBytes => Job.SizeBytes;

    /// <summary>What the Submitted column sorts on. <see cref="DateTime.MinValue"/> where the host's
    /// date format was not one this can read, which costs the ordering and not the cell.</summary>
    public DateTime Submitted => Job.Submitted;

    private string _pending = "";
    public string Pending
    {
        get => _pending;
        set
        {
            if (!Set(ref _pending, value)) return;
            Raise(nameof(StateBrush));
            RaiseCommands();
        }
    }

    public bool IsPending => _pending.Length > 0;

    public IBrush StateBrush =>
        IsPending ? StateBrushes.Transient
        : Job.Completed ? StateBrushes.Stopped
        : StateBrushes.Running;

    public bool CanCancel => !IsPending && !Job.Completed;
    public bool CanMove => !IsPending && !Job.Completed;
    public bool CanHold => !IsPending && !Job.Completed;
    public bool CanRelease => !IsPending && !Job.Completed;

    private void RaiseCommands()
    {
        Raise(nameof(IsPending));
        Raise(nameof(SizeBytes));
        Raise(nameof(Submitted));
        Raise(nameof(CanCancel));
        Raise(nameof(CanMove));
        Raise(nameof(CanHold));
        Raise(nameof(CanRelease));
    }

    /// <summary>The same scale the volumes and file explorer tables use, so a size reads the same
    /// wherever it appears in this app.</summary>
    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B",
    };

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
