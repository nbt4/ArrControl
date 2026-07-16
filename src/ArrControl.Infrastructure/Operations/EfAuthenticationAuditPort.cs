using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArrControl.Application.Identity;
using ArrControl.Infrastructure.Persistence;
using ArrControl.Infrastructure.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ArrControl.Infrastructure.Operations;

public sealed class EfAuthenticationAuditPort(ArrControlDbContext dbContext) : IAuthenticationAuditPort
{
    private const string AuthenticationScopeJson = "{\"kind\":\"authentication\"}";
    private const string LocalMethodSummaryJson = "{\"method\":\"local\"}";
    private const string OidcMethodSummaryJson = "{\"method\":\"oidc\"}";

    public async Task<ILoginThrottleLease> AcquireLoginThrottleAsync(
        string actorIdentifier,
        IPAddress? ipAddress,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            var lockKeys = new List<long>
            {
                CreateLockKey(1, actorIdentifier),
            };
            if (ipAddress is not null)
            {
                lockKeys.Add(CreateLockKey(2, ipAddress.ToString()));
            }

            foreach (var lockKey in lockKeys.Distinct().Order())
            {
                if (!await TryAcquireTransactionLockAsync(
                        transaction,
                        lockKey,
                        cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    return UnavailableLoginThrottleLease.Instance;
                }
            }

            var accountFailures = await dbContext.Set<AuditEventEntity>()
                .AsNoTracking()
                .CountAsync(
                    x => x.Action == "identity.login"
                        && x.Outcome == "failed"
                        && x.ActorIdentifier == actorIdentifier
                        && x.OccurredAt >= since,
                    cancellationToken);
            var ipFailures = ipAddress is null
                ? 0
                : await dbContext.Set<AuditEventEntity>()
                    .AsNoTracking()
                    .CountAsync(
                        x => x.Action == "identity.login"
                            && x.Outcome == "failed"
                            && x.IpAddress == ipAddress
                            && x.OccurredAt >= since,
                        cancellationToken);
            return new EfLoginThrottleLease(
                transaction,
                new LoginFailureCounts(accountFailures, ipFailures));
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public void Stage(AuthenticationAuditEvent auditEvent)
    {
        dbContext.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = auditEvent.OccurredAt,
            ActorUserId = auditEvent.ActorUserId,
            ActorType = auditEvent.ActorType,
            ActorIdentifier = auditEvent.ActorIdentifier,
            Action = auditEvent.Action,
            ScopeJson = AuthenticationScopeJson,
            CorrelationId = auditEvent.CorrelationId,
            Outcome = auditEvent.Outcome,
            SummaryJson = auditEvent.AuthenticationMethod switch
            {
                LocalIdentityConstants.LocalAuthenticationMethod => LocalMethodSummaryJson,
                LocalIdentityConstants.OidcAuthenticationMethod => OidcMethodSummaryJson,
                _ => throw new InvalidOperationException("The authentication audit method is invalid."),
            },
            IpAddress = auditEvent.IpAddress,
        });
    }

    private async Task<bool> TryAcquireTransactionLockAsync(
        IDbContextTransaction transaction,
        long lockKey,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(@lock_key)";
        command.Parameters.AddWithValue("lock_key", lockKey);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static long CreateLockKey(byte lockNamespace, string identifier)
    {
        var identifierBytes = Encoding.UTF8.GetBytes(identifier);
        var input = GC.AllocateUninitializedArray<byte>(identifierBytes.Length + 1);
        try
        {
            input[0] = lockNamespace;
            identifierBytes.CopyTo(input, 1);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(input, hash);
            return BinaryPrimitives.ReadInt64BigEndian(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identifierBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private sealed class EfLoginThrottleLease(
        IDbContextTransaction transaction,
        LoginFailureCounts failureCounts) : ILoginThrottleLease
    {
        private bool completed;

        public bool Acquired => true;

        public LoginFailureCounts FailureCounts { get; } = failureCounts;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (completed)
            {
                return;
            }

            await transaction.CommitAsync(cancellationToken);
            completed = true;
        }

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed class UnavailableLoginThrottleLease : ILoginThrottleLease
    {
        public static UnavailableLoginThrottleLease Instance { get; } = new();

        public bool Acquired => false;

        public LoginFailureCounts FailureCounts { get; } = new(0, 0);

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
