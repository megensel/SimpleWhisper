using System.IO;
using Microsoft.Win32;
using SimpleWhisper.Helpers;

namespace SimpleWhisper.Services;

/// <summary>
/// Manages the Windows "Run at startup" registry entry for SimpleWhisper.
/// Uses <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// so it works without administrator privileges.
/// </summary>
public static class StartupService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SimpleWhisper";

    /// <summary>
    /// Adds or removes SimpleWhisper from the Windows startup registry.
    /// </summary>
    /// <param name="enable">
    /// <c>true</c> to register the current executable for startup;
    /// <c>false</c> to remove the registration.
    /// </param>
    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key is null) return;

            if (enable)
            {
                // Use the current executable path so the registry always points
                // to wherever the app is installed (or running from).
                string exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "SimpleWhisper.exe");
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[StartupService] Registry error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns <c>true</c> if SimpleWhisper is currently registered to run at Windows startup.
    /// </summary>
    public static bool IsRegisteredForStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
