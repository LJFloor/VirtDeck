using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VirtDeck.Avalonia.Views;

namespace VirtDeck.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The login window hands off to a shell, so shutting down with the last window would
            // close the app in the gap where login has hidden itself and the shell has not opened.
            // Which window keeps the process alive is ShellRegistry's, and it is not one window:
            // a host switch runs two shells for an instant, and a shell can open a login window of
            // its own to add a host.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new LoginWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
