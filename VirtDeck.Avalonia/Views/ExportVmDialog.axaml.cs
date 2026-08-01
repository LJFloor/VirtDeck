using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Pipelines;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>One selectable disk in the export list. Block devices and empty drives are offered disabled.</summary>
public sealed class ExportDiskRow
{
    public DiskInfo Disk { get; init; } = new();
    public string Label { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public string? Tooltip { get; init; }
    public bool IsChecked { get; set; }
}

/// <summary>
/// Exports a VM to a <c>.tar</c> containing <c>domain.xml</c> + <c>disks/</c>.
///
/// The disk data streams SSH → pipe → <see cref="TarWriter"/> → file, so a known-size disk never
/// touches local temp storage. <see cref="TarWriter"/> needs the entry size in the header before
/// data flows, which is what <see cref="KnownLengthStream"/> supplies.
/// </summary>
public partial class ExportVmDialog : Window
{
    private readonly SshConnectionManager _ssh;
    private readonly VirshService _virsh;
    private readonly string _vmName;
    private readonly bool _vmIsRunning;

    private List<ExportDiskRow> _diskRows = new();
    private CancellationTokenSource? _cts;
    private string? _tarPath;
    private bool _useSparse;

    private long _speedSampleBytes;
    private long _speedSampleTick;
    private long _speedBps;

    /// <summary>Design-time only.</summary>
    public ExportVmDialog() : this(null!, null!, "", false) { }

    public ExportVmDialog(SshConnectionManager ssh, VirshService virsh, string vmName, bool vmIsRunning)
    {
        _ssh = ssh;
        _virsh = virsh;
        _vmName = vmName;
        _vmIsRunning = vmIsRunning;
        InitializeComponent();

        Title = $"Export VM - {vmName}";

        ExportButton.Click += (_, _) => StartExport();
        CancelSelectButton.Click += (_, _) => Close();
        CancelProgressButton.Click += (_, _) =>
        {
            CancelProgressButton.IsEnabled = false;
            StatusText.Text = "Cancelling…";
            _cts?.Cancel();
        };

        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Start the SSH work before showing the file dialog so it runs in parallel while the
        // user picks a save location.
        var configTask = Task.Run(() => _virsh.GetVmConfig(_vmName));
        var sparseTask = Task.Run(() => _virsh.CheckVirtSparseAvailable());

        _tarPath = await FileDialogs.SaveFileAsync(this, "Export VM", "Tar archive (*.tar)|*.tar",
            suggestedName: _vmName + ".tar", defaultExtension: "tar");
        if (_tarPath == null)
        {
            Close();
            return;
        }

        SelectHint.Text = "Reading VM configuration…";
        ExportButton.IsEnabled = false;

        VmConfig cfg;
        try
        {
            cfg = await configTask;
            await sparseTask;
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Export VM", $"Could not read VM configuration:\n{ex.Message}");
            Close();
            return;
        }

        _diskRows = cfg.Disks.Select(d =>
        {
            bool isBlock = d.SourceType == "block";
            bool isEmpty = d.IsCdrom && string.IsNullOrEmpty(d.Source);
            bool disabled = isBlock || isEmpty;
            string src = string.IsNullOrEmpty(d.Source) ? "(empty)" : Path.GetFileName(d.Source);
            return new ExportDiskRow
            {
                Disk = d,
                Label = $"{d.Target}: {src}" + (d.IsCdrom ? "  [cdrom]" : ""),
                IsEnabled = !disabled,
                Tooltip = isBlock ? "Block devices cannot be exported via SSH streaming" : null,
                IsChecked = !disabled && !d.IsCdrom,
            };
        }).ToList();
        DiskList.ItemsSource = _diskRows;

        SelectHint.Text = "Select disks to include in the export:";
        RunningWarning.IsVisible = _vmIsRunning;

        if (_virsh.VirtSparseAvailable && !_vmIsRunning)
        {
            SparseCheck.IsEnabled = true;
        }
        else
        {
            string msg = _vmIsRunning
                ? "VM must be shut down to sparsify safely."
                : "virt-sparsify not found; install libguestfs-tools on the host.";
            ToolTip.SetTip(SparseCheck, msg);
            SparseHint.Text = msg;
            SparseHint.IsVisible = true;
        }
        ExportButton.IsEnabled = true;
    }

