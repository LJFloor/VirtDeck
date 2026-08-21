using System.Formats.Tar;
using System.IO.Pipelines;
using System.Text;

namespace VirtDeck.Services
{
    /// <summary>How far along a transfer is. <see cref="Total"/> is -1 when nobody could say.</summary>
    public sealed record TransferProgress(long Bytes, long Total, string Current);

    /// <summary>
    /// What one transfer did. A refusal is a value here for the same reason a listing's is: the
    /// explorer has to draw it, and offer root when it was a permission that stopped it.
    /// </summary>
    public sealed class TransferOutcome
    {
        public int Items { get; init; }
        public int Skipped { get; init; }

        /// <summary>
        /// The top-level names the transfer landed under, which is a free "name (copy)" wherever the
        /// user chose to keep both. The caller selects these afterwards, the same way a paste's
        /// <see cref="PasteOutcome.Landed"/> is used.
        /// </summary>
        public List<string> Landed { get; init; } = new();

        /// <summary>Null when it worked.</summary>
        public string? Error { get; init; }

        /// <summary>True when the refusal was a permission, so root is worth offering.</summary>
        public bool Denied { get; init; }
    }

    /// <summary>
    /// Bytes crossing the client/host boundary, in both directions. A sibling of
    /// <see cref="RemoteFileService"/> rather than part of it: that one is metadata and host-to-host
    /// operations, this one is the only thing in the app that moves a user's files between this PC
    /// and the host.
    ///
    /// <para><b>One mechanism, tar over exec.</b> It is the only option that covers files and whole
    /// directory trees, elevated and not, in one shape: recursion is tar's, and <c>sudo</c> is just a
    /// prefix. SFTP would be faster on a single large file but runs as the login user only, and this
    /// module's one-shot "retry as root" is a rule that applies to listing, paste and rename already.
    /// GNU tar is assumed, as GNU <c>find -printf</c> already is in <see cref="RemoteFileService"/>.
    /// </para>
    ///
    /// <para><b>Symlinks are dereferenced in both directions</b> (<c>tar -h</c> going out; plain
    /// recursion coming in, since <see cref="Directory.Exists"/> and <see cref="FileStream"/> follow
    /// links by themselves). Somebody downloading a folder wants the files, it behaves identically on
    /// either client OS, and it never asks Windows to create a symlink, which needs a privilege there.
    /// A cyclic symlink inside a tree makes tar recurse; that is GNU tar's own caveat under
    /// <c>-h</c> and is not guarded here.</para>
    /// </summary>
    public sealed class RemoteTransferService
    {
        private readonly SshConnectionManager _ssh;

        /// <summary>Progress is reported no more than this often; a 64 KiB chunk is far too small a
        /// step to repaint on, and every report costs a hop to the UI thread.</summary>
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(120);

        public RemoteTransferService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        // ---- Download (host -> this PC) ---------------------------------

        /// <summary>
        /// Streams the named entries out of <paramref name="remoteDir"/> and writes them under
        /// <paramref name="localDir"/>. <paramref name="total"/> comes from
        /// <see cref="RemoteFileService.Measure"/>; -1 leaves the caller's bar indeterminate.
        /// </summary>
        public async Task<TransferOutcome> DownloadAsync(
            IReadOnlyList<PasteItem> items, string remoteDir, string localDir, long total,
            bool elevated, IProgress<TransferProgress> progress, CancellationToken ct)
        {
            var wanted = items.Where(i => i.Resolution != PasteResolution.Skip).ToList();
            var skipped = items.Count - wanted.Count;
            if (wanted.Count == 0) return new TransferOutcome { Skipped = skipped };

            // The top-level name each entry lands under. Only KeepBoth renames, and unlike an upload
            // the free name is resolved here, because this side is the one that knows what is there.
            var rename = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in wanted)
            {
                rename[item.Name] = item.Resolution == PasteResolution.KeepBoth
                    ? FreeLocalName(localDir, item.Name, item.SourceIsDir)
                    : item.Name;
            }

            var script = DownloadScript(remoteDir, wanted.Select(i => i.Name));
            var command = elevated ? RemoteFileService.SudoWrap(script) : RemoteFileService.Wrap(script);

