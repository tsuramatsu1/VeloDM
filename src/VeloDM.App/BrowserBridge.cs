using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VeloDM.Core;

namespace VeloDM.App;

public sealed record BridgeRequest(
    string? Url,
    string? Referrer = null,
    string? Cookies = null,
    string? UserAgent = null,
    string? FileName = null,
    string? Mime = null,
    bool Force = false,
    bool ShowWindow = false,
    // Streamed video: Url is the page showing it, ManifestUrl a playlist the browser saw there.
    bool Stream = false,
    string? ManifestUrl = null,
    string? Title = null);

/// <summary>
/// Loopback HTTP endpoint used by the browser extension and by second app instances.
///
/// Web pages must not be able to queue downloads, so every request needs the custom
/// <see cref="AuthHeader"/> header (which forces a CORS preflight we never approve) and
/// any Origin header must belong to a browser extension.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public const string AuthHeader = "X-VeloDM";
    private const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpListener _listener = new();
    private readonly Func<BridgeRequest, bool> _handler;

    /// <summary>
    /// Raised for every request from a browser extension, with the version it reports in the
    /// <see cref="AuthHeader"/> header (extensions before 1.5.1 send "1").
    /// </summary>
    public event Action<string>? ExtensionSeen;

    public BrowserBridge(int port, Func<BridgeRequest, bool> handler)
    {
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _handler = handler;
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!IsTrustedOrigin(req.Headers["Origin"]) || req.Headers[AuthHeader] is null)
            {
                Reply(res, 403, new { error = "forbidden" });
                return;
            }

            if (req.Headers["Origin"] is not null) ExtensionSeen?.Invoke(req.Headers[AuthHeader]!);

            switch (req.HttpMethod, req.Url?.AbsolutePath)
            {
                case ("GET", "/ping"):
                    Reply(res, 200, new { app = "VeloDM", version = typeof(BrowserBridge).Assembly.GetName().Version?.ToString() });
                    break;

                case ("POST", "/show"):
                    _handler(new BridgeRequest(null, ShowWindow: true));
                    Reply(res, 200, new { ok = true });
                    break;

                case ("POST", "/add"):
                    var body = ReadBody(req);
                    var payload = JsonSerializer.Deserialize<BridgeRequest>(body, Json);
                    // Local .torrent paths are only accepted from local processes (a second VeloDM
                    // instance launched by a file association), never from a browser extension.
                    bool fromLocalProcess = req.Headers["Origin"] is null;
                    bool valid = payload is not null
                                 && (App.IsHttpUrl(payload.Url) || TorrentSources.IsMagnet(payload.Url)
                                     || (fromLocalProcess && TorrentSources.IsTorrentFile(payload.Url)));
                    if (!valid)
                    {
                        Reply(res, 400, new { error = "an http(s) url or magnet link is required" });
                        return;
                    }
                    if (payload!.Stream && !App.IsHttpUrl(payload.Url))
                    {
                        Reply(res, 400, new { error = "a video needs the http(s) address of its page" });
                        return;
                    }
                    var manifest = App.IsHttpUrl(payload.ManifestUrl) ? payload.ManifestUrl : null;
                    bool accepted = _handler(payload with { ShowWindow = false, ManifestUrl = manifest });
                    Reply(res, 200, new { ok = true, accepted });
                    break;

                default:
                    Reply(res, 404, new { error = "not found" });
                    break;
            }
        }
        catch (JsonException)
        {
            TryReply(res, 400, new { error = "invalid json" });
        }
        catch (Exception ex)
        {
            TryReply(res, 500, new { error = ex.Message });
        }
        finally
        {
            try { res.Close(); } catch (Exception) { }
        }
    }

    private static bool IsTrustedOrigin(string? origin) =>
        origin is null
        || origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);

    private static string ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBodyBytes) throw new InvalidDataException("Request body too large.");
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxBodyBytes];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static void Reply(HttpListenerResponse res, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    private static void TryReply(HttpListenerResponse res, int status, object body)
    {
        try { Reply(res, status, body); } catch (Exception) { /* client already gone */ }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException) { }
    }
}
