using System.Net;

namespace ArrControl.Application.Identity;

public sealed record AuthenticationAuditEvent(
    Guid? ActorUserId,
    string ActorType,
    string ActorIdentifier,
    string Action,
    string Outcome,
    string AuthenticationMethod,
    string CorrelationId,
    IPAddress? IpAddress,
    DateTimeOffset OccurredAt);

public interface ILoginThrottleLease : IAsyncDisposable
{
    bool Acquired { get; }

    LoginFailureCounts FailureCounts { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IAuthenticationAuditPort
{
    Task<ILoginThrottleLease> AcquireLoginThrottleAsync(
        string actorIdentifier,
        IPAddress? ipAddress,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    void Stage(AuthenticationAuditEvent auditEvent);
}
