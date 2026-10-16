using System.Globalization;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public sealed record ProcessFact(string Label, string Value);

/// <summary>
/// One process in three tabs: what it is and what it is doing, the files it holds open, and its
/// environment. The live half (state, CPU, memory) comes from the module's sampler, which keeps
/// running while this window is open; the rest is one read of <c>/proc/&lt;pid&gt;</c>, again on
/// Refresh.
///
/// <para>The executable, working directory, open files and environment are readable only by the
/// process's owner or root. For another user's process the window reads them un-elevated first and
/// offers Read as root rather than asking for sudo up front: somebody who opened Properties to see
/// what a process is should not meet a password prompt for it.</para>
/// </summary>
public partial class ProcessDetailsWindow : Window
{
    private readonly ProcessService? _service;
    private readonly ProcessKey _key;
    private HostProcess _process;
    private ProcessSnapshot? _snapshot;
    private ProcessDetails? _details;
    private bool _elevated;
    private bool _exited;
    private bool _reading;

    private readonly TableSort _fileSort;
    private readonly TableSort _envSort;

    public ProcessDetailsWindow() : this(null, new HostProcess(), null) { }

    public ProcessDetailsWindow(ProcessService? service, HostProcess process, ProcessSnapshot? snapshot)
    {
        InitializeComponent();
        _service = service;
        _key = process.Key;
        _process = process;
        _snapshot = snapshot;

        _fileSort = new TableSort(FileHeaderStrip);
        _envSort = new TableSort(EnvHeaderStrip);
        _fileSort.Changed += DrawFiles;
        _envSort.Changed += DrawEnvironment;
        FileSearch.Changed += DrawFiles;
        EnvSearch.Changed += DrawEnvironment;

        RefreshButton.Click += async (_, _) => await ReadAsync();
        CloseButton.Click += (_, _) => Close();
        FileRootButton.Click += async (_, _) => await ReadAsRootAsync();
        EnvRootButton.Click += async (_, _) => await ReadAsRootAsync();

        Title = $"{process.Name} ({process.Pid})";
        TitleName.Text = Title;
        DrawLive();
        Opened += async (_, _) => await ReadAsync();
    }

    /// <summary>One tick from the module. Null once the process has gone.</summary>
    public void Feed(HostProcess? process, ProcessSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (process is null)
        {
            if (_exited) return;
            _exited = true;
            Title = $"{_process.Name} ({_process.Pid}) - exited";
            StatusText.Text = "The process has exited.";
            RefreshButton.IsEnabled = false;
        }
        else _process = process;
        DrawLive();
    }

    private async Task ReadAsRootAsync()
    {
        _elevated = true;
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        if (_service is null || _reading || _exited) return;
        _reading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Reading...";
        try
        {
            var details = await _service.ReadDetailsAsync(_key, _elevated);
            if (details.Exited)
            {
                _exited = true;
                StatusText.Text = "The process has exited.";
                return;
            }
            _details = details;
            StatusText.Text = "";
            CommandBox.Text = details.CommandLine.Length > 0 ? details.CommandLine : $"[{_process.Name}]";
            DrawLive();
            DrawFiles();
            DrawEnvironment();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read the process: {Trim(ex.Message)}";
        }
        finally
        {
            _reading = false;
            RefreshButton.IsEnabled = !_exited;
        }
    }

    // ---- General ---------------------------------------------------------------

