using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VeloDM.Tests;

/// <summary>
/// Local HTTP server that streams a random payload slowly so the engine has time to split.
///   /file      honours Range requests (206)
///   /norange   ignores Range (always 200 + full body)
///   /chunked   no Content-Length, no ranges
/// </summary>
internal sealed partial class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private int _current;
    private int _peak;

    public byte[] Data { get; }
    public string BaseUrl { get; }
    public int PeakConnections => Volatile.Read(ref _peak);
    public int ChunkDelayMs { get; set; } = 2;

    /// <summary>Requests whose address contains this text are refused with 403, like an expired link.</summary>
    public string? ExpiredToken { get; set; }

    public TestServer(int size) : this(RandomBytes(size))
    {
    }

    private static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        new Random(1234).NextBytes(data);
        return data;
    }

    public TestServer(byte[] data)
    {
        Data = data;
        int port = FreePort();
        BaseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    [GeneratedRegex(@"bytes=(\d+)-(\d*)")]
    private static partial Regex RangeRegex();

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        int now = Interlocked.Increment(ref _current);
        int peak;
        while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }

        var res = ctx.Response;
        try
        {
            string path = ctx.Request.Url!.AbsolutePath;
            if (ExpiredToken is { } token && ctx.Request.RawUrl!.Contains(token))
            {
                res.StatusCode = 403;
                return;
            }
            long from = 0, to = Data.Length - 1;
            res.StatusCode = 200;

            if (path == "/file" && ctx.Request.Headers["Range"] is { } range && RangeRegex().Match(range) is { Success: true } m)
            {
                from = long.Parse(m.Groups[1].Value);
                if (m.Groups[2].Value.Length > 0) to = Math.Min(to, long.Parse(m.Groups[2].Value));
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {from}-{to}/{Data.Length}";
            }

            if (path == "/chunked") res.SendChunked = true;
            else res.ContentLength64 = to - from + 1;
            res.ContentType = "application/octet-stream";

            const int chunk = 16 * 1024;
            for (long pos = from; pos <= to; pos += chunk)
            {
                int n = (int)Math.Min(chunk, to - pos + 1);
                await res.OutputStream.WriteAsync(Data.AsMemory((int)pos, n));
                if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
            }
        }
        catch
        {
            // Client closed the connection (split segment, pause, probe) — expected.
        }
        finally
        {
            Interlocked.Decrement(ref _current);
            try { res.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
