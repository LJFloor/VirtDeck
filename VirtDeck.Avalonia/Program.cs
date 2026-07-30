using System.IO;
using Avalonia;
using VirtDeck.Services;

namespace VirtDeck.Avalonia;

internal static class Program
{
    /// <summary>Crash log path — %LOCALAPPDATA%\VirtDeck\crash.log, ~/.local/share/VirtDeck/crash.log.</summary>
    public static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtDeck", "crash.log");

    // Avalonia needs an STA thread on Windows and must be initialised before any UI type is touched.
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("background thread", e.ExceptionObject as Exception);

        // Bring an existing Windows registry configuration forward the first time the JSON-backed
        // settings are used. No-op on Linux, which never had registry settings.
        if (OperatingSystem.IsWindows())
            AppSettings.LegacyImporter = LegacyRegistryImport.Apply;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("startup", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Referenced by name by the Avalonia designer tooling — keep the signature.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

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
    }
}
