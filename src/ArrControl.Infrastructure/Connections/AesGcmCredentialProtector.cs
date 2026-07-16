using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Connections;

namespace ArrControl.Infrastructure.Connections;

public sealed class AesGcmCredentialProtector(CredentialEncryptionKeyRing keyRing)
    : ICredentialProtector
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const string AssociatedDataPrefix = "ArrControl.Credential.v1";

    public bool IsConfigured => keyRing.IsConfigured;

    public ProtectedCredential Protect(
        Guid instanceId,
        string purpose,
        string secret)
    {
        if (!IsConfigured)
        {
            throw new CredentialEncryptionUnavailableException();
        }

        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = GC.AllocateUninitializedArray<byte>(plaintext.Length);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = GC.AllocateUninitializedArray<byte>(TagLength);
        var associatedData = CreateAssociatedData(instanceId, purpose, keyRing.ActiveVersion);
        try
        {
            using var aes = new AesGcm(keyRing.GetKey(keyRing.ActiveVersion), TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return new ProtectedCredential(
                ciphertext,
                nonce,
                tag,
                keyRing.ActiveVersion);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public SecretCredential Unprotect(StoredProtectedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsConfigured)
        {
            throw new CredentialEncryptionUnavailableException();
        }

        if (credential.Ciphertext.Length == 0
            || credential.Nonce.Length != NonceLength
            || credential.Tag.Length != TagLength
            || credential.KeyVersion <= 0
            || !CredentialPurposes.IsKnown(credential.Purpose))
        {
            throw new CredentialDecryptionException();
        }

        var plaintext = GC.AllocateUninitializedArray<byte>(credential.Ciphertext.Length);
        var associatedData = CreateAssociatedData(
            credential.InstanceId,
            credential.Purpose,
            credential.KeyVersion);
        try
        {
            using var aes = new AesGcm(keyRing.GetKey(credential.KeyVersion), TagLength);
            aes.Decrypt(
                credential.Nonce,
                credential.Ciphertext,
                credential.Tag,
                plaintext,
                associatedData);
            return new SecretCredential(new UTF8Encoding(false, true).GetString(plaintext));
        }
        catch (Exception exception) when (
            exception is CryptographicException or DecoderFallbackException)
        {
            throw new CredentialDecryptionException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static byte[] CreateAssociatedData(
        Guid instanceId,
        string purpose,
        int keyVersion) =>
        Encoding.UTF8.GetBytes(
            $"{AssociatedDataPrefix}\0{instanceId:D}\0{purpose}\0{keyVersion}");
}
