using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2;

public partial class App : Application
{
    private AppRuntime? _runtime;
    private SingleInstance? _instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

        // A second copy forwards the toggle signal and leaves; only the primary owns the tray
        // icon and the hotkey, so double-launching can never produce two of either.
        _instance = new SingleInstance();
        if (!_instance.IsPrimary)
        {
            SingleInstance.SignalPrimary();
            _instance.Dispose();
            desktop.Shutdown();
            return;
        }

        var paths = AppDataPaths.ForExecutable();
        _runtime = new AppRuntime(paths);

        _instance.ToggleRequested += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _runtime?.Toggle());

        // The tray icon and the hotkey sink hold OS handles; release them with the lifetime.
        desktop.Exit += (_, _) => _runtime?.Dispose();

        base.OnFrameworkInitializationCompleted();
    }
}
