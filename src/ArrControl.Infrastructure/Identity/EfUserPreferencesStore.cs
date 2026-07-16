using System.Text.Json;
using ArrControl.Application.Authorization;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Identity;

public sealed class EfUserPreferencesStore(
    ArrControlDbContext dbContext,
    TimeProvider timeProvider) : IUserPreferencesStore
{
    public Task<UserPreferences?> GetAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.Set<UserEntity>()
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new UserPreferences(user.Locale, user.TimeZone))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<UserPreferences?> UpdateAsync(
        RbacActorContext actor,
        UserPreferences preferences,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Set<UserEntity>().SingleOrDefaultAsync(
            candidate => candidate.Id == actor.UserId,
            cancellationToken);
        if (user is null)
        {
            return null;
        }

        var changed = user.Locale != preferences.Locale || user.TimeZone != preferences.TimeZone;
        user.Locale = preferences.Locale;
        user.TimeZone = preferences.TimeZone;
        if (changed)
        {
            user.UpdatedAt = timeProvider.GetUtcNow();
        }

        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = timeProvider.GetUtcNow(),
            ActorUserId = actor.UserId,
            ActorType = "user",
            ActorIdentifier = actor.Email,
            Action = "identity.preferences_update",
            ScopeJson = JsonSerializer.Serialize(new { kind = "user", userId = actor.UserId }),
            CorrelationId = actor.RequestContext.CorrelationId,
            Outcome = changed ? "updated" : "unchanged",
            SummaryJson = JsonSerializer.Serialize(new
            {
                preferences.Locale,
                preferences.TimeZone,
            }),
            IpAddress = actor.RequestContext.IpAddress,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return preferences;
    }
}
