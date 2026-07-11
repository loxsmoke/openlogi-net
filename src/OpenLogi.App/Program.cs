using Avalonia;
using System;

namespace OpenLogi.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // DevTools (F12) intentionally not attached: it isn't needed at runtime and
            // otherwise pops a "developer tools not installed" dialog on F12.
            .WithInterFont()
            .LogToTrace();
}
