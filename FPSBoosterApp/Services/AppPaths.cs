using System.IO;

namespace FPSBoosterApp.Services;

/// <summary>
/// Single-file publishes extract the app to a temp dir, so
/// AppContext.BaseDirectory points at the extraction folder. Everything that
/// must live NEXT to the exe (ffmpeg.exe, keys.txt) resolves through here.
/// </summary>
public static class AppPaths
{
    /// <summary>Directory containing the actual .exe on disk.</summary>
    public static string ExeDir
    {
        get
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p))
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch { }
            return AppContext.BaseDirectory;
        }
    }

    public static string Combine(string fileName) => Path.Combine(ExeDir, fileName);
}
