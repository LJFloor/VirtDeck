using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VirtDeck.Diagnostics;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Live diagnostic log viewer backed by <see cref="SpiceLog"/>. Single shared instance —
/// call <see cref="ShowLog"/> to open or focus it. Incoming lines are batched and flushed on a
/// UI timer so even Verbose (per-frame) tracing can't flood the UI thread.
/// </summary>
public partial class LogWindow : Window
{
    private static LogWindow? _instance;

    private readonly object _pendingGate = new();
    private readonly List<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly StringBuilder _text = new();

    public static void ShowLog(Window? owner = null)
    {
        if (_instance == null)
        {
            _instance = new LogWindow();
            _instance.Closed += (_, _) => _instance = null;
            if (owner != null) _instance.Show(owner); else _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    public LogWindow()
    {
        InitializeComponent();

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => FlushTick();

        VerboseCheck.IsChecked = SpiceLog.Verbose;
        VerboseCheck.IsCheckedChanged += (_, _) => SpiceLog.Verbose = VerboseCheck.IsChecked == true;

        ClearButton.Click += (_, _) =>
        {
            SpiceLog.Clear();
            lock (_pendingGate) _pending.Clear();
            _text.Clear();
            LogText.Text = "";
        };

        CopyButton.Click += async (_, _) =>
        {
            if (Clipboard is { } cb && _text.Length > 0) await cb.SetTextAsync(_text.ToString());
        };

        OpenFileButton.Click += async (_, _) => await OpenLogFileAsync();

        Opened += (_, _) =>
        {
            foreach (var line in SpiceLog.Snapshot()) _text.AppendLine(line);
            LogText.Text = _text.ToString();
            ScrollToEnd();

            SpiceLog.LineLogged += OnLineLogged;
            _flushTimer.Start();
        };

        Closed += (_, _) =>
        {
            SpiceLog.LineLogged -= OnLineLogged;
            _flushTimer.Stop();
        };
    }

    // Called from any thread — just buffer; the UI timer drains.
    private void OnLineLogged(string line)
    {
        lock (_pendingGate) _pending.Add(line);
    }

    private void FlushTick()
    {
        string[] batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        if (_text.Length > 1_000_000)
        {
            _text.Clear();
            foreach (var line in SpiceLog.Snapshot()) _text.AppendLine(line);
        }

        foreach (var line in batch) _text.AppendLine(line);
        LogText.Text = _text.ToString();
        if (AutoScrollCheck.IsChecked == true) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        LogText.CaretIndex = LogText.Text?.Length ?? 0;
    }

    /// <summary>
    /// Hands the log file to the desktop. <c>UseShellExecute</c> covers Windows and macOS;
    /// on Linux it does nothing useful, so the file goes to <c>xdg-open</c> explicitly.
    /// </summary>
    private async Task OpenLogFileAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start(new ProcessStartInfo("xdg-open", SpiceLog.FilePath) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(SpiceLog.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Log", $"Could not open log file:\n{ex.Message}");
        }
    }
}
