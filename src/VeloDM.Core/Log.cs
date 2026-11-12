using System.Globalization;
using System.Text;

namespace VeloDM.Core;

/// <summary>
/// A plain-text log that keeps itself small: when the file reaches <see cref="MaxBytes"/> it
/// becomes "name.1.log" (replacing the previous one) and a new file is started.
/// Writing never throws: a log that cannot be written must not break a download.
/// </summary>
public sealed class LogFile
{
    public const long MaxBytes = 2 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly long _maxBytes;

    public string Path { get; }
    public string PreviousPath { get; }

    public LogFile(string directory, string name = "velodm", long maxBytes = MaxBytes)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, name + ".log");
        PreviousPath = System.IO.Path.Combine(directory, name + ".1.log");
        _maxBytes = maxBytes;
    }

    public void Write(string level, string message, Exception? exception = null)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.PadRight(5)).Append(' ')
            .Append(message.ReplaceLineEndings(" "));
        if (exception is not null)
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message.ReplaceLineEndings(" "));
        line.AppendLine();
        // The stack is only worth its space for failures nobody expected.
        if (exception is not null && level == "ERROR" && exception.StackTrace is { } stack)
            line.AppendLine(stack);

        lock (_sync)
        {
            try
            {
                var info = new FileInfo(Path);
                if (info.Exists && info.Length >= _maxBytes) File.Move(Path, PreviousPath, overwrite: true);
                File.AppendAllText(Path, line.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The app-wide log (<c>%LocalAppData%\VeloDM\logs\velodm.log</c>). Nothing is written until the
/// app calls <see cref="Initialize"/>, so the engine can be used (and tested) without a log.
/// Addresses are logged without their query string, and cookies and passwords are never logged,
/// because download links often carry access tokens.
/// </summary>
public static class Log
{
    private static LogFile? _file;

    public static string? Directory { get; private set; }

    public static void Initialize(string dataDirectory)
    {
        Directory = Path.Combine(dataDirectory, "logs");
        try
        {
            _file = new LogFile(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
        }
    }

    public static void Info(string message) => _file?.Write("INFO", message);
    public static void Warn(string message, Exception? exception = null) => _file?.Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => _file?.Write("ERROR", message, exception);

    /// <summary>An address that is safe to log: no query string, fragment or login, and only the hash of a magnet link.</summary>
    public static string Address(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "(none)";
        address = address.Trim();
        if (address.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            int at = address.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? "magnet:(link)" : "magnet:" + new string(address[(at + 5)..].TakeWhile(char.IsLetterOrDigit).Take(12).ToArray()) + "…";
        }
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}{(uri.Query.Length > 1 ? "?…" : "")}";
        return Path.GetFileName(address); // a local .torrent file: its folder is nobody's business
    }
}
