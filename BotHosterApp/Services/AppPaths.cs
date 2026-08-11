using System.IO;

namespace BotHosterApp.Services;

/// <summary>
/// Resolves files next to the real exe. Single-file publishes extract to a
/// temp dir, so AppContext.BaseDirectory is wrong there - the exe path is
/// the source of truth (that is where keys.txt lives, for example).
/// </summary>
public static class AppPaths
{
    private static readonly string ExeDir = GetExeDir();

    private static string GetExeDir()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(Path.GetFullPath(p))!;
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    /// <summary>Full path of a file that sits next to the exe.</summary>
    public static string Combine(string fileName) => Path.Combine(ExeDir, fileName);

    /// <summary>App data dir for local state (settings, stored bots).</summary>
    public static string LocalDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kicia");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
