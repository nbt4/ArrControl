using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class UserPreferencesServiceTests
{
    private static readonly RbacActorContext Actor = new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "operator@example.invalid",
        new AuthenticationRequestContext("preferences-test", null));

    [Theory]
    [InlineData("en", "UTC")]
    [InlineData("de", "Europe/Berlin")]
    public async Task Accepts_shipped_locales_and_available_IANA_timezones(
        string locale,
        string timeZone)
    {
        var store = new RecordingStore();
        var result = await new UserPreferencesService(store).UpdateAsync(
            Actor, locale, timeZone, CancellationToken.None);

        Assert.Equal(new UserPreferences(locale, timeZone), result);
        Assert.Equal(result, store.Updated);
    }

    [Theory]
    [InlineData("fr", "UTC", "locale_not_supported")]
    [InlineData("EN", "UTC", "locale_not_supported")]
    [InlineData("en", "Mars/Olympus_Mons", "time_zone_not_supported")]
    [InlineData("en", "", "time_zone_not_supported")]
    public async Task Rejects_unshipped_locales_and_unknown_timezones(
        string locale,
        string timeZone,
        string expectedCode)
    {
        var exception = await Assert.ThrowsAsync<UserPreferenceValidationException>(() =>
            new UserPreferencesService(new RecordingStore()).UpdateAsync(
                Actor, locale, timeZone, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
    }

    private sealed class RecordingStore : IUserPreferencesStore
    {
        public UserPreferences? Updated { get; private set; }

        public Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<UserPreferences?>(new("en", "UTC"));

        public Task<UserPreferences?> UpdateAsync(
            RbacActorContext actor,
            UserPreferences preferences,
            CancellationToken cancellationToken)
        {
            Updated = preferences;
            return Task.FromResult<UserPreferences?>(preferences);
        }
    }
}
