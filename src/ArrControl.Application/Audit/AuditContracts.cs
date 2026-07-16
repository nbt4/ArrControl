using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArrControl.Application.Authorization;

namespace ArrControl.Application.Audit;

public static class AuditLimits
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
    public const int MaximumExportAuditRows = 10_000;
    public static readonly TimeSpan MaximumQueryRange = TimeSpan.FromDays(366);
}

public sealed record AuditFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? ActorUserId,
    string? Action,
    string? Outcome,
    string? CorrelationId);

public sealed record NormalizedAuditFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? ActorUserId,
    string? Action,
    string? Outcome,
    string? CorrelationId);

public sealed record AuditCursor(
    DateTimeOffset OccurredAt,
    Guid Id,
    string FilterFingerprint,
    NormalizedAuditFilter Filter);

public interface IAuditCursorCodec
{
    string Encode(AuditCursor cursor);
    AuditCursor? Decode(string value);
}

public sealed record AuditEventDetails(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorType,
    string ActorIdentifier,
    string Action,
    JsonElement Scope,
    string CorrelationId,
    string Outcome,
    JsonElement Summary,
    string? IpAddress);

public sealed record AuditPage(
    IReadOnlyList<AuditEventDetails> Items,
    string? NextCursor);

public interface IAuditQueryStore
{
    Task<IReadOnlyList<AuditEventDetails>> QueryAsync(
        NormalizedAuditFilter filter,
        AuditCursor? cursor,
        int fetchCount,
        CancellationToken cancellationToken);
}

public sealed class AuditQueryService(
    RbacAuthorizationService authorization,
    IAuditQueryStore store,
    IAuditCursorCodec cursorCodec,
    TimeProvider timeProvider)
{
    public async Task<AuditQueryResult> QueryAsync(
        Guid userId,
        Guid sessionId,
        AuditFilter filter,
        string? cursorValue,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!await authorization.HasGlobalAsync(
                userId, sessionId, RbacPermissions.AuditRead, cancellationToken))
            return new AuditQueryResult(AuditQueryStatus.Forbidden);
        if (limit is < 1 or > AuditLimits.MaximumPageSize)
            return new AuditQueryResult(AuditQueryStatus.Invalid, ErrorCode: "audit_limit_invalid");

        AuditCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            cursor = cursorCodec.Decode(cursorValue);
            if (cursor is null)
                return new AuditQueryResult(AuditQueryStatus.Invalid, ErrorCode: "audit_cursor_invalid");
            filter = new AuditFilter(
                filter.From ?? cursor.Filter.From,
                filter.To ?? cursor.Filter.To,
                filter.ActorUserId ?? cursor.Filter.ActorUserId,
                filter.Action ?? cursor.Filter.Action,
                filter.Outcome ?? cursor.Filter.Outcome,
                filter.CorrelationId ?? cursor.Filter.CorrelationId);
        }

        NormalizedAuditFilter normalized;
        try { normalized = Normalize(filter, timeProvider.GetUtcNow()); }
        catch (ArgumentException) { return new AuditQueryResult(AuditQueryStatus.Invalid, ErrorCode: "audit_filter_invalid"); }
        var fingerprint = Fingerprint(normalized);
        if (cursor is not null && cursor.FilterFingerprint != fingerprint)
            return new AuditQueryResult(AuditQueryStatus.Invalid, ErrorCode: "audit_cursor_invalid");

        var rows = await store.QueryAsync(normalized, cursor, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).ToArray();
        var next = hasMore && items.Length > 0
            ? cursorCodec.Encode(new AuditCursor(
                items[^1].OccurredAt, items[^1].Id, fingerprint, normalized))
            : null;
        return new AuditQueryResult(AuditQueryStatus.Success, new AuditPage(items, next));
    }

    private static NormalizedAuditFilter Normalize(AuditFilter value, DateTimeOffset now)
    {
        var from = value.From ?? now.AddDays(-30);
        var to = value.To ?? now;
        var action = NormalizeText(value.Action, 160);
        var outcome = NormalizeText(value.Outcome, 32);
        var correlation = NormalizeText(value.CorrelationId, 128);
        if (from >= to || to - from > AuditLimits.MaximumQueryRange || to > now.AddMinutes(5)
            || value.ActorUserId == Guid.Empty)
            throw new ArgumentException("The audit filter is invalid.");
        return new NormalizedAuditFilter(from, to, value.ActorUserId, action, outcome, correlation);
    }

    private static string? NormalizeText(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maximumLength || trimmed.Any(char.IsControl))
            throw new ArgumentException("The audit filter is invalid.");
        return trimmed;
    }

    private static string Fingerprint(NormalizedAuditFilter value)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            from = value.From.ToUniversalTime(),
            to = value.To.ToUniversalTime(),
            value.ActorUserId,
            value.Action,
            value.Outcome,
            value.CorrelationId,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public enum AuditQueryStatus { Success, Forbidden, Invalid }

public sealed record AuditQueryResult(
    AuditQueryStatus Status,
    AuditPage? Page = null,
    string? ErrorCode = null);
