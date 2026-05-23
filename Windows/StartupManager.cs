using Microsoft.Win32;

namespace CopilotUsageFilter;

/// <summary>
/// Manages the HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry
/// so the app launches automatically at Windows login.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CopilotUsageFilter";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(AppName) is string val &&
                       val.Equals(ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException($"Cannot open {RunKey}");
        key.SetValue(AppName, ExePath, RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    public static void Toggle()
    {
        if (IsEnabled) Disable(); else Enable();
    }

    private static string ExePath =>
        $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";
}
