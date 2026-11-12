using MonoTorrent;

namespace VeloDM.Core;

/// <summary>Recognises the three ways a torrent can be handed to VeloDM.</summary>
public static class TorrentSources
{
    public const long MaxTorrentFileBytes = 32 * 1024 * 1024;

    public static bool IsMagnet(string? text) =>
        text is not null && text.TrimStart().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
                         && MagnetLink.TryParse(text.Trim(), out _);

    public static bool IsTorrentUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && u.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    public static bool IsTorrentFile(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Path.IsPathFullyQualified(text.Trim())
        && text.Trim().EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
        && File.Exists(text.Trim());

    public static bool IsTorrent(string? text) => IsMagnet(text) || IsTorrentUrl(text) || IsTorrentFile(text);

    /// <summary>Downloads a .torrent over HTTP with the item's cookies/referrer, or reads a local one.</summary>
    public static async Task<byte[]> ReadTorrentBytesAsync(string source, string? referrer, string? cookies, string? userAgent,
                                                           CancellationToken ct)
    {
        if (IsTorrentFile(source))
        {
            var info = new FileInfo(source.Trim());
            if (info.Length > MaxTorrentFileBytes) throw new InvalidDataException("The .torrent file is too large.");
            return await File.ReadAllBytesAsync(info.FullName, ct).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpEngine.ProbeTimeout);
        using var request = HttpEngine.CreateRequest(source.Trim(), referrer, cookies, userAgent, null, null);
        using var response = await HttpEngine.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);
        HttpEngine.EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaxTorrentFileBytes)
            throw new InvalidDataException("The .torrent file is too large.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        if (bytes.Length > MaxTorrentFileBytes) throw new InvalidDataException("The .torrent file is too large.");
        return bytes;
    }

    public static Torrent Parse(byte[] bytes) =>
        Torrent.TryLoad(bytes, out var torrent) && torrent is not null
            ? torrent
            : throw new InvalidDataException("This is not a valid .torrent file.");

    public static string NameFor(MagnetLink magnet) =>
        !string.IsNullOrWhiteSpace(magnet.Name)
            ? FileNameHelper.Sanitize(magnet.Name)
            : "magnet-" + magnet.InfoHashes.V1OrV2.ToHex()[..8].ToLowerInvariant();

    public static string HashOf(InfoHashes hashes) => hashes.V1OrV2.ToHex().ToLowerInvariant();
}
