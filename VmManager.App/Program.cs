using System.IO;

namespace VmManager
{
    internal static class Program
    {
        /// <summary>Crash log path: %LOCALAPPDATA%\VmManager\crash.log</summary>
        public static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VmManager", "crash.log");

        [STAThread]
        static void Main()
        {
            // Capture managed crashes (UI-thread and background-thread) to a file so failures are
            // diagnosable without a debugger. NOTE: native access violations inside the USB DLLs are
            // corrupted-state exceptions the CLR cannot catch — those won't appear here, which itself
            // is a useful signal (check Windows Event Viewer for the faulting module in that case).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => LogCrash("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("background thread", e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new Forms.LoginForm());
        }

        private static void LogCrash(string where, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                File.AppendAllText(CrashLogPath,
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} unhandled exception on {where} ===={Environment.NewLine}" +
                    (ex?.ToString() ?? "(unknown)") + Environment.NewLine + Environment.NewLine);
            }
            catch { /* last-resort logger must never throw */ }

            try
            {
                MessageBox.Show(
                    $"SpiceVmManager hit an unexpected error and may need to close.{Environment.NewLine}{Environment.NewLine}" +
                    $"{ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}{Environment.NewLine}" +
                    $"Details were written to:{Environment.NewLine}{CrashLogPath}",
                    "SpiceVmManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* ignore */ }
        }
    }
}
