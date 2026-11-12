using System.Globalization;

namespace VeloDM.Core;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        if (bytes < 0) return "Unknown";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }
}
