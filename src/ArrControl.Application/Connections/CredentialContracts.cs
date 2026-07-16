using System.Security.Cryptography;
using ArrControl.Application.Authorization;

namespace ArrControl.Application.Connections;

public static class CredentialPurposes
{
    public const string ApiKey = "api-key";
    public const string Username = "username";
    public const string Password = "password";

    public static IReadOnlyList<string> All { get; } = [ApiKey, Username, Password];

    public static bool IsKnown(string? purpose) =>
        purpose is not null && All.Contains(purpose, StringComparer.Ordinal);
}

public static class CredentialLimits
{
    public const int MaximumSecretBytes = 4096;
}

public sealed class ProtectedCredential : IDisposable
{
    public ProtectedCredential(
        byte[] ciphertext,
        byte[] nonce,
        byte[] tag,
        int keyVersion)
    {
        Ciphertext = ciphertext;
        Nonce = nonce;
        Tag = tag;
        KeyVersion = keyVersion;
    }

    public byte[] Ciphertext { get; }

    public byte[] Nonce { get; }

    public byte[] Tag { get; }

    public int KeyVersion { get; }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Ciphertext);
        CryptographicOperations.ZeroMemory(Nonce);
        CryptographicOperations.ZeroMemory(Tag);
    }

    public override string ToString() => "[REDACTED]";
}

public sealed record StoredProtectedCredential(
    Guid InstanceId,
    string Purpose,
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] Tag,
    int KeyVersion)
{
    public override string ToString() => "[REDACTED]";
}

public sealed class SecretCredential(string value)
{
    public string Value { get; } = value;

    public override string ToString() => "[REDACTED]";
}

public sealed record CredentialMetadata(
    string Purpose,
    bool Configured,
    DateTimeOffset UpdatedAt);

public sealed record InstanceCredentialScope(
    bool Exists,
    Guid? InstanceGroupId);

public sealed record CredentialUpsertStoreResult(
    bool InstanceExists,
    bool Created,
    CredentialMetadata? Metadata = null);

public sealed record CredentialDeleteStoreResult(
    bool InstanceExists,
    bool Deleted);

public enum PutCredentialStatus
{
    Created,
    Updated,
    NotFound,
    EncryptionUnavailable,
}

public sealed record PutCredentialResult(
    PutCredentialStatus Status,
    CredentialMetadata? Credential = null);

public enum DeleteCredentialStatus
{
    Deleted,
    Absent,
    NotFound,
}

public interface ICredentialProtector
{
    bool IsConfigured { get; }

    ProtectedCredential Protect(
        Guid instanceId,
        string purpose,
        string secret);

    SecretCredential Unprotect(StoredProtectedCredential credential);
}

public interface ICredentialStore
{
    Task<InstanceCredentialScope> GetInstanceScopeAsync(
        Guid instanceId,
        CancellationToken cancellationToken);

    Task<CredentialUpsertStoreResult> UpsertAsync(
        RbacActorContext actor,
        Guid instanceId,
        string purpose,
        ProtectedCredential credential,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CredentialMetadata>> ListMetadataAsync(
        Guid instanceId,
        CancellationToken cancellationToken);

    Task<CredentialDeleteStoreResult> DeleteAsync(
        RbacActorContext actor,
        Guid instanceId,
        string purpose,
        CancellationToken cancellationToken);

    Task<StoredProtectedCredential?> FindAsync(
        Guid instanceId,
        string purpose,
        CancellationToken cancellationToken);
}

public sealed class CredentialValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class CredentialEncryptionUnavailableException()
    : Exception("Credential encryption is unavailable.");

public sealed class CredentialDecryptionException()
    : Exception("Credential decryption failed.");
