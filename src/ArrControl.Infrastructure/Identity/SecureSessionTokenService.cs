using System.Security.Cryptography;
using ArrControl.Application.Identity;

namespace ArrControl.Infrastructure.Identity;

public sealed class SecureSessionTokenService : ISessionTokenService
{
    private const int TokenLength = 32;
    private const int EncodedTokenLength = 43;

    public SecretToken Issue()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(TokenLength);
        try
        {
            return new SecretToken(
                EncodeBase64Url(tokenBytes),
                SHA256.HashData(tokenBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    public bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (token is null || token.Length != EncodedTokenLength)
        {
            return false;
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = Convert.FromBase64String(
                token.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (tokenBytes.Length != TokenLength
                || !string.Equals(EncodeBase64Url(tokenBytes), token, StringComparison.Ordinal))
            {
                return false;
            }

            hash = SHA256.HashData(tokenBytes);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
