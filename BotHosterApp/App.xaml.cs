using System.IO;
using System.Windows;

namespace BotHosterApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);

        try
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Closed += (_, _) => Shutdown();
            main.Show();
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow", ex);
            MessageBox.Show(
                "The main window could not be created:\n\n" + ex.Message,
                "CFG2 Bot Hoster - startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kicia", "bot_hoster_crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log,
                $"[{DateTime.Now:HH:mm:ss.fff}] BotHoster:{source}\n{ex}\n\n");
        }
        catch
        {
        }
    }
}
