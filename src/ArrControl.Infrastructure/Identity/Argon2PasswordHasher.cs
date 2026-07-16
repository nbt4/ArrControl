using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Identity;
using Sodium;

namespace ArrControl.Infrastructure.Identity;

public sealed class Argon2PasswordHasher : IPasswordHasher, IDisposable
{
    private const int MemoryKiB = 65_536;
    private const int Iterations = 3;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int MaximumPasswordBytes = 1024;
    private const int MaximumPhcLength = 160;
    private const int MaximumVerificationRequests = 16;
    private const string ParameterSegment = "m=65536,t=3,p=1";
    private static readonly TimeSpan VerificationQueueTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim concurrencyGate;
    private readonly ParsedHash dummyHash;
    private int verificationRequests;

    public Argon2PasswordHasher()
    {
        concurrencyGate = new SemaphoreSlim(1, 1);
        dummyHash = ParseRequired(HashCore("ArrControl dummy password verification"));
    }

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        EnsurePasswordLength(password);
        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            return HashCore(password);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    public async Task<PasswordVerificationResult> VerifyAsync(
        string password,
        string? passwordHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        EnsurePasswordLength(password);

        if (Interlocked.Increment(ref verificationRequests) > MaximumVerificationRequests)
        {
            Interlocked.Decrement(ref verificationRequests);
            return new PasswordVerificationResult(PasswordVerificationStatus.CapacityExceeded);
        }

        var enteredGate = false;
        try
        {
            enteredGate = await concurrencyGate.WaitAsync(
                VerificationQueueTimeout,
                cancellationToken);
            if (!enteredGate)
            {
                return new PasswordVerificationResult(PasswordVerificationStatus.CapacityExceeded);
            }

            var hashWasValid = TryParse(passwordHash, out var parsedHash);
            try
            {
                var hashToVerify = hashWasValid ? parsedHash : dummyHash;
                var candidate = DeriveHash(password, hashToVerify.Salt);
                try
                {
                    var verified = CryptographicOperations.FixedTimeEquals(candidate, hashToVerify.Hash);
                    return new PasswordVerificationResult(
                        hashWasValid && verified
                            ? PasswordVerificationStatus.Valid
                            : PasswordVerificationStatus.Invalid);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(candidate);
                }
            }
            finally
            {
                if (hashWasValid)
                {
                    CryptographicOperations.ZeroMemory(parsedHash.Salt);
                    CryptographicOperations.ZeroMemory(parsedHash.Hash);
                }
            }
        }
        finally
        {
            if (enteredGate)
            {
                concurrencyGate.Release();
            }

            Interlocked.Decrement(ref verificationRequests);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(dummyHash.Salt);
        CryptographicOperations.ZeroMemory(dummyHash.Hash);
        concurrencyGate.Dispose();
    }

    private static string HashCore(string password)
    {
        var salt = PasswordHash.ArgonGenerateSalt();
        try
        {
            var hash = DeriveHash(password, salt);
            try
            {
                return $"$argon2id$v=19${ParameterSegment}" +
                    $"${EncodeBase64(salt)}${EncodeBase64(hash)}";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static byte[] DeriveHash(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return PasswordHash.ArgonHashBinary(
                passwordBytes,
                salt,
                Iterations,
                checked(MemoryKiB * 1024),
                HashLength,
                PasswordHash.ArgonAlgorithm.Argon_2ID13);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool TryParse(string? value, out ParsedHash parsedHash)
    {
        parsedHash = default;
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumPhcLength
            || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var components = value.Split('$', StringSplitOptions.None);
        if (components.Length != 6
            || components[0].Length != 0
            || components[1] != "argon2id"
            || components[2] != "v=19"
            || components[3] != ParameterSegment)
        {
            return false;
        }

        if (!TryDecodeCanonicalBase64(components[4], SaltLength, out var salt))
        {
            return false;
        }

        if (!TryDecodeCanonicalBase64(components[5], HashLength, out var hash))
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
            return false;
        }

        parsedHash = new ParsedHash(salt, hash);
        return true;
    }

    private static ParsedHash ParseRequired(string value)
    {
        if (!TryParse(value, out var parsedHash))
        {
            throw new InvalidOperationException("The internal Argon2id profile could not be parsed.");
        }

        return parsedHash;
    }

    private static string EncodeBase64(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=');

    private static bool TryDecodeCanonicalBase64(
        string value,
        int expectedLength,
        out byte[] decoded)
    {
        decoded = [];
        if (value.Length == 0 || value.Contains('='))
        {
            return false;
        }

        try
        {
            var paddingLength = (4 - value.Length % 4) % 4;
            decoded = Convert.FromBase64String(value + new string('=', paddingLength));
            if (decoded.Length != expectedLength
                || !string.Equals(EncodeBase64(decoded), value, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                decoded = [];
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void EnsurePasswordLength(string password)
    {
        if (Encoding.UTF8.GetByteCount(password) > MaximumPasswordBytes)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Passwords may not exceed {0} UTF-8 bytes.",
                    MaximumPasswordBytes),
                nameof(password));
        }
    }

    private readonly record struct ParsedHash(byte[] Salt, byte[] Hash);
}