            var reporter = new Reporter(progress, total);
            var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));

            // SSH writes into the pipe while the tar reader drains it, the same shape ExportVmDialog
            // uses in the other direction.
            Exception? pumped = null;
            var pump = Task.Run(async () =>
            {
                try
                {
                    await _ssh.RunPipeOutAsync(command, elevated, pipe.Writer.AsStream(),
                                               reporter.Add, ct);
                }
                catch (Exception ex)
                {
                    pumped = ex;
                    throw;
                }
                finally
                {
                    // Completing with the exception is what makes the reader stop rather than block
                    // on a stream nobody will ever write to again.
                    await pipe.Writer.CompleteAsync(pumped);
                }
            }, ct);

            try
            {
                await ExtractAsync(pipe.Reader.AsStream(), localDir, rename, reporter, ct);
            }
            catch (OperationCanceledException)
            {
                RemoveLocal(localDir, CreatedNames(wanted, i => rename[i.Name]));
                throw;
            }
            catch (Exception ex)
            {
                await pipe.Reader.CompleteAsync();
                try { await pump; } catch { /* pumped holds it, and it is the better message */ }
                return Failed(pumped ?? ex);
            }

            // Extraction finishing is NOT proof the transfer worked, which is the whole reason the
            // pump is awaited separately. A tar that cannot open a member still writes the
            // end-of-archive blocks and exits non-zero, so stdout is a perfectly valid empty archive
            // and this side sees nothing wrong; swallowing that reported a refused download as a
            // success with no files in it.
            try { await pump; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Failed(ex); }

            return new TransferOutcome
            {
                Items = wanted.Count,
                Skipped = skipped,
                Landed = wanted.Select(LandingName).ToList(),
            };
        }

        /// <summary>
        /// <c>-C</c> plus bare names rather than absolute paths, so the entries come out rooted at
        /// what the user picked instead of carrying the whole path down from <c>/</c>.
        /// </summary>
        private static string DownloadScript(string remoteDir, IEnumerable<string> names)
        {
            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteDir));
            return
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                RemoteFileService.ArrayFrom("a", names) +
                "exec tar -C \"$d\" -chf - -- \"${a[@]}\"\n";
        }

        private static async Task ExtractAsync(Stream stream, string localDir,
                                               IReadOnlyDictionary<string, string> rename,
                                               Reporter reporter, CancellationToken ct)
        {
            Directory.CreateDirectory(localDir);
            var root = Path.GetFullPath(localDir);

            await using var reader = new TarReader(stream, leaveOpen: false);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                ct.ThrowIfCancellationRequested();

                if (Remap(entry.Name, rename) is not { } relative) continue;

                // The entry names come off the host, so nothing is created until the path is known
                // to stay inside the directory the user chose.
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !string.Equals(full, root, StringComparison.Ordinal))
                    continue;

                reporter.Now(Path.GetFileName(relative));

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(full);
                        break;

                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                    case TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        await using (var file = new FileStream(full, FileMode.Create, FileAccess.Write,
                                                               FileShare.None, 131072, useAsync: true))
                        {
                            if (entry.DataStream is { } data) await data.CopyToAsync(file, ct);
                        }
                        break;

                    // Everything else (devices, fifos, and the symlinks tar -h should have resolved)
                    // has no meaning on a client filesystem and is passed over rather than guessed at.
                }
            }
        }

        /// <summary>
        /// Swaps an entry's first path segment for the local name the user settled on, and drops an
        /// entry whose first segment is not one of the names asked for.
        /// </summary>
        private static string? Remap(string entryName, IReadOnlyDictionary<string, string> rename)
        {
            var name = entryName.Replace('\\', '/').TrimStart('/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            var trimmed = name.TrimEnd('/');
            if (trimmed.Length == 0 || trimmed == "." || trimmed == "..") return null;

            var slash = trimmed.IndexOf('/');
            var head = slash < 0 ? trimmed : trimmed[..slash];
            if (!rename.TryGetValue(head, out var mapped)) return null;
            // A ".." anywhere else is refused rather than normalised: no legitimate entry has one.
            var tail = slash < 0 ? "" : trimmed[(slash + 1)..];
            if (tail.Split('/').Any(p => p == "..")) return null;

            return slash < 0 ? mapped : mapped + "/" + tail;
        }

        /// <summary>
        /// The first free "name (copy)" on this PC. Deliberately the same rule the host-side loops
        /// use, down to only splitting an extension on <c>?*.*</c> and never for a directory, so a
        /// kept-both copy is named the same whichever way it travelled.
        /// </summary>
        private static string FreeLocalName(string dir, string name, bool isDir)
        {
            var baseName = name;
            var ext = string.Empty;
            if (!isDir)
            {
                var dot = name.LastIndexOf('.');
                if (dot > 0) { baseName = name[..dot]; ext = name[dot..]; }
            }

            for (var n = 1; ; n++)
            {
                var candidate = n == 1 ? $"{baseName} (copy){ext}" : $"{baseName} (copy {n}){ext}";
                var full = Path.Combine(dir, candidate);
                if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
            }
        }

        // ---- Upload (this PC -> host) -----------------------------------

        /// <summary>
        /// Writes the given local files and directories into <paramref name="remoteDir"/>.
        /// <paramref name="total"/> is the summed local length, which the caller already walked.
        /// </summary>
        public async Task<TransferOutcome> UploadAsync(
            IReadOnlyList<PasteItem> items, string remoteDir, long total,
            bool elevated, IProgress<TransferProgress> progress, CancellationToken ct)
        {
            var wanted = items.Where(i => i.Resolution != PasteResolution.Skip).ToList();
            var skipped = items.Count - wanted.Count;
            if (wanted.Count == 0) return new TransferOutcome { Skipped = skipped };

            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteDir));
            var script =
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                // --no-same-owner because the archive is built here: as root, tar would otherwise
                // restore whatever uid a client-built entry happened to carry. This way the result
                // belongs to whoever ran tar, the login user or root.
                "exec tar -C \"$d\" -xf - --no-same-owner\n";

            var reporter = new Reporter(progress, total);

            try
            {
                await _ssh.RunPipeInAsync(script, elevated,
                    (stdin, token) => WriteArchiveAsync(stdin, wanted, reporter, token), ct);
            }
            catch (OperationCanceledException)
            {
                // tar was cut off mid-entry, so whatever it was writing is a partial file. Undo what
                // this transfer put there before letting the cancellation through.
                RemoveRemote(remoteDir, CreatedNames(wanted, LandingName), elevated);
                throw;
            }
            catch (Exception ex) { return Failed(ex); }

            return new TransferOutcome
            {
                Items = wanted.Count,
                Skipped = skipped,
                Landed = wanted.Select(LandingName).ToList(),
            };
        }

        private static async Task WriteArchiveAsync(Stream stdin, IReadOnlyList<PasteItem> items,
                                                    Reporter reporter, CancellationToken ct)
        {
            await using var tar = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                var name = item.Resolution == PasteResolution.KeepBoth && item.FreeName.Length > 0
                    ? item.FreeName
                    : item.Name;

                if (item.SourceIsDir) await AddDirectoryAsync(tar, item.Source, name, reporter, ct);
                else await AddFileAsync(tar, item.Source, name, reporter, ct);
            }
        }

        private static async Task AddDirectoryAsync(TarWriter tar, string path, string name,
                                                    Reporter reporter, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var dir = new PaxTarEntry(TarEntryType.Directory, name + "/") { Mode = ModeOf(path, true) };
            await tar.WriteEntryAsync(dir, ct);

            // AttributesToSkip = 0 is the whole point: the default skips Hidden and System, which on
            // Linux would silently leave every dotfile in the tree out of the archive.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
            };

            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", options).OrderBy(p => p, StringComparer.Ordinal))
            {
                var childName = name + "/" + Path.GetFileName(child);
                // Directory.Exists follows a link, which is the dereferencing tar -h does going out.
                if (Directory.Exists(child)) await AddDirectoryAsync(tar, child, childName, reporter, ct);
                else await AddFileAsync(tar, child, childName, reporter, ct);
            }
        }

        private static async Task AddFileAsync(TarWriter tar, string path, string name,
                                               Reporter reporter, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            reporter.Now(Path.GetFileName(name));

            FileStream source;
            try
            {
                source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                        131072, useAsync: true);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A file that vanished or cannot be opened is left out rather than failing the whole
                // archive; tar's own behaviour, and the alternative is losing a large upload to one
                // unreadable file.
                return;
            }

            await using (source)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode = ModeOf(path, false),
                    DataStream = new CountingReadStream(source, reporter.Add),
                };
                await tar.WriteEntryAsync(entry, ct);
            }
        }

        /// <summary>
        /// The local mode where the platform has one. Windows has no POSIX mode at all, so the
        /// conventional pair is used rather than something derived from an ACL.
        /// </summary>
        private static UnixFileMode ModeOf(string path, bool isDir)
        {
            try { return File.GetUnixFileMode(path); }
            catch
            {
                return isDir
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            }
        }

        // ---- Undoing a cancelled transfer -------------------------------
        //
        // Cancelling mid-entry leaves whatever tar (or the extractor) was part way through, and a
        // half-written file presented as a real one is worse than no file at all. So a cancelled
        // transfer takes back what it put there, the way ExportVmDialog deletes its ".part".
        //
        // **Only what this transfer created.** An entry the destination already had and the user
        // chose to Overwrite is left exactly where it is: this end never kept a copy of it, so there
        // is nothing to restore, and removing it would destroy something that was there before the
        // transfer started. That is the same rule the rest of the module lives by, and it is why the
        // rm below can only ever reach names the pre-flight said were free.
        //
        // Cancelling is the only trigger. A transfer that *failed* keeps what did land, because tar
        // extracts the members it can and reports the ones it cannot; throwing those away would be
        // discarding good work over an unrelated entry, which is not what a failure means.

        /// <summary>Where one item ends up, which is its free name only when it is keeping both.</summary>
        private static string LandingName(PasteItem item) =>
            item.Resolution == PasteResolution.KeepBoth && item.FreeName.Length > 0
                ? item.FreeName
                : item.Name;

        /// <summary>
        /// The top-level names this transfer brought into being: everything that landed on a free
        /// name, which is every item that was not overwriting something already there.
        /// </summary>
        private static List<string> CreatedNames(IEnumerable<PasteItem> items, Func<PasteItem, string> landedAs) =>
            items.Where(i => i.Resolution == PasteResolution.KeepBoth || !i.TargetExists)
                 .Select(landedAs)
                 .Where(n => n.Length > 0 && n != "." && n != ".." && !n.Contains('/'))
                 .ToList();

        /// <summary>
        /// Removes named children of one host directory. Best-effort and silent: this runs while a
        /// cancellation is already unwinding, and a tidy-up that threw would replace the reason the
        /// transfer stopped with a reason the cleanup did.
        /// </summary>
        private void RemoveRemote(string dir, IReadOnlyList<string> names, bool elevated)
        {
            if (names.Count == 0) return;

            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(dir));
            var body =
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                RemoteFileService.ArrayFrom("a", names) +
                // Each name came from the pre-flight as one this directory did not hold, so this can
                // only take back what the cancelled transfer itself wrote.
                "for n in \"${a[@]}\"; do rm -rf -- \"$d/$n\"; done\n" +
                "exit 0\n";

            var script = RemoteFileService.Wrap(body);
            try
            {
                if (elevated) _ssh.RunSudoCommand(script);
                else _ssh.RunCommand(script);
            }
            catch { /* the connection may already be going away; nothing here is worth reporting */ }
        }

        /// <summary>The client-side half of <see cref="RemoveRemote"/>, and just as silent.</summary>
        private static void RemoveLocal(string dir, IReadOnlyList<string> names)
        {
            foreach (var name in names)
            {
                var full = Path.Combine(dir, name);
                try
                {
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                    else if (File.Exists(full)) File.Delete(full);
                }
                catch { /* a file still open, or already gone; neither is worth reporting */ }
            }
        }

        // ---- Shared -----------------------------------------------------

        /// <summary>
        /// A transfer is one <c>tar</c>, so there is no per-item failure to report the way a paste
        /// has: it worked or it did not. The wording is matched against the C locale, which the
        /// scripts export, and is what decides whether root is worth offering.
        /// </summary>
        private static TransferOutcome Failed(Exception ex)
        {
            var message = ex.Message.Trim();
            if (message.Length == 0) message = "The host refused it without saying why.";
            return new TransferOutcome
            {
                Error = message,
                Denied = message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("Cannot open", StringComparison.OrdinalIgnoreCase),
            };
        }

        /// <summary>
        /// Throttles progress to <see cref="ReportEvery"/>. Bytes arrive on the transfer's own thread
        /// and the current name from the tar loop, so both are guarded rather than assumed to be one
        /// thread.
        /// </summary>
        private sealed class Reporter(IProgress<TransferProgress> progress, long total)
        {
            private readonly Lock _lock = new();
            private long _bytes;
            private string _current = string.Empty;
            private long _lastTick;

            public void Add(int n)
            {
                lock (_lock)
                {
                    _bytes += n;
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsed = (now - _lastTick) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (elapsed < ReportEvery.TotalSeconds) return;
                    _lastTick = now;
                    progress.Report(new TransferProgress(_bytes, total, _current));
                }
            }

            public void Now(string name)
            {
                lock (_lock)
                {
                    _current = name;
                    progress.Report(new TransferProgress(_bytes, total, name));
                }
            }
        }

        /// <summary>
        /// Counts what the tar writer pulls out of a file, so upload progress moves with the bytes
        /// read rather than jumping once per file.
        ///
        /// <para><c>CanSeek</c> is true and <c>Seek</c> still throws, which looks wrong and is not:
        /// <see cref="TarWriter"/> has to write an entry's size into its header before the data
        /// flows, and it reads <c>Length</c> behind a <c>CanSeek</c> guard. Same trick, same reason as
        /// <c>ExportVmDialog.KnownLengthStream</c>; only sequential reads are ever made.</para>
        /// </summary>
        private sealed class CountingReadStream(Stream inner, Action<int> onRead) : Stream
        {
            private long _pos;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = inner.Read(buffer, offset, count);
                if (n > 0) { _pos += n; onRead(n); }
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var n = await inner.ReadAsync(buffer, ct);
                if (n > 0) { _pos += n; onRead(n); }
                return n;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                var n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
                if (n > 0) { _pos += n; onRead(n); }
                return n;
            }

            // The FileStream is owned by AddFileAsync's await using, not by this wrapper.
            protected override void Dispose(bool disposing) => base.Dispose(disposing);
        }
    }
}
