using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VeloDM.App;

/// <summary>Registers VeloDM for magnet: links and .torrent files under HKCU (no admin rights needed).</summary>
public static class FileAssociations
{
    private const string ProgId = "VeloDM.Torrent";

    public static void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the VeloDM executable path.");
        var command = $"\"{exe}\" \"%1\"";
        var icon = $"\"{exe}\",0";

        using (var magnet = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet"))
        {
            magnet.SetValue("", "URL:Magnet link");
            magnet.SetValue("URL Protocol", "");
            using var defaultIcon = magnet.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue("", icon);
            using var open = magnet.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "Torrent file");
            using var defaultIcon = progId.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue("", icon);
            using var open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }

        using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
        {
            ext.SetValue("", ProgId);
            ext.SetValue("Content Type", "application/x-bittorrent");
            using var openWith = ext.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
