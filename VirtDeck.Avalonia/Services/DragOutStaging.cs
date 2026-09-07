using Avalonia.Input;
using Avalonia.Platform.Storage;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The bytes a drag out of the file explorer hands to the desktop, fetched into a directory under
/// the temp directory. The counterpart of <see cref="DropFiles"/>: that one translates what a drop
/// brings in, this one produces what a drag takes out.
///
/// <para><b>Why it is staged rather than streamed.</b> Neither platform lets a drag source promise a
/// stream. Win32 maps <see cref="DataFormat.File"/> to CF_HDROP and Avalonia's OLE wrapper accepts
/// TYMED_HGLOBAL only, so there is no CFSTR_FILECONTENTS to hang an IStream off; X11 maps it to a
/// text/uri-list of file:// URIs and the backend implements no XdndDirectSave0. Both hand the target
/// nothing but paths, and those paths must already be complete files.</para>
///
/// <para><b>Why the copy cannot simply happen afterwards.</b> The source is never told where the
/// target is putting things, so there is no destination to write into once the drop has landed.
/// Windows gives the target a list of paths and Explorer's copy engine reads them after GetData
/// returns; X11 gives it URIs the target reads after XdndDrop. A path still being written hands over
/// a truncated file.</para>
///
/// <para><b>What the platform does give is the timing.</b> The value behind a format is fetched
/// lazily, from IDataObject.GetData on Windows and from the X11 selection request, and both of those
/// run at the <b>drop</b> rather than when the drag starts. So a drag can carry a promise. It just
/// has to be one that can be kept in the moment, on the UI thread, because that is where both
/// backends call the getter (the X11 one blocks on its own async path to do it).</para>
///
/// <para>Hence the two bounds. <see cref="MaxBytes"/> is measured before a byte is fetched and
/// refuses outright rather than starting something that cannot finish in a gesture, and
/// <see cref="StageTimeout"/> caps the wait for everything that gets past it. The timeout is the
/// backstop for the wait as a whole: whatever goes wrong behind it, the getter gives up after ten
/// seconds with nothing rather than hanging the app on somebody else's drop.</para>
/// </summary>
internal static class DragOutStaging
{
    /// <summary>
    /// The most one drag will fetch. At the 112 MB/s a transfer was measured at this is a little
    /// over two seconds, which covers configuration, logs, source trees and ordinary folders while
    /// leaving a disk image on the host, where downloading it is a command with a progress bar and a
    /// Cancel button beside it.
    /// </summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>How long the getter will block the UI thread waiting for the bytes.</summary>
    public static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);

    private const string Prefix = "virtdeck-drag-";

    /// <summary>This process's staging root. Named by pid so <see cref="Sweep"/> can tell a crashed
    /// run's leftovers from a second copy of the app running right now.</summary>
    private static string Root => Path.Combine(Path.GetTempPath(), Prefix + Environment.ProcessId);

    private static int _next;

    /// <summary>A fresh directory for one drag. Its names all come from one listing, so they cannot
    /// collide inside it.</summary>
    public static string NewDirectory()
    {
        var dir = Path.Combine(Root, Interlocked.Increment(ref _next).ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Drops this process's whole staging root. Best effort: a file the desktop still has
    /// open is not worth an error at shutdown.</summary>
    public static void DeleteRoot() => Delete(Root);

    /// <summary>
    /// Deletes the staging roots of runs that are no longer here, so a crash or a kill does not
    /// leave the bytes behind. A pid that has been reused by something else keeps its directory,
    /// which is the harmless way to be wrong.
    /// </summary>
    public static void Sweep()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
            {
                var tail = Path.GetFileName(dir)[Prefix.Length..];
                if (!int.TryParse(tail, out var pid) || pid == Environment.ProcessId) continue;
                try { using var _ = System.Diagnostics.Process.GetProcessById(pid); }
                catch { Delete(dir); }
            }
        }
        catch { /* no temp directory to read is not something a file listing needs to report */ }
    }

    private static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

/// <summary>Why a drag out could not hand anything over. A value rather than an exception, because
/// the module has to say it.</summary>
internal enum StageRefusal
{
    None,

    /// <summary>More than <see cref="DragOutStaging.MaxBytes"/>, so nothing was fetched at all.</summary>
    TooLarge,

    /// <summary>Under the cap but still going when <see cref="DragOutStaging.StageTimeout"/> ran out.</summary>
    TooSlow,

    /// <summary>The host refused it, or the transfer broke.</summary>
    Failed,
}

/// <summary>
/// One drag's worth of staged bytes. Created when the drag starts, started when the drag leaves the
/// window (or when the getter asks, whichever comes first), and read once from the drop.
///
/// <para>Starting on the way out rather than on the way up is what keeps an internal move free: a
/// row dragged onto a folder row never leaves the window, so it never fetches anything. It is only
/// a head start, though, never the correctness: <see cref="Wait"/> starts it too, so a backend that
/// raises no leave event on the source simply pays the whole fetch inside the blocking wait.</para>
/// </summary>
internal sealed class DragOutStage : IDisposable
{
    /// <summary>Progress nothing is drawing from. Deliberately not <see cref="Progress{T}"/>, which
    /// captures whatever synchronisation context it was built on: the UI thread is blocked inside
    /// <see cref="Wait"/> while this runs, so posting a report to it would be a deadlock the timeout
    /// then has to clean up.</summary>
    private sealed class NoProgress : IProgress<TransferProgress>
    {
        public static readonly NoProgress Instance = new();
        public void Report(TransferProgress value) { }
    }

