using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace FPSBoosterApp.Services;

public enum TweakId
{
    PowerPlan,
    VisualEffects,
    GameMode,
    GameDvr,
    Nagle,
    GpuPreference,
}

public class BoostTweak
{
    public TweakId Id { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public bool NeedsAdmin { get; init; }
    public bool Recommended { get; init; } = true;
}

public static class BoostService
{
    public const string HighPerfScheme = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedScheme = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>Full path to the game exe that should get the high-performance GPU.</summary>
    public static string GameExePath { get; set; } = "";

    public static readonly List<BoostTweak> Tweaks = new()
    {
        new BoostTweak
        {
            Id = TweakId.PowerPlan,
            Title = "High-performance power plan",
            Description = "Switches Windows to the High Performance power plan so the CPU and GPU are never held back.",
            NeedsAdmin = true,
        },
        new BoostTweak
        {
            Id = TweakId.VisualEffects,
            Title = "Best-performance visual effects",
            Description = "Turns off window animations, shadows and transparency for lower system overhead.",
            NeedsAdmin = false,
        },
        new BoostTweak
        {
            Id = TweakId.GameMode,
            Title = "Windows Game Mode",
            Description = "Tells Windows to prioritize gaming when the game is running.",
            NeedsAdmin = false,
        },
        new BoostTweak
        {
            Id = TweakId.GameDvr,
            Title = "Disable Game DVR / background capture",
            Description = "Stops Xbox Game Bar from secretly recording your gameplay, saving CPU and GPU.",
            NeedsAdmin = false,
        },
        new BoostTweak
        {
            Id = TweakId.Nagle,
            Title = "Disable Nagle (lower network latency)",
            Description = "Sends small packets immediately, which can reduce lag in online games.",
            NeedsAdmin = true,
        },
        new BoostTweak
        {
            Id = TweakId.GpuPreference,
            Title = "High-performance GPU for a game",
            Description = "Forces the dedicated GPU for one game exe (laptops with dual GPUs). Enter the path below.",
            NeedsAdmin = false,
            Recommended = false,
        },
    };

    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ State

    public static bool IsApplied(TweakId id)
    {
        try
        {
            return id switch
            {
                TweakId.PowerPlan => ActiveScheme().Contains(HighPerfScheme, StringComparison.OrdinalIgnoreCase),
                TweakId.VisualEffects => ReadDword(Registry.CurrentUser,
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting") == 2,
                TweakId.GameMode =>
                    ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode") == 1 &&
                    ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == 1,
                TweakId.GameDvr =>
                    ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 0 &&
                    ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") == 0,
                TweakId.Nagle => AnyInterface(f => f.GetValue("TcpAckFrequency") is int ack && ack == 1),
                TweakId.GpuPreference => !string.IsNullOrWhiteSpace(GameExePath) &&
                    ReadString(Registry.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", GameExePath) != null,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ Apply / Restore

    public static (bool Ok, string Message) Apply(TweakId id) => id switch
    {
        TweakId.PowerPlan => SetPowerPlan(HighPerfScheme),
        TweakId.VisualEffects => SetVisualEffects(true),
        TweakId.GameMode => SetGameMode(true),
        TweakId.GameDvr => SetGameDvr(false),
        TweakId.Nagle => SetNagle(true),
        TweakId.GpuPreference => SetGpuPreference(true),
        _ => (false, "Unknown tweak"),
    };

    public static (bool Ok, string Message) Restore(TweakId id) => id switch
    {
        TweakId.PowerPlan => SetPowerPlan(BalancedScheme),
        TweakId.VisualEffects => SetVisualEffects(false),
        TweakId.GameMode => SetGameMode(false),
        TweakId.GameDvr => SetGameDvr(true),
        TweakId.Nagle => SetNagle(false),
        TweakId.GpuPreference => SetGpuPreference(false),
        _ => (false, "Unknown tweak"),
    };

    // ================================================================ Implementations

    private static (bool, string) SetPowerPlan(string schemeGuid)
    {
        var (exit, err) = RunPowerCfg($"-setactive {schemeGuid}");
        return exit == 0
            ? (true, schemeGuid == HighPerfScheme ? "High Performance power plan active" : "Balanced power plan active")
            : (false, string.IsNullOrWhiteSpace(err) ? "powercfg failed (needs administrator?)" : err.Trim());
    }

    private static (bool, string) SetVisualEffects(bool on)
    {
        // 0 = let Windows choose, 1 = best appearance, 2 = best performance
        WriteDword(Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
            "VisualFXSetting", on ? 2 : 0);
        return (true, on ? "Visual effects set to best performance" : "Visual effects restored");
    }

    private static (bool, string) SetGameMode(bool on)
    {
        var v = on ? 1 : 0;
        WriteDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", v);
        WriteDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", v);
        return (true, on ? "Game Mode enabled" : "Game Mode disabled");
    }

    private static (bool, string) SetGameDvr(bool enabled)
    {
        var v = enabled ? 1 : 0;
        WriteDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", v);
        WriteDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", v);
        // Machine-wide policy when elevated (best effort - may fail without admin).
        try
        {
            WriteDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", v);
        }
        catch
        {
        }
        return (true, enabled ? "Game DVR / captures restored" : "Game DVR / background capture disabled");
    }

    private static (bool, string) SetNagle(bool disable)
    {
        var changed = 0;
        using (var interfaces = Registry.LocalMachine.OpenSubKey(
                   @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", writable: true))
        {
            if (interfaces == null) return (false, "Network interfaces registry not found");
            foreach (var name in interfaces.GetSubKeyNames())
            {
                using var iface = interfaces.OpenSubKey(name, writable: true);
                if (iface == null) continue;
                if (disable)
                {
                    iface.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    iface.SetValue("TcpNoDelay", 1, RegistryValueKind.DWord);
                    changed++;
                }
                else
                {
                    if (iface.GetValue("TcpAckFrequency") != null) { iface.DeleteValue("TcpAckFrequency"); changed++; }
                    if (iface.GetValue("TcpNoDelay") != null) { iface.DeleteValue("TcpNoDelay"); changed++; }
                }
            }
        }
        return (true, changed > 0
            ? (disable ? "Nagle disabled on network adapters" : "Nagle restored")
            : (disable ? "No change needed" : "Nothing to restore"));
    }

    private static (bool, string) SetGpuPreference(bool enable)
    {
        if (string.IsNullOrWhiteSpace(GameExePath))
            return (false, "Enter the game exe path first");
        using var prefs = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        if (enable)
            prefs.SetValue(GameExePath, "GpuPreference=2;");
        else
            prefs.DeleteValue(GameExePath, throwOnMissingValue: false);
        return (true, enable ? "GPU preference set for the game" : "GPU preference removed");
    }

    // ================================================================ Helpers

    private static string ActiveScheme()
    {
        var (_, outText) = RunPowerCfg("-getactivescheme");
        return outText;
    }

    private static (int ExitCode, string Output) RunPowerCfg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var outp = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, outp + err);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static bool AnyInterface(Func<RegistryKey, bool> test)
    {
        using var interfaces = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
        if (interfaces == null) return false;
        foreach (var name in interfaces.GetSubKeyNames())
        {
            using var iface = interfaces.OpenSubKey(name);
            if (iface != null && test(iface)) return true;
        }
        return false;
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int i ? i : null;
    }

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static void WriteDword(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}
