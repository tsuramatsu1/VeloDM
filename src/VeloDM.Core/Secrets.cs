using System.Security.Cryptography;
using System.Text;

namespace VeloDM.Core;

/// <summary>
/// Keeps a password out of settings.json in clear text: it is encrypted with the Windows
/// data-protection API, so only the same Windows user on the same PC can read it back.
/// </summary>
public static class Secrets
{
    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!OperatingSystem.IsWindows()) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !OperatingSystem.IsWindows()) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // settings copied from another PC or user: the password has to be entered again
        }
    }
}
