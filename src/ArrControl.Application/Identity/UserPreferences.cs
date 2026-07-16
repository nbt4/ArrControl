using ArrControl.Application.Authorization;

namespace ArrControl.Application.Identity;

public static class SupportedUserLocales
{
    public const string English = "en";
    public const string German = "de";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { English, German };
}

public sealed record UserPreferences(string Locale, string TimeZone);

public interface IUserPreferencesStore
{
    Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserPreferences?> UpdateAsync(
        RbacActorContext actor,
        UserPreferences preferences,
        CancellationToken cancellationToken);
}

public sealed class UserPreferenceValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class UserPreferencesService(IUserPreferencesStore store)
{
    public Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user identifier is required.", nameof(userId));
        }

        return store.GetAsync(userId, cancellationToken);
    }

    public Task<UserPreferences?> UpdateAsync(
        RbacActorContext actor,
        string? locale,
        string? timeZone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserId == Guid.Empty || actor.SessionId == Guid.Empty)
        {
            throw new ArgumentException("A valid actor is required.", nameof(actor));
        }

        if (locale is null || !SupportedUserLocales.All.Contains(locale))
        {
            throw new UserPreferenceValidationException("locale_not_supported");
        }

        if (string.IsNullOrWhiteSpace(timeZone)
            || timeZone.Length > 128
            || timeZone.Any(char.IsControl)
            || !IsTimeZoneAvailable(timeZone))
        {
            throw new UserPreferenceValidationException("time_zone_not_supported");
        }

        return store.UpdateAsync(
            actor,
            new UserPreferences(locale, timeZone),
            cancellationToken);
    }

    private static bool IsTimeZoneAvailable(string timeZone)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return true;
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
