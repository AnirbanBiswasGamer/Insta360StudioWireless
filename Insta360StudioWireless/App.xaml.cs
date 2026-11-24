using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;

namespace Insta360StudioWireless;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogFatal(args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogFatal(args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    private static void LogFatal(Exception? ex)
    {
        if (ex == null) return;
        string logPath = @"D:\Insta360\startup_error.log";
        File.WriteAllText(logPath, ex.ToString());
        MessageBox.Show(ex.ToString(), "Insta360 Studio Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
