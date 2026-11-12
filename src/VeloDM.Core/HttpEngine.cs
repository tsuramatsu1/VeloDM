using System.Net;
using System.Net.Http.Headers;

namespace VeloDM.Core;

public sealed record RemoteFileInfo(Uri FinalUri, long Size, bool AcceptRanges, string FileName, string? ContentType);

public sealed class RangeNotSupportedException()
    : IOException("The server stopped honouring range requests, so the download cannot be resumed.");

public static class HttpEngine
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One shared client. Decompression is off because byte ranges refer to the encoded
    /// body, and HTTP/1.1 is used so each segment really gets its own TCP connection.
    /// </summary>
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        MaxConnectionsPerServer = 128,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    public static HttpRequestMessage CreateRequest(string url, string? referrer, string? cookies, string? userAgent,
                                                   long? from, long? to)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var r))
            req.Headers.Referrer = r;
        if (!string.IsNullOrWhiteSpace(cookies))
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (from is long f)
            req.Headers.Range = new RangeHeaderValue(f, to is long t && t >= 0 ? t : null);
        return req;
    }

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
    }

    /// <summary>
    /// Asks for "bytes=0-" and reads only the headers: a 206 reply proves the server can
    /// resume and split; a 200 reply means one connection, no resume.
    /// </summary>
    public static async Task<RemoteFileInfo> ProbeAsync(string url, string? referrer, string? cookies, string? userAgent,
                                                        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            using var req = CreateRequest(url, referrer, cookies, userAgent, 0, null);
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Typically an empty file.
                var empty = resp.RequestMessage?.RequestUri ?? new Uri(url);
                return new RemoteFileInfo(empty, 0, false, FileNameHelper.FromUrl(empty), null);
            }

            EnsureSuccess(resp);
            var finalUri = resp.RequestMessage?.RequestUri ?? new Uri(url);
            var headers = resp.Content.Headers;

            bool ranges = resp.StatusCode == HttpStatusCode.PartialContent;
            long size = ranges
                ? headers.ContentRange?.Length ?? -1
                : headers.ContentLength ?? -1;
            if (size < 0) ranges = false;

            var mime = headers.ContentType?.MediaType;
            string? name = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName;
            name = string.IsNullOrWhiteSpace(name) ? FileNameHelper.FromUrl(finalUri) : FileNameHelper.Sanitize(name);
            if (Path.GetExtension(name).Length == 0 && FileNameHelper.ExtensionForMime(mime) is { } ext)
                name += ext;

            return new RemoteFileInfo(finalUri, size, ranges, name, mime);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server did not respond in time.");
        }
    }
}