    private readonly RemoteFileService _files;
    private readonly RemoteTransferService _transfers;
    private readonly IStorageProvider? _storage;
    private readonly string _remoteDir;
    private readonly IReadOnlyList<(string Name, bool IsDir)> _items;
    private readonly bool _elevated;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private string? _dir;
    private Task<string[]>? _run;
    private bool _disposed;

    public DragOutStage(RemoteFileService files, RemoteTransferService transfers,
                        IStorageProvider? storage, string remoteDir,
                        IReadOnlyList<(string Name, bool IsDir)> items, bool elevated)
    {
        _files = files;
        _transfers = transfers;
        _storage = storage;
        _remoteDir = remoteDir;
        _items = items;
        _elevated = elevated;
    }

    /// <summary>True once the drop actually asked for a file, which is also what says the desktop
    /// may still be copying out of the staging directory.</summary>
    public bool Used { get; private set; }

    public StageRefusal Refusal { get; private set; }

    /// <summary>What <c>du -sb</c> said, for the module to name in the status slot.</summary>
    public long MeasuredBytes { get; private set; }

    /// <summary>The host's own words when <see cref="Refusal"/> is <see cref="StageRefusal.Failed"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>Begins the fetch if it has not begun. Idempotent, and safe from either thread.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null || _disposed) return;
            var ct = _cts.Token;
            _run = Task.Run(() => StageAsync(ct), ct);
        }
    }

    /// <summary>
    /// The staged entry at <paramref name="index"/>, or null when the promise could not be kept.
    /// Called from the platform's drag machinery at the drop, on the UI thread, so this is the one
    /// place in the module that blocks it, and <see cref="DragOutStaging.StageTimeout"/> is what
    /// says for how long.
    /// </summary>
    public IStorageItem? Wait(int index)
    {
        Used = true;

        // Latched, and that is not a nicety: the platform asks item by item, so without this a
        // selection of ten entries would spend the timeout ten times over on a fetch that has
        // already been given up on.
        if (Refusal != StageRefusal.None) return null;

        Start();

        Task<string[]>? run;
        lock (_gate) run = _run;
        if (run is null) return null;

        try
        {
            if (!run.Wait(DragOutStaging.StageTimeout))
            {
                _cts.Cancel();
                if (Refusal == StageRefusal.None) Refusal = StageRefusal.TooSlow;
                return null;
            }
        }
        catch (Exception ex)
        {
            if (Refusal == StageRefusal.None)
            {
                Refusal = StageRefusal.Failed;
                Error = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
            }
            return null;
        }

        var staged = run.Result;
        if (index >= staged.Length || staged[index] is not { Length: > 0 } path) return null;

        // Resolved here rather than on the fetch's thread on purpose: this runs on the UI thread,
        // and BclStorageProvider answers both of these straight out of Task.FromResult, with null
        // for a path that is not there, which is exactly the check a half-finished fetch needs.
        try
        {
            return _items[index].IsDir
                ? _storage?.TryGetFolderFromPathAsync(path).GetAwaiter().GetResult()
                : _storage?.TryGetFileFromPathAsync(path).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private async Task<string[]> StageAsync(CancellationToken ct)
    {
        var sources = _items.Select(i => RemoteFileService.CombinePath(_remoteDir, i.Name)).ToList();

        // du -sb is the only thing that knows what a tree weighs: the listing's %s is a directory's
        // own inode and not what is under it. It runs here rather than before the drag because the
        // format list is fixed the moment DoDragDropAsync is called, and there is no round trip to
        // be had before that without the button coming up in the middle of it. A -1 is "could not
        // tell", not "enormous", and is let through for the timeout to bound.
        MeasuredBytes = _files.Measure(sources, _elevated);
        ct.ThrowIfCancellationRequested();

        if (MeasuredBytes > DragOutStaging.MaxBytes)
        {
            Refusal = StageRefusal.TooLarge;
            return Array.Empty<string>();
        }

        var dir = DragOutStaging.NewDirectory();
        lock (_gate) _dir = dir;

        var items = _items
            .Select(i => new PasteItem
            {
                Source = RemoteFileService.CombinePath(_remoteDir, i.Name),
                Name = i.Name,
                SourceIsDir = i.IsDir,
                // Fresh throughout: the directory was made a line ago, so nothing can be in the way
                // and there is no conflict for anybody to settle.
                Resolution = PasteResolution.Fresh,
            })
            .ToList();

        var outcome = await _transfers
            .DownloadAsync(items, _remoteDir, dir, MeasuredBytes, _elevated, NoProgress.Instance, ct)
            .ConfigureAwait(false);

        if (outcome.Error is { } error)
        {
            // A permission cannot become the one-shot root retry the download command offers: there
            // is nowhere to ask while a drag is in the air. It is a refused drop and a line in the
            // status slot afterwards.
            Refusal = StageRefusal.Failed;
            Error = error;
            return Array.Empty<string>();
        }

        return _items.Select(i => Path.Combine(dir, i.Name)).ToArray();
    }

    /// <summary>
    /// Cancels whatever is still running and takes the directory back. The waiting half runs off the
    /// UI thread, because this is called from the end of a drag and from the start of the next one,
    /// and neither is a place to block on a tar that has not noticed the cancel yet.
    /// </summary>
    public void Dispose()
    {
        Task<string[]>? run;
        string? dir;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            run = _run;
            dir = _dir;
        }

        try { _cts.Cancel(); } catch { }

        _ = Task.Run(async () =>
        {
            // After the cancel has been observed, so tar is not still writing into what is about to
            // be removed. Whatever it throws on the way out is the cancel itself.
            if (run is not null) { try { await run.ConfigureAwait(false); } catch { } }
            if (dir is not null) { try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { } }
            try { _cts.Dispose(); } catch { }
        });
    }
}
