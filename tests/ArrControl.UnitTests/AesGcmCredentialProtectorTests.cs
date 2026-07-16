using System.Security.Cryptography;
using ArrControl.Application.Connections;
using ArrControl.Infrastructure.Connections;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class AesGcmCredentialProtectorTests
{
    [Fact]
    public void Key_ring_accepts_only_canonical_32_byte_file_keys_and_redacts_itself()
    {
        using var valid = TemporaryKeyFile.Create();
        using var ring = CredentialEncryptionKeyRing.Load(
            1,
            [new CredentialKeyFile(1, valid.Path)]);

        Assert.True(ring.IsConfigured);
        Assert.Equal(1, ring.ActiveVersion);
        Assert.DoesNotContain(valid.EncodedKey, ring.ToString(), StringComparison.Ordinal);

        using var tooShort = TemporaryKeyFile.Create(RandomNumberGenerator.GetBytes(31));
        var invalidLength = Assert.Throws<InvalidOperationException>(() =>
            CredentialEncryptionKeyRing.Load(
                1,
                [new CredentialKeyFile(1, tooShort.Path)]));
        Assert.DoesNotContain(tooShort.Path, invalidLength.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(tooShort.EncodedKey, invalidLength.ToString(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            CredentialEncryptionKeyRing.Load(
                2,
                [new CredentialKeyFile(1, valid.Path)]));
        Assert.Throws<InvalidOperationException>(() =>
            CredentialEncryptionKeyRing.Load(
                1,
                [
                    new CredentialKeyFile(1, valid.Path),
                    new CredentialKeyFile(1, valid.Path),
                ]));
    }

    [Fact]
    public void Protector_round_trips_rotated_keys_randomizes_ciphertext_and_binds_context()
    {
        using var firstKey = TemporaryKeyFile.Create();
        using var secondKey = TemporaryKeyFile.Create();
        var instanceId = Guid.CreateVersion7();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        ProtectedCredential oldCredential;
        using (var oldRing = CredentialEncryptionKeyRing.Load(
                   1,
                   [new CredentialKeyFile(1, firstKey.Path)]))
        {
            var oldProtector = new AesGcmCredentialProtector(oldRing);
            oldCredential = oldProtector.Protect(instanceId, CredentialPurposes.ApiKey, secret);
        }

        using var rotatedRing = CredentialEncryptionKeyRing.Load(
            2,
            [
                new CredentialKeyFile(1, firstKey.Path),
                new CredentialKeyFile(2, secondKey.Path),
            ]);
        var protector = new AesGcmCredentialProtector(rotatedRing);
        using var first = protector.Protect(instanceId, CredentialPurposes.ApiKey, secret);
        using var second = protector.Protect(instanceId, CredentialPurposes.ApiKey, secret);

        Assert.Equal(2, first.KeyVersion);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(secret, protector.Unprotect(ToStored(instanceId, first)).Value);
        Assert.Equal(secret, protector.Unprotect(ToStored(instanceId, oldCredential)).Value);
        Assert.Equal("[REDACTED]", first.ToString());

        var wrongInstance = ToStored(Guid.CreateVersion7(), first);
        Assert.Throws<CredentialDecryptionException>(() => protector.Unprotect(wrongInstance));
        var tamperedTag = first.Tag.ToArray();
        tamperedTag[0] ^= 0x80;
        Assert.Throws<CredentialDecryptionException>(() => protector.Unprotect(
            new StoredProtectedCredential(
                instanceId,
                CredentialPurposes.ApiKey,
                first.Ciphertext,
                first.Nonce,
                tamperedTag,
                first.KeyVersion)));
        oldCredential.Dispose();
    }

    private static StoredProtectedCredential ToStored(
        Guid instanceId,
        ProtectedCredential credential) =>
        new(
            instanceId,
            CredentialPurposes.ApiKey,
            credential.Ciphertext,
            credential.Nonce,
            credential.Tag,
            credential.KeyVersion);

    private sealed class TemporaryKeyFile : IDisposable
    {
        private TemporaryKeyFile(string path, string encodedKey)
        {
            Path = path;
            EncodedKey = encodedKey;
        }

        public string Path { get; }

        public string EncodedKey { get; }

        public static TemporaryKeyFile Create(byte[]? key = null)
        {
            var keyBytes = key ?? RandomNumberGenerator.GetBytes(32);
            var encoded = Convert.ToBase64String(keyBytes);
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllText(path, encoded + Environment.NewLine);
            return new TemporaryKeyFile(path, encoded);
        }

        public void Dispose() => File.Delete(Path);
    }
}
