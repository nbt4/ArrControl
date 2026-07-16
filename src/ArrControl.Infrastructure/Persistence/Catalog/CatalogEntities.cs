namespace ArrControl.Infrastructure.Persistence.Catalog;

public sealed class ProviderItemEntity
{
    public Guid InstanceId { get; set; }
    public required string ProviderKey { get; set; }
    public required string ProviderKind { get; set; }
    public required string RawKind { get; set; }
    public string? ParentProviderKey { get; set; }
    public required string ProviderDataJson { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public MediaEntityEntity MediaEntity { get; set; } = null!;
    public MissingItemEntity? MissingItem { get; set; }
}

public sealed class MissingItemEntity
{
    public Guid InstanceId { get; set; }
    public required string ProviderKey { get; set; }
    public required string Reason { get; set; }
    public bool Monitored { get; set; } = true;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ProviderItemEntity ProviderItem { get; set; } = null!;
}

public sealed class MissingSavedViewEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string FilterJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MediaEntityEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid InstanceId { get; set; }
    public required string ProviderKey { get; set; }
    public required string CanonicalKind { get; set; }
    public required string Title { get; set; }
    public string SortTitle { get; private set; } = null!;
    public int? Year { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public bool Monitored { get; set; }
    public bool? HasFile { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }
    public DateTimeOffset? SourceAddedAt { get; set; }
    public required string ExternalIdsJson { get; set; }
    public ProviderItemEntity ProviderItem { get; set; } = null!;
}
