using System.Net.Mail;

namespace ArrControl.Application.Identity;

public static class IdentityEmailAddress
{
    public const int MaximumLength = 320;

    public static bool TryNormalize(
        string? value,
        out string canonicalEmail,
        out string normalizedEmail)
    {
        canonicalEmail = string.Empty;
        normalizedEmail = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > MaximumLength)
        {
            return false;
        }

        try
        {
            var parsed = new MailAddress(candidate);
            if (!string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            canonicalEmail = parsed.Address;
            normalizedEmail = parsed.Address.ToUpperInvariant();
            return normalizedEmail.Length <= MaximumLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
