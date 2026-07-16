using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArrControl.Infrastructure.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed partial class SecureSessionTokenServiceTests
{
    [Fact]
    public void Issued_token_is_canonical_and_can_only_be_recovered_as_a_hash()
    {
        var service = new SecureSessionTokenService();

        var first = service.Issue();
        var second = service.Issue();

        Assert.Matches(Base64UrlTokenPattern(), first.Value);
        Assert.Equal(32, first.Hash.Length);
        Assert.NotEqual(first.Value, second.Value);
        Assert.Equal("[REDACTED]", first.ToString());
        Assert.True(service.TryHash(first.Value, out var recoveredHash));
        Assert.Equal(first.Hash, recoveredHash);

        var decodedToken = Convert.FromBase64String(
            first.Value.Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(SHA256.HashData(decodedToken), first.Hash);

        const string base64UrlAlphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var finalCharacterIndex = base64UrlAlphabet.IndexOf(first.Value[^1]);
        Assert.True(finalCharacterIndex >= 0 && finalCharacterIndex % 4 == 0);
        var noncanonicalLastCharacter = base64UrlAlphabet[finalCharacterIndex + 1];
        Assert.False(service.TryHash(
            first.Value[..^1] + noncanonicalLastCharacter,
            out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_token_is_rejected(string? token)
    {
        var service = new SecureSessionTokenService();

        var parsed = service.TryHash(token, out var hash);

        Assert.False(parsed);
        Assert.Empty(hash);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlTokenPattern();
}
