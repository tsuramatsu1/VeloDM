using System.Security.Cryptography;

namespace VeloDM.Core;

public static class Checksums
{
    public static readonly IReadOnlyList<string> Algorithms = ["MD5", "SHA-1", "SHA-256", "SHA-512"];

    /// <summary>The algorithm a pasted checksum most likely is, judged by its length; null if it is not a checksum.</summary>
    public static string? AlgorithmFor(string? checksum)
    {
        var hex = Normalize(checksum);
        if (hex.Length == 0 || !hex.All(Uri.IsHexDigit)) return null;
        return hex.Length switch { 32 => "MD5", 40 => "SHA-1", 64 => "SHA-256", 128 => "SHA-512", _ => null };
    }

    /// <summary>Lower-case hex without spaces, separators or a "sha256:" style prefix.</summary>
    public static string Normalize(string? checksum)
    {
        var text = (checksum ?? "").Trim();
        int colon = text.IndexOf(':');
        if (colon is > 0 and < 10) text = text[(colon + 1)..];
        return new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToLowerInvariant();
    }

    public static bool Matches(string computed, string? expected) =>
        string.Equals(Normalize(computed), Normalize(expected), StringComparison.Ordinal);

    public static async Task<string> ComputeAsync(string path, string algorithm, IProgress<double>? progress, CancellationToken ct)
    {
        using HashAlgorithm hash = algorithm switch
        {
            "MD5" => MD5.Create(),
            "SHA-1" => SHA1.Create(),
            "SHA-256" => SHA256.Create(),
            "SHA-512" => SHA512.Create(),
            _ => throw new ArgumentException("Unknown checksum algorithm: " + algorithm, nameof(algorithm)),
        };

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long total = stream.Length, done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            progress?.Report(total > 0 ? done * 100.0 / total : 0);
        }
        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(hash.Hash!);
    }
}
