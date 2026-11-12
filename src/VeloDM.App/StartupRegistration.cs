using System;
using Microsoft.Win32;

namespace VeloDM.App;

/// <summary>
/// "Start VeloDM when I sign in": a value under the current user's Run key (no admin rights needed).
/// The registry is the only record of this choice, so it cannot drift from what Windows will do.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager's "Startup apps" switch lives here; an entry it disabled is skipped by Windows.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "VeloDM";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte: even = enabled, odd = disabled in Task Manager.
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] % 2 == 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the VeloDM executable path.");
            run.SetValue(ValueName, $"\"{exe}\" --tray");
            // Clear a "disabled" mark left by Task Manager, or the entry would still be skipped.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
