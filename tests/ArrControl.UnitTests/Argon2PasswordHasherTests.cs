using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class Argon2PasswordHasherTests
{
    [Fact]
    public async Task Hash_and_verify_use_the_expected_argon2id_profile()
    {
        const string password = "correct horse battery staple";
        using var hasher = new Argon2PasswordHasher();

        var passwordHash = await hasher.HashAsync(password, CancellationToken.None);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", passwordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(password, passwordHash, StringComparison.Ordinal);

        var valid = await hasher.VerifyAsync(
            password,
            passwordHash,
            CancellationToken.None);
        var wrongPassword = await hasher.VerifyAsync(
            "not the correct password",
            passwordHash,
            CancellationToken.None);
        var malformedProfile = await hasher.VerifyAsync(
            password,
            passwordHash.Replace("m=65536", "m=8192", StringComparison.Ordinal),
            CancellationToken.None);

        Assert.Equal(PasswordVerificationStatus.Valid, valid.Status);
        Assert.Equal(PasswordVerificationStatus.Invalid, wrongPassword.Status);
        Assert.Equal(PasswordVerificationStatus.Invalid, malformedProfile.Status);
    }

    [Fact]
    public async Task Verification_rejects_noncanonical_or_unbounded_phc_variants()
    {
        const string password = "correct horse battery staple";
        using var hasher = new Argon2PasswordHasher();
        var passwordHash = await hasher.HashAsync(password, CancellationToken.None);
        var components = passwordHash.Split('$', StringSplitOptions.None);
        var malformedHashes = new[]
        {
            passwordHash.Replace("argon2id", "argon2i", StringComparison.Ordinal),
            passwordHash.Replace("v=19", "v=16", StringComparison.Ordinal),
            passwordHash.Replace("m=65536,t=3,p=1", "t=3,m=65536,p=1", StringComparison.Ordinal),
            passwordHash.Replace("m=65536", "m=065536", StringComparison.Ordinal),
            passwordHash + " ",
            passwordHash.Replace(components[4], components[4] + "=", StringComparison.Ordinal),
            passwordHash.Replace(components[4], "AA", StringComparison.Ordinal),
            passwordHash.Replace(components[5], "AA", StringComparison.Ordinal),
        };

        foreach (var malformedHash in malformedHashes)
        {
            var result = await hasher.VerifyAsync(
                password,
                malformedHash,
                CancellationToken.None);
            Assert.Equal(PasswordVerificationStatus.Invalid, result.Status);
        }
    }

    [Fact]
    public async Task Passwords_over_the_utf8_limit_are_rejected_before_hashing()
    {
        using var hasher = new Argon2PasswordHasher();
        var oversizedPassword = new string('\u20ac', 342);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            hasher.HashAsync(oversizedPassword, CancellationToken.None));
    }
}
