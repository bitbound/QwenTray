using System.Security.Cryptography;
using Bitbound.SystemAbstractions.FileSystem;

namespace QwenTray.Updates;

/// <summary>
/// Verifies a downloaded asset against the SHA-256 digest GitHub publishes for it. GitHub reports the
/// digest as "sha256:" followed by lowercase hex and omits it for assets uploaded before the field
/// existed, so an absent or unrecognized digest parses as false rather than as a mismatch.
/// </summary>
internal static class UpdateDigest
{
    private const string AlgorithmPrefix = "sha256:";

    private const int Sha256HexLength = 64;

    /// <summary>Parses a "sha256:hex" digest, returning false for a missing digest or another algorithm.</summary>
    public static bool TryParse(string? digest, out byte[] expected)
    {
        expected = [];

        if (string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith(AlgorithmPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hex = digest[AlgorithmPrefix.Length..].Trim();

        if (hex.Length != Sha256HexLength)
        {
            return false;
        }

        try
        {
            expected = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Hashes the file at <paramref name="path"/> and compares it to <paramref name="expected"/>.</summary>
    public static async Task<bool> MatchesAsync(string path, byte[] expected, IFileSystem fileSystem, CancellationToken cancellationToken)
    {
        await using var stream = fileSystem.OpenFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
