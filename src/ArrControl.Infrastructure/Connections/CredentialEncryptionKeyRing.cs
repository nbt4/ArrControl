using System.Security.Cryptography;
using System.Text;

namespace ArrControl.Infrastructure.Connections;

public sealed record CredentialKeyFile(int Version, string Path);

public sealed class CredentialEncryptionKeyRing : IDisposable
{
    private const int KeyLength = 32;
    private const int MaximumKeyFileBytes = 256;
    private readonly Dictionary<int, byte[]> keys;
    private bool disposed;

    private CredentialEncryptionKeyRing(int activeVersion, Dictionary<int, byte[]> keys)
    {
        ActiveVersion = activeVersion;
        this.keys = keys;
    }

    public static CredentialEncryptionKeyRing Empty => new(0, []);

    public int ActiveVersion { get; }

    public bool IsConfigured => ActiveVersion > 0;

    public static CredentialEncryptionKeyRing Load(
        int activeVersion,
        IReadOnlyCollection<CredentialKeyFile> keyFiles)
    {
        ArgumentNullException.ThrowIfNull(keyFiles);
        if (activeVersion <= 0 || keyFiles.Count == 0)
        {
            throw InvalidConfiguration();
        }

        var loadedKeys = new Dictionary<int, byte[]>();
        try
        {
            foreach (var keyFile in keyFiles)
            {
                if (keyFile.Version <= 0
                    || !Path.IsPathFullyQualified(keyFile.Path)
                    || !loadedKeys.TryAdd(keyFile.Version, ReadKeyFile(keyFile.Path)))
                {
                    throw InvalidConfiguration();
                }
            }

            if (!loadedKeys.ContainsKey(activeVersion))
            {
                throw InvalidConfiguration();
            }

            return new CredentialEncryptionKeyRing(activeVersion, loadedKeys);
        }
        catch
        {
            foreach (var key in loadedKeys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            throw;
        }
    }

    internal ReadOnlySpan<byte> GetKey(int version)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!keys.TryGetValue(version, out var key))
        {
            throw new CryptographicException("The credential key version is unavailable.");
        }

        return key;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var key in keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        keys.Clear();
        disposed = true;
    }

    public override string ToString() => "CredentialEncryptionKeyRing { [REDACTED] }";

    private static byte[] ReadKeyFile(string path)
    {
        byte[] fileBytes;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumKeyFileBytes)
            {
                throw InvalidConfiguration();
            }

            fileBytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(fileBytes);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidConfiguration();
        }

        try
        {
            var encoded = new UTF8Encoding(false, true).GetString(fileBytes);
            encoded = encoded.EndsWith("\r\n", StringComparison.Ordinal)
                ? encoded[..^2]
                : encoded.EndsWith('\n')
                    ? encoded[..^1]
                    : encoded;
            if (encoded.Length != 44 || encoded.Any(char.IsWhiteSpace))
            {
                throw InvalidConfiguration();
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw InvalidConfiguration();
            }

            if (key.Length != KeyLength
                || !string.Equals(
                    Convert.ToBase64String(key),
                    encoded,
                    StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(key);
                throw InvalidConfiguration();
            }

            return key;
        }
        catch (DecoderFallbackException)
        {
            throw InvalidConfiguration();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileBytes);
        }
    }

    private static InvalidOperationException InvalidConfiguration() =>
        new("Credential master-key configuration is invalid.");
}
