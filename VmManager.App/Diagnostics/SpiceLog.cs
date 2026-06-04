using System.Collections.Concurrent;
using System.Diagnostics;

namespace VmManager.Diagnostics
{
    /// <summary>
    /// Central diagnostic log: an in-memory ring buffer + a live <see cref="LineLogged"/>
    /// event, with file writes done on a background thread so logging never blocks the
    /// caller (the SPICE read threads). Per-frame tracing is gated by <see cref="Verbose"/>.
    /// </summary>
    public static class SpiceLog
    {
        private const int MaxLines = 5000;
        private static readonly object Gate = new();
        private static readonly LinkedList<string> Buffer = new();
        private static readonly BlockingCollection<string> WriteQueue = new(new ConcurrentQueue<string>());

        public static readonly string FilePath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpiceVmManager.log");

        /// <summary>Raised for every logged line (on the calling thread).</summary>
        public static event Action<string>? LineLogged;

        private static bool _verbose;
        /// <summary>When true, the protocol channels log every received message.</summary>
        public static bool Verbose
        {
            get => _verbose;
            set
            {
                if (_verbose == value) return;
                _verbose = value;
                VerboseChanged?.Invoke(value);
            }
        }
        public static event Action<bool>? VerboseChanged;

        static SpiceLog()
        {
            var writer = new Thread(WriterLoop) { IsBackground = true, Name = "spicelog-writer" };
            writer.Start();
        }

        private static void WriterLoop()
        {
            foreach (var line in WriteQueue.GetConsumingEnumerable())
            {
                try { System.IO.File.AppendAllText(FilePath, line + Environment.NewLine); }
                catch { /* logging must never throw */ }
            }
        }

        public static void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            Debug.WriteLine(line);

            lock (Gate)
            {
                Buffer.AddLast(line);
                while (Buffer.Count > MaxLines) Buffer.RemoveFirst();
            }
            try { WriteQueue.Add(line); } catch { /* queue completed */ }

            LineLogged?.Invoke(line);
        }

        /// <summary>A snapshot of the current buffered lines (for a newly opened window).</summary>
        public static string[] Snapshot()
        {
            lock (Gate) return Buffer.ToArray();
        }

        public static void Clear()
        {
            lock (Gate) Buffer.Clear();
        }
    }
}
