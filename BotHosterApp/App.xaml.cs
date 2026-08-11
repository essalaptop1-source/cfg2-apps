using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    /// <summary>
    /// Loads the app logo (Assets/icon.png, copied next to the exe, or the
    /// shared Config icon folder in the source tree). Returns null when none
    /// is found so the UI can fall back to a glyph.
    /// </summary>
    public static ImageSource? TryLoadLogo()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png"),
            Path.Combine(AppContext.BaseDirectory, "icon.png"),
            Path.Combine("Assets", "icon.png"),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(Path.GetFullPath(path));
                bitmap.DecodePixelWidth = 64;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
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
