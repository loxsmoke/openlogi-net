using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using OpenLogi.App.Localization;
using OpenLogi.Core.Localization;
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
            AppSettings? settings = null;
            try { settings = Config.LoadOrDefault().AppSettings; }
            catch { /* keep logging, fall back to the OS language */ }
            if (settings is not null) DiagnosticLog.Suppressed = settings.SuppressLogging;

            // Resolve the UI language before the first window is constructed: a
            // window built under the wrong culture would keep it until recreated.
            var culture = LanguageCatalog.Resolve(settings?.Language);
            Loc.Current.SetCulture(culture);
            DiagnosticLog.Info("env",
                $"language {settings?.Language ?? "system"} (OS {Loc.SystemUiCulture.Name}) → {culture.Name}");

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

            var startFromLogin = Environment.GetCommandLineArgs()
                .Skip(1)
                .Any(arg => string.Equals(arg, Autostart.StartupArgument, StringComparison.OrdinalIgnoreCase));
            Autostart.RefreshCurrentEntry();
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
                StartHiddenToTray = startFromLogin,
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
