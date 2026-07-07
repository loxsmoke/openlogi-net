using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;
using OpenLogi.App.Views;
using OpenLogi.Core;
using OpenLogi.Core.Logging;
using OpenLogi.Core.Config;

namespace OpenLogi.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Apply log suppression before anything (the sweep starts with the
            // view model). Missing key / unreadable config ⇒ logging stays on.
            try { DiagnosticLog.Suppressed = Config.LoadOrDefault().AppSettings.SuppressLogging; }
            catch { /* keep logging */ }

            // Env header for the diagnostic log, off the UI thread (the Logitech
            // check enumerates processes). May interleave with sweep lines — fine.
            Task.Run(() =>
            {
                DiagnosticLog.Info("env", SystemInfo.WindowsVersion());
                var logi = SystemInfo.DetectLogitechSoftware();
                DiagnosticLog.Info("env",
                    $"Logitech software: {(logi.Count > 0 ? string.Join(", ", logi) : "none detected")}");
                DiagnosticLog.Info("env", $"config {Paths.ConfigPath()}");
            });

            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            // Tear down the OS mouse hook cleanly when the app quits, then seal
            // the log so a missing end marker always means a crash.
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                DiagnosticLog.ShutdownAsync("clean exit").AsTask().Wait(2000);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}