namespace ArrControl.Infrastructure.Persistence.Connections;

public sealed class InstanceGroupEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<InstanceEntity> Instances { get; } = new List<InstanceEntity>();
}

public sealed class InstanceEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? GroupId { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string BaseUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public bool TlsVerificationEnabled { get; set; } = true;
    public bool AllowPrivateNetworkAccess { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public InstanceGroupEntity? Group { get; set; }
    public ICollection<CredentialEntity> Credentials { get; } = new List<CredentialEntity>();
    public ICollection<ProviderCapabilityEntity> Capabilities { get; } = new List<ProviderCapabilityEntity>();
}

public sealed class CredentialEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid InstanceId { get; set; }
    public required string Purpose { get; set; }
    public required byte[] Ciphertext { get; set; }
    public required byte[] Nonce { get; set; }
    public required byte[] Tag { get; set; }
    public int KeyVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public InstanceEntity Instance { get; set; } = null!;
}

public sealed class ProviderCapabilityEntity
{
    public Guid InstanceId { get; set; }
    public required string Capability { get; set; }
    public bool Supported { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public InstanceEntity Instance { get; set; } = null!;
}