    private void StartExport()
    {
        _useSparse = SparseCheck.IsChecked == true;
        var selected = _diskRows.Where(r => r.IsEnabled && r.IsChecked).Select(r => r.Disk).ToList();

        SelectPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        CancelProgressButton.IsEnabled = true;

        _cts = new CancellationTokenSource();
        _ = RunExportAsync(selected, _cts.Token);
    }

    private async Task RunExportAsync(List<DiskInfo> disks, CancellationToken ct)
    {
        var tarPath = _tarPath!;
        var partPath = tarPath + ".part";

        bool showOverall = disks.Count > 1;
        OverallProgress.IsVisible = showOverall;
        OverallText.IsVisible = showOverall;

        try
        {
            // Phase 1: Sparsify each disk on the server (if requested) before measuring/downloading.
            // virt-sparsify --in-place reclaims unused qcow2 clusters, shrinking the file size.
            if (_useSparse && disks.Count > 0)
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < disks.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var disk = disks[i];
                        string label = $"{Path.GetFileName(disk.Source)} ({i + 1} of {disks.Count})";
                        UpdateStatus($"Sparsifying {label}…");
                        Dispatcher.UIThread.Post(() =>
                        {
                            FileProgress.IsIndeterminate = true;
                            CurrentFileText.Text = "";
                            BytesText.Text = "";
                        });
                        _virsh.SparsifyDisk(disk.Source, line => UpdateStatus($"Sparsifying {label}: {line}"), ct);
                    }
                }, ct);
            }

            // Phase 2: Measure sizes (after sparsify so the tar headers carry the shrunken size).
            var diskSizes = new long[disks.Count];
            long overallTotal = -1;
            if (disks.Count > 0)
            {
                StatusText.Text = "Measuring disk sizes…";
                bool allKnown = true;
                await Task.Run(() =>
                {
                    for (int i = 0; i < disks.Count; i++)
                    {
                        diskSizes[i] = _virsh.GetFileSize(disks[i].Source);
                        if (diskSizes[i] < 0) allKnown = false;
                    }
                }, ct);

                if (allKnown) overallTotal = diskSizes.Sum();
                OverallProgress.IsIndeterminate = overallTotal <= 0;
            }

            StatusText.Text = "Exporting…";

            // Phase 3: Download disks and write the tar.
            await Task.Run(async () =>
            {
                await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 131072);
                await using var tar = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false);

                var xmlBytes = Encoding.UTF8.GetBytes(_virsh.GetDomainXml(_vmName));
                var xmlEntry = new PaxTarEntry(TarEntryType.RegularFile, "domain.xml")
                {
                    DataStream = new MemoryStream(xmlBytes),
                    ModificationTime = DateTimeOffset.UtcNow,
                };
                await tar.WriteEntryAsync(xmlEntry, ct);

                // Two disks can share a basename; prefix with the target only when they collide.
                var baseNames = disks.Select(d => Path.GetFileName(d.Source)).ToList();
                bool hasDupes = baseNames.Count != baseNames.Distinct().Count();

                long overallDone = 0;
                for (int i = 0; i < disks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var disk = disks[i];
                    string baseName = hasDupes
                        ? $"{disk.Target}-{Path.GetFileName(disk.Source)}"
                        : Path.GetFileName(disk.Source);
                    string entryName = $"disks/{baseName}";
                    string displayLabel = $"{Path.GetFileName(disk.Source)} ({i + 1} of {disks.Count})";
                    long diskSize = diskSizes[i];

                    UpdateStatus($"Downloading {displayLabel}…");
                    _speedSampleBytes = 0;
                    _speedSampleTick = Stopwatch.GetTimestamp();
                    _speedBps = 0;
                    var prog = new Progress<(long bytes, long total)>(p =>
                        UpdateFileProgress(displayLabel, p.bytes, p.total,
                            overallDone + p.bytes, overallTotal));

                    if (diskSize > 0)
                    {
                        // Stream directly: SSH → pipe → TarWriter. No temp file, no local copy step.
                        // DownloadFileAsync writes to the pipe writer while WriteEntryAsync reads
                        // from the pipe reader concurrently.
                        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));

                        var downloadTask = Task.Run(async () =>
                        {
                            try
                            {
                                await _ssh.DownloadFileAsync(disk.Source, pipe.Writer.AsStream(), prog, ct,
                                    knownSize: diskSize);
                            }
                            finally { await pipe.Writer.CompleteAsync(); }
                        }, ct);

                        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                        {
                            DataStream = new KnownLengthStream(pipe.Reader.AsStream(), diskSize),
                            ModificationTime = DateTimeOffset.UtcNow,
                        };
                        await tar.WriteEntryAsync(entry, ct);
                        await downloadTask;
                        overallDone += diskSize;
                    }
                    else
                    {
                        // Size unknown; fall back to a temp file so TarWriter can measure it.
                        string tmpFile = Path.GetTempFileName();
                        try
                        {
                            await using (var tmp = new FileStream(tmpFile, FileMode.Create,
                                             FileAccess.Write, FileShare.None, 131072))
                                await _ssh.DownloadFileAsync(disk.Source, tmp, prog, ct);

                            await using var dataStream = new FileStream(tmpFile, FileMode.Open,
                                FileAccess.Read, FileShare.None, 131072);
                            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                            {
                                DataStream = dataStream,
                                ModificationTime = DateTimeOffset.UtcNow,
                            };
                            await tar.WriteEntryAsync(entry, ct);
                            overallDone += new FileInfo(tmpFile).Length;
                        }
                        finally { try { File.Delete(tmpFile); } catch { /* temp cleanup */ } }
                    }
                }
            }, ct);

            if (File.Exists(tarPath)) File.Delete(tarPath);
            File.Move(partPath, tarPath);

            await MessageDialog.Info(this, "Export VM", $"Export complete.\n\n{tarPath}");
            Close(true);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(partPath); } catch { /* partial file may not exist */ }
            StatusText.Text = "Export cancelled.";
            await MessageDialog.Info(this, "Export VM", "Export was cancelled.");
            Close();
        }
        catch (Exception ex)
        {
            try { File.Delete(partPath); } catch { /* partial file may not exist */ }
            StatusText.Text = $"Error: {ex.Message}";
            await MessageDialog.Info(this, "Export VM", $"Export failed:\n{ex.Message}");
            Close();
        }
    }

    private void UpdateStatus(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);

    private void UpdateFileProgress(string label, long fileBytes, long fileTotal,
                                    long overallBytes, long overallTotal)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
                UpdateFileProgress(label, fileBytes, fileTotal, overallBytes, overallTotal));
            return;
        }

        CurrentFileText.Text = label;

        // Rate over the last half-second, not since the start; a stalled tunnel should show as slow.
        var now = Stopwatch.GetTimestamp();
        var sampleSec = (now - _speedSampleTick) / (double)Stopwatch.Frequency;
        if (sampleSec >= 0.5)
        {
            _speedBps = (long)((fileBytes - _speedSampleBytes) / sampleSec);
            _speedSampleBytes = fileBytes;
            _speedSampleTick = now;
        }
        var speedText = _speedBps > 0 ? $"  •  {FormatBytes(_speedBps)}/s" : "";
        BytesText.Text = fileTotal > 0
            ? $"{FormatBytes(fileBytes)} / {FormatBytes(fileTotal)}{speedText}"
            : $"{FormatBytes(fileBytes)}{speedText}";

        if (fileTotal > 0)
        {
            FileProgress.IsIndeterminate = false;
            FileProgress.Value = Math.Clamp(fileBytes * 1000.0 / fileTotal, 0, 1000);
        }
        if (overallTotal > 0)
        {
            OverallProgress.IsIndeterminate = false;
            OverallProgress.Value = Math.Clamp(overallBytes * 1000.0 / overallTotal, 0, 1000);
            OverallText.Text = $"Overall: {FormatBytes(overallBytes)} / {FormatBytes(overallTotal)}";
        }
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024         => $"{b / (1024.0 * 1024):0.#} MB",
        >= 1024                => $"{b / 1024.0:0.#} KB",
        _                      => $"{b} B",
    };

    // Wraps a non-seekable stream with a known length so TarWriter can write the entry header
    // before data flows. Only Length and sequential reads are used; Seek is not supported.
    private sealed class KnownLengthStream(Stream inner, long length) : Stream
    {
        private long _pos;
        public override bool CanRead  => true;
        public override bool CanSeek  => true;   // TarWriter reads Length via a CanSeek guard
        public override bool CanWrite => false;
        public override long Length   => length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void  Flush() { }
        public override long  Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void  SetLength(long value)                 => throw new NotSupportedException();
        public override void  Write(byte[] b, int o, int c)         => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count); _pos += n; return n;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int n = await inner.ReadAsync(buffer, offset, count, ct); _pos += n; return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await inner.ReadAsync(buffer, ct); _pos += n; return n;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
