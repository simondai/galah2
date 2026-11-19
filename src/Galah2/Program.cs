using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Galah2;

internal static class Program
{
    // Avalonia needs an STA thread and no synthetic sync context during boot.
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}
