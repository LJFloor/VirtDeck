using System.Formats.Tar;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Forms
{
    public partial class ExportVmDialog : AppForm
    {
        private readonly SshConnectionManager _ssh;
        private readonly VirshService _virsh;
        private readonly string _vmName;
        private readonly bool _vmIsRunning;

        private record DiskItem(DiskInfo Disk, string Label, bool Disabled, string? Tooltip);
        private List<DiskItem> _diskItems = new();
        private int _lastTooltipIndex = -1;

        private CancellationTokenSource? _cts;
        private string? _tarPath;
        private bool _useSparse;

        private long _speedSampleBytes;
        private long _speedSampleTick;
        private long _speedBps;

        public ExportVmDialog(SshConnectionManager ssh, VirshService virsh, string vmName, bool vmIsRunning)
        {
            _ssh = ssh;
            _virsh = virsh;
            _vmName = vmName;
            _vmIsRunning = vmIsRunning;
            InitializeComponent();
            Text = $"Export VM — {vmName}";
            if (AppIcons.Get("download") is Bitmap dlBmp)
                Icon = Icon.FromHandle(dlBmp.GetHicon());
        }

        private async void ExportVmDialog_Load(object? sender, EventArgs e)
        {
            // Start SSH work before showing the file dialog so it runs in parallel
            // while the user picks a save location.
            var configTask = Task.Run(() => _virsh.GetVmConfig(_vmName));
            var sparseTask = Task.Run(() => _virsh.CheckVirtSparseAvailable());

            using var sfd = new SaveFileDialog
            {
                Title = "Export VM",
                Filter = "Tar archive (*.tar)|*.tar",
                FileName = _vmName + ".tar",
                DefaultExt = "tar",
            };
            if (sfd.ShowDialog(this) != DialogResult.OK)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            _tarPath = sfd.FileName;

            lblSelectHint.Text = "Reading VM configuration…";
            btnExport.Enabled = false;

            VmConfig cfg;
            try
            {
                cfg = await configTask;
                await sparseTask;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not read VM configuration:\n{ex.Message}",
                    "Export VM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.Abort;
                Close();
                return;
            }

            // Build disk item list
            _diskItems = cfg.Disks.Select(d =>
            {
                bool isBlock = d.SourceType == "block";
                bool isEmpty = d.IsCdrom && string.IsNullOrEmpty(d.Source);
                bool disabled = isBlock || isEmpty;
                string src = string.IsNullOrEmpty(d.Source) ? "(empty)" : Path.GetFileName(d.Source);
                string label = $"{d.Target}: {src}" + (d.IsCdrom ? "  [cdrom]" : "");
                string? tooltip = isBlock ? "Block devices cannot be exported via SSH streaming" : null;
                return new DiskItem(d, label, disabled, tooltip);
            }).ToList();

            clbDisks.Items.Clear();
            foreach (var item in _diskItems)
            {
                bool defaultChecked = !item.Disabled && !item.Disk.IsCdrom;
                clbDisks.Items.Add(item.Label, defaultChecked ? CheckState.Checked : CheckState.Unchecked);
            }

            lblSelectHint.Text = "Select disks to include in the export:";
            lblRunningWarning.Visible = _vmIsRunning;
            if (_virsh.VirtSparseAvailable && !_vmIsRunning)
            {
                chkSparse.Enabled = true;
            }
            else if (_vmIsRunning)
            {
                string msg = "VM must be shut down to sparsify safely.";
                toolTip.SetToolTip(chkSparse, msg);
                lblSparseHint.Text = msg;
                lblSparseHint.Visible = true;
            }
            else
            {
                string msg = "virt-sparsify not found — install libguestfs-tools on the host.";
                toolTip.SetToolTip(chkSparse, msg);
                lblSparseHint.Text = msg;
                lblSparseHint.Visible = true;
            }
            btnExport.Enabled = true;
        }

        // Prevent toggling disabled items
        private void clbDisks_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (e.Index >= 0 && e.Index < _diskItems.Count && _diskItems[e.Index].Disabled)
                e.NewValue = e.CurrentValue;
        }

        // Per-item tooltip for disabled entries
        private void clbDisks_MouseMove(object? sender, MouseEventArgs e)
        {
            int idx = clbDisks.IndexFromPoint(e.Location);
            if (idx == _lastTooltipIndex) return;
            _lastTooltipIndex = idx;
            string? tip = (idx >= 0 && idx < _diskItems.Count) ? _diskItems[idx].Tooltip : null;
            toolTip.SetToolTip(clbDisks, tip ?? string.Empty);
        }

        // Draw disabled items in gray
        private void clbDisks_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();

            bool disabled = e.Index < _diskItems.Count && _diskItems[e.Index].Disabled;
            var textColor = disabled ? SystemColors.GrayText : e.ForeColor;

            // Draw the checkbox manually at the standard position
            var checkBounds = new Rectangle(e.Bounds.Left + 2, e.Bounds.Top + 3, 13, 13);
            var state = clbDisks.GetItemCheckState(e.Index);
            ButtonState btnState = disabled
                ? ButtonState.Inactive
                : (state == CheckState.Checked ? ButtonState.Checked : ButtonState.Normal);
            ControlPaint.DrawCheckBox(e.Graphics, checkBounds, btnState);

            // Draw label
            var textRect = new Rectangle(e.Bounds.Left + 18, e.Bounds.Top, e.Bounds.Width - 18, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, clbDisks.Items[e.Index]?.ToString() ?? "",
                e.Font, textRect, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            e.DrawFocusRectangle();
        }

        private void btnExport_Click(object? sender, EventArgs e)
        {
            _useSparse = chkSparse.Checked;

            var selectedDisks = Enumerable.Range(0, _diskItems.Count)
                .Where(i => clbDisks.GetItemChecked(i))
                .Select(i => _diskItems[i].Disk)
                .ToList();

            panelSelect.Visible = false;
            panelProgress.Visible = true;
            btnCancelProgress.Enabled = true;

            _cts = new CancellationTokenSource();
            _ = RunExportAsync(selectedDisks, _cts.Token);
        }

        private void btnCancelSelect_Click(object? sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void btnCancelProgress_Click(object? sender, EventArgs e)
        {
            btnCancelProgress.Enabled = false;
            lblStatus.Text = "Cancelling…";
            _cts?.Cancel();
        }

        private async Task RunExportAsync(List<DiskInfo> disks, CancellationToken ct)
        {
            var tarPath = _tarPath!;
            var partPath = tarPath + ".part";

            bool showOverall = disks.Count > 1;
            progressOverall.Visible = showOverall;
            lblOverall.Visible = showOverall;

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
                            BeginInvoke(() =>
                            {
                                progressFile.Style = ProgressBarStyle.Marquee;
                                lblCurrentFile.Text = "";
                                lblBytes.Text = "";
                            });
                            _virsh.SparsifyDisk(disk.Source,
                                line => UpdateStatus($"Sparsifying {label}: {line}"),
                                ct);
                        }
                    }, ct);
                }

                // Phase 2: Measure sizes (after sparsify so we get the shrunken size for tar headers).
                var diskSizes = new long[disks.Count];
                long overallTotal = -1;
                if (disks.Count > 0)
                {
                    lblStatus.Text = "Measuring disk sizes…";
                    bool allKnown = true;
                    await Task.Run(() =>
                    {
                        for (int i = 0; i < disks.Count; i++)
                        {
                            diskSizes[i] = _virsh.GetFileSize(disks[i].Source);
                            if (diskSizes[i] < 0) allKnown = false;
                        }
                    }, ct);

                    if (allKnown)
                        overallTotal = diskSizes.Sum();

                    progressOverall.Style = overallTotal > 0
                        ? ProgressBarStyle.Continuous
                        : ProgressBarStyle.Marquee;
                }

                lblStatus.Text = "Exporting…";

                // Phase 3: Download disks and write tar.
                await Task.Run(async () =>
                {
                    using var fs  = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, 131072);
                    using var tar = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false);

                    // domain.xml entry
                    var xmlBytes = System.Text.Encoding.UTF8.GetBytes(_virsh.GetDomainXml(_vmName));
                    var xmlEntry = new PaxTarEntry(TarEntryType.RegularFile, "domain.xml")
                    {
                        DataStream = new MemoryStream(xmlBytes),
                        ModificationTime = DateTimeOffset.UtcNow,
                    };
                    await tar.WriteEntryAsync(xmlEntry, ct);

                    // Resolve duplicate basenames
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
                        _speedSampleTick = System.Diagnostics.Stopwatch.GetTimestamp();
                        _speedBps = 0;
                        var prog = new Progress<(long bytes, long total)>(p =>
                            UpdateFileProgress(displayLabel, p.bytes, p.total,
                                overallDone + p.bytes, overallTotal));

                        if (diskSize > 0)
                        {
                            // Stream directly: SSH → pipe → TarWriter. No temp file, no local copy step.
                            // DownloadFileAsync writes to the pipe writer while WriteEntryAsync reads
                            // from the pipe reader concurrently.
                            var pipe = new System.IO.Pipelines.Pipe(
                                new System.IO.Pipelines.PipeOptions(useSynchronizationContext: false));

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
                            // Size unknown — fall back to temp file so TarWriter can measure it.
                            string tmpFile = Path.GetTempFileName();
                            try
                            {
                                using (var tmp = new FileStream(tmpFile, FileMode.Create,
                                    FileAccess.Write, FileShare.None, 131072))
                                    await _ssh.DownloadFileAsync(disk.Source, tmp, prog, ct);

                                using var dataStream = new FileStream(tmpFile, FileMode.Open,
                                    FileAccess.Read, FileShare.None, 131072);
                                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                                {
                                    DataStream = dataStream,
                                    ModificationTime = DateTimeOffset.UtcNow,
                                };
                                await tar.WriteEntryAsync(entry, ct);
                                overallDone += new FileInfo(tmpFile).Length;
                            }
                            finally { try { File.Delete(tmpFile); } catch { } }
                        }
                    }
                }, ct);

                if (File.Exists(tarPath)) File.Delete(tarPath);
                File.Move(partPath, tarPath);

                MessageBox.Show(this,
                    $"Export complete.\n\n{tarPath}",
                    "Export VM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(partPath); } catch { }
                lblStatus.Text = "Export cancelled.";
                MessageBox.Show(this, "Export was cancelled.", "Export VM",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.Cancel;
                Close();
            }
            catch (Exception ex)
            {
                try { File.Delete(partPath); } catch { }
                lblStatus.Text = $"Error: {ex.Message}";
                MessageBox.Show(this, $"Export failed:\n{ex.Message}", "Export VM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.Abort;
                Close();
            }
        }

        private void UpdateStatus(string text)
        {
            if (InvokeRequired) BeginInvoke(() => lblStatus.Text = text);
            else lblStatus.Text = text;
        }

        private void UpdateFileProgress(string label, long fileBytes, long fileTotal,
            long overallBytes, long overallTotal)
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateFileProgress(label, fileBytes, fileTotal, overallBytes, overallTotal));
                return;
            }
            lblCurrentFile.Text = label;

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var sampleSec = (now - _speedSampleTick) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (sampleSec >= 0.5)
            {
                _speedBps = (long)((fileBytes - _speedSampleBytes) / sampleSec);
                _speedSampleBytes = fileBytes;
                _speedSampleTick = now;
            }
            var speedText = _speedBps > 0 ? $"  •  {FormatBytes(_speedBps)}/s" : "";
            lblBytes.Text = fileTotal > 0
                ? $"{FormatBytes(fileBytes)} / {FormatBytes(fileTotal)}{speedText}"
                : $"{FormatBytes(fileBytes)}{speedText}";

            if (fileTotal > 0)
            {
                progressFile.Style = ProgressBarStyle.Continuous;
                progressFile.Value = (int)Math.Clamp(fileBytes * 1000 / fileTotal, 0, 1000);
            }
            if (overallTotal > 0)
            {
                progressOverall.Style = ProgressBarStyle.Continuous;
                progressOverall.Value = (int)Math.Clamp(overallBytes * 1000 / overallTotal, 0, 1000);
                lblOverall.Text = $"Overall: {FormatBytes(overallBytes)} / {FormatBytes(overallTotal)}";
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
            public override bool CanSeek  => true;   // TarWriter reads Length via CanSeek guard
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
}