    private void DrawLive()
    {
        var p = _process;
        var row = new ProcessRow(p);
        TitleDot.Fill = _exited ? StateBrushes.Stopped : row.StateBrush;
        TitleState.Text = _exited ? "Exited" : row.StateText;

        var parent = _snapshot?.Processes.FirstOrDefault(x => x.Pid == p.ParentPid);
        var status = _details?.Status;

        LeftFacts.ItemsSource = new[]
        {
            new ProcessFact("PID", p.Pid.ToString(CultureInfo.InvariantCulture)),
            new ProcessFact("Parent", parent is null
                ? p.ParentPid.ToString(CultureInfo.InvariantCulture)
                : $"{parent.Name} ({parent.Pid})"),
            new ProcessFact("User", p.User),
            new ProcessFact("CPU", p.CpuPercent is { } c ? $"{c.ToString("0.0", CultureInfo.InvariantCulture)} %" : ""),
            new ProcessFact("Memory", row.MemoryText),
            new ProcessFact("Threads", p.Threads.ToString(CultureInfo.InvariantCulture)),
            new ProcessFact("Nice", p.Nice.ToString(CultureInfo.InvariantCulture)),
            new ProcessFact("Started", (DateTime.Now - TimeSpan.FromSeconds(p.ElapsedSeconds))
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        };

        RightFacts.ItemsSource = new[]
        {
            new ProcessFact("Belongs to", _details?.Owner ?? ""),
            new ProcessFact("Executable", Private(d => d.Executable)),
            new ProcessFact("Working directory", Private(d => d.WorkingDirectory)),
            new ProcessFact("Peak memory", Field(status, "VmHWM")),
            new ProcessFact("Swap", Field(status, "VmSwap")),
            new ProcessFact("Context switches", status is null ? "" :
                $"{Field(status, "voluntary_ctxt_switches")} voluntary, {Field(status, "nonvoluntary_ctxt_switches")} forced"),
            new ProcessFact("CPUs allowed", Field(status, "Cpus_allowed_list")),
            new ProcessFact("Kernel thread", p.IsKernelThread ? "Yes" : "No"),
        };
    }

    private string Private(Func<ProcessDetails, string> pick) =>
        _details is null ? "" : _details.PrivateReadable ? pick(_details) : "Needs root";

    private static string Field(IReadOnlyDictionary<string, string>? status, string key) =>
        status is not null && status.TryGetValue(key, out var v) ? v : "";

    // ---- Open files and Environment -------------------------------------------

    private void DrawFiles()
    {
        if (_details is null) return;
        if (!ShowPrivate(FileList, FilePrivate, FileEmpty, FileRootButton)) return;

        var needle = FileSearch.Needle;
        var items = _details.OpenFiles.Where(f => needle.Length == 0 ||
            f.Fd == needle || f.Target.Contains(needle, StringComparison.OrdinalIgnoreCase));
        FileList.ItemsSource = (_fileSort.Key switch
        {
            "target" => _fileSort.By(items, f => f.Target, StringComparer.Ordinal),
            "fd" => _fileSort.By(items, f => int.TryParse(f.Fd, out var n) ? n : int.MaxValue),
            _ => items,
        }).ToList();

        FileNote.Text = _details.OpenFilesTruncated
            ? $"Showing the first {_details.OpenFiles.Count} descriptors."
            : $"{_details.OpenFiles.Count} open";
    }

    private void DrawEnvironment()
    {
        if (_details is null) return;
        if (!ShowPrivate(EnvList, EnvPrivate, EnvEmpty, EnvRootButton)) return;

        var needle = EnvSearch.Needle;
        var items = _details.Environment.Where(v => needle.Length == 0 ||
            v.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            v.Value.Contains(needle, StringComparison.OrdinalIgnoreCase));
        EnvList.ItemsSource = (_envSort.Key switch
        {
            "name" => _envSort.By(items, v => v.Name, StringComparer.Ordinal),
            "value" => _envSort.By(items, v => v.Value, StringComparer.Ordinal),
            _ => items,
        }).ToList();
    }

    /// <summary>Shows the list, or why it cannot be shown and the one button that would.</summary>
    private bool ShowPrivate(ListBox list, Control blocked, TextBlock text, Button root)
    {
        var readable = _details!.PrivateReadable;
        list.IsVisible = readable;
        blocked.IsVisible = !readable;
        if (readable) return true;

        list.ItemsSource = null;
        text.Text = _elevated
            ? "Root could not read this either."
            : "Only the process owner or root can read this.";
        root.IsVisible = !_elevated;
        return false;
    }

    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
