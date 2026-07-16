using System.Security.Cryptography;
using ArrControl.Application.Activity;
using ArrControl.Application.Connections;
using ArrControl.Application.Health;
using ArrControl.Infrastructure.Automation;
using ArrControl.Infrastructure.Catalog;
using ArrControl.Infrastructure.Connections;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Automation;
using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArrControl.IntegrationTests;

public sealed class DownloadClientPersistenceTests(AuthApiDatabaseFixture fixture)
    : IClassFixture<AuthApiDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Download_clients_receive_schedules_and_resolve_all_encrypted_credentials()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString).Options;
        var instances = new[] { "nzbget", "sabnzbd", "qbittorrent", "transmission", "deluge" }
            .Select(kind => new InstanceEntity
            {
                Name = $"{kind} {Guid.NewGuid():N}",
                Kind = kind,
                BaseUrl = $"https://{kind}.example.invalid/",
                Enabled = true,
                TlsVerificationEnabled = true,
                CreatedAt = Now,
                UpdatedAt = Now,
            }).ToArray();
        var keyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(keyPath,
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + Environment.NewLine);
            using var keyRing = CredentialEncryptionKeyRing.Load(1, [new CredentialKeyFile(1, keyPath)]);
            var protector = new AesGcmCredentialProtector(keyRing);
            var credentials = new[]
            {
                Credential(instances[0].Id, CredentialPurposes.Username, "fixture-user", protector),
                Credential(instances[0].Id, CredentialPurposes.Password, "fixture-password-secret", protector),
            };

            await using (var seed = new ArrControlDbContext(options))
            {
                seed.AddRange(instances);
                seed.AddRange(credentials);
                await seed.SaveChangesAsync();
                Assert.Equal(5, await new EfActivityScheduleProvisioner(seed, new FixedTimeProvider(Now))
                    .ReconcileAsync(CancellationToken.None));
                Assert.Equal(5, await new EfHealthScheduleProvisioner(seed, new FixedTimeProvider(Now))
                    .ReconcileAsync(CancellationToken.None));
                Assert.Equal(0, await new EfActivityScheduleProvisioner(seed, new FixedTimeProvider(Now))
                    .ReconcileAsync(CancellationToken.None));
                Assert.Equal(0, await new EfHealthScheduleProvisioner(seed, new FixedTimeProvider(Now))
                    .ReconcileAsync(CancellationToken.None));
            }

            await using (var context = new ArrControlDbContext(options))
            {
                var schedules = await context.Set<ScheduleEntity>().AsNoTracking()
                    .Where(value => value.Enabled).GroupBy(value => value.Type)
                    .ToDictionaryAsync(value => value.Key, value => value.Count());
                Assert.Equal(5, schedules[ActivityJobTypes.Sync]);
                Assert.Equal(5, schedules[HealthJobTypes.Sync]);

                var target = await new EfCatalogSyncTargetResolver(context, protector)
                    .ResolveAsync(instances[0].Id, CancellationToken.None);
                Assert.NotNull(target);
                Assert.True(target.Connection.TryGetCredential(CredentialPurposes.Username, out var username));
                Assert.True(target.Connection.TryGetCredential(CredentialPurposes.Password, out var password));
                Assert.Equal("fixture-user", username);
                Assert.Equal("fixture-password-secret", password);
                Assert.DoesNotContain(password, target.Connection.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task Media_servers_receive_only_the_supported_health_schedule()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString).Options;
        var instances = new[] { "plex", "jellyfin", "emby" }.Select(kind => new InstanceEntity
        {
            Name = $"{kind} {Guid.NewGuid():N}",
            Kind = kind,
            BaseUrl = $"https://{kind}.example.invalid/",
            Enabled = true,
            TlsVerificationEnabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        }).ToArray();

        await using (var context = new ArrControlDbContext(options))
        {
            context.AddRange(instances);
            await context.SaveChangesAsync();
            Assert.Equal(3, await new EfHealthScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await new EfHealthScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await new EfActivityScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
        }

        await using (var verification = new ArrControlDbContext(options))
        {
            var schedules = await verification.Set<ScheduleEntity>().AsNoTracking().ToArrayAsync();
            Assert.Equal(3, schedules.Length);
            Assert.All(schedules, value => Assert.Equal(HealthJobTypes.Sync, value.Type));
            Assert.Equal(instances.Select(value => value.Id.ToString("D")).Order().ToArray(),
                schedules.Select(value => value.ScopeKey).Order().ToArray());
        }
    }

    [Fact]
    public async Task Request_managers_receive_only_the_supported_health_schedule()
    {
        var connectionString = await fixture.CreateMigratedSchemaAsync();
        var options = new DbContextOptionsBuilder<ArrControlDbContext>()
            .UseNpgsql(connectionString).Options;
        var instances = new[] { "overseerr", "jellyseerr", "ombi" }.Select(kind => new InstanceEntity
        {
            Name = $"{kind} {Guid.NewGuid():N}",
            Kind = kind,
            BaseUrl = $"https://{kind}.example.invalid/",
            Enabled = true,
            TlsVerificationEnabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        }).ToArray();

        await using (var context = new ArrControlDbContext(options))
        {
            context.AddRange(instances);
            await context.SaveChangesAsync();
            Assert.Equal(3, await new EfHealthScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await new EfHealthScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
            Assert.Equal(0, await new EfActivityScheduleProvisioner(context, new FixedTimeProvider(Now))
                .ReconcileAsync(CancellationToken.None));
        }

        await using (var verification = new ArrControlDbContext(options))
        {
            var schedules = await verification.Set<ScheduleEntity>().AsNoTracking().ToArrayAsync();
            Assert.Equal(3, schedules.Length);
            Assert.All(schedules, value => Assert.Equal(HealthJobTypes.Sync, value.Type));
            Assert.Equal(instances.Select(value => value.Id.ToString("D")).Order().ToArray(),
                schedules.Select(value => value.ScopeKey).Order().ToArray());
        }
    }

    private static CredentialEntity Credential(
        Guid instanceId, string purpose, string secret, ICredentialProtector protector)
    {
        using var protectedValue = protector.Protect(instanceId, purpose, secret);
        return new CredentialEntity
        {
            InstanceId = instanceId,
            Purpose = purpose,
            Ciphertext = protectedValue.Ciphertext.ToArray(),
            Nonce = protectedValue.Nonce.ToArray(),
            Tag = protectedValue.Tag.ToArray(),
            KeyVersion = protectedValue.KeyVersion,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
