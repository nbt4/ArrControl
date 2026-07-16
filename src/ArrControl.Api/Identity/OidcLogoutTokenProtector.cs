using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace ArrControl.Api.Identity;

public sealed class OidcLogoutTokenProtector
{
    private const int MaximumIdTokenLength = 16 * 1024;
    private const int MaximumProtectedTokenLength = 32 * 1024;
    private readonly IDataProtector protector;

    public OidcLogoutTokenProtector(IDataProtectionProvider dataProtectionProvider)
    {
        protector = dataProtectionProvider.CreateProtector(
            "ArrControl.Identity.OidcLogoutToken",
            "v1");
    }

    public bool TryProtect(string? idToken, out string protectedToken)
    {
        protectedToken = string.Empty;
        if (string.IsNullOrEmpty(idToken) || idToken.Length > MaximumIdTokenLength)
        {
            return false;
        }

        var candidate = protector.Protect(idToken);
        if (candidate.Length > MaximumProtectedTokenLength)
        {
            return false;
        }

        protectedToken = candidate;
        return true;
    }

    public bool TryUnprotect(string? protectedToken, out string idToken)
    {
        idToken = string.Empty;
        if (string.IsNullOrEmpty(protectedToken)
            || protectedToken.Length > MaximumProtectedTokenLength)
        {
            return false;
        }

        try
        {
            var candidate = protector.Unprotect(protectedToken);
            if (string.IsNullOrEmpty(candidate) || candidate.Length > MaximumIdTokenLength)
            {
                return false;
            }

            idToken = candidate;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
