using VeloDM.Core;

namespace VeloDM.Tests;

public sealed class LogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VeloDM.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Lines_carry_time_level_message_and_the_error()
    {
        var log = new LogFile(_dir);

        log.Write("INFO", "Added download\n\"a.zip\"");
        log.Write("WARN", "A connection failed", new IOException("The server closed\r\nthe connection early."));

        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} INFO  Added download ""a\.zip""$", lines[0]);
        Assert.EndsWith("WARN  A connection failed | IOException: The server closed the connection early.", lines[1]);
    }

    [Fact]
    public void Unexpected_errors_include_where_they_happened()
    {
        var log = new LogFile(_dir);
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            log.Write("ERROR", "Unexpected error", ex);
        }

        var text = File.ReadAllText(log.Path);
        Assert.Contains("ERROR Unexpected error | InvalidOperationException: boom", text);
        Assert.Contains(nameof(Unexpected_errors_include_where_they_happened), text);
    }

    [Fact]
    public void A_full_log_is_set_aside_and_a_new_one_started()
    {
        var log = new LogFile(_dir, maxBytes: 2000);
        for (int i = 0; i < 60; i++) log.Write("INFO", $"line {i:00} " + new string('x', 60));

        Assert.True(File.Exists(log.PreviousPath));
        Assert.True(new FileInfo(log.Path).Length < 2200);
        Assert.True(new FileInfo(log.PreviousPath).Length < 2200);
        Assert.Contains("line 59", File.ReadAllText(log.Path));
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // never more than the current and the previous file
    }

    [Theory]
    [InlineData("https://files.example.com/dl/archive.rar?token=SECRET&expires=1", "https://files.example.com/dl/archive.rar?…")]
    [InlineData("http://user:pw@localhost:8080/a/b.zip#part", "http://localhost:8080/a/b.zip")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Private+Name", "magnet:0123456789ab…")]
    [InlineData(@"C:\Users\someone\Downloads\linux.torrent", "linux.torrent")]
    [InlineData("", "(none)")]
    public void Logged_addresses_leave_out_tokens_logins_and_folders(string address, string expected) =>
        Assert.Equal(expected, Log.Address(address));
}
