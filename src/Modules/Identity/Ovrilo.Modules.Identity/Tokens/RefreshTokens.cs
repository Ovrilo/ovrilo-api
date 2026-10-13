using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Ovrilo.Modules.Identity.Tokens;

/// <summary>
/// Refresh token format: <c>{sessionId:N}.{base64url(32 random bytes)}</c>.
/// Only a SHA-256 hash of the secret is stored (ADR-0012).
/// </summary>
internal static class RefreshTokens
{
    private const int SecretBytes = 32;

    public static (string Token, string Hash) Create(Guid sessionId)
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        return ($"{sessionId:N}.{Base64Url.EncodeToString(secret)}", Convert.ToHexString(SHA256.HashData(secret)));
    }

    public static bool TryParse(string? token, out Guid sessionId, out byte[] secret)
    {
        sessionId = Guid.Empty;
        secret = [];

        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return false;
        }

        var separator = token.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || !Guid.TryParseExact(token.AsSpan(0, separator), "N", out sessionId))
        {
            return false;
        }

        var encoded = token.AsSpan(separator + 1);
        var buffer = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
        if (Base64Url.DecodeFromChars(encoded, buffer, out _, out var written) != OperationStatus.Done || written != SecretBytes)
        {
            return false;
        }

        secret = buffer[..written];
        return true;
    }

    public static bool Matches(ReadOnlySpan<byte> secret, string storedHash)
    {
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(secret, actual);
        return Convert.FromHexString(storedHash, expected, out _, out var written) == OperationStatus.Done
            && written == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
