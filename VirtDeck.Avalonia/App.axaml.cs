using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Hosts;

namespace VirtDeck.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The connect window hands off to a shell, so shutting down with the last window would
            // close the app in the gap where it has closed and the shell has not opened. Which
            // window keeps the process alive is ShellRegistry's, and it is not one window: a host
            // switch runs two shells for an instant, and a shell can open a connect window of its
            // own to add a host.
            // Before any window: every fixed-pitch surface reads this, and resolving it here is
            // also what guarantees the font manager is up when the one-time lookup happens.
            Resources[MonoFont.ResourceKey] = MonoFont.Family;

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new HostManagerWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
