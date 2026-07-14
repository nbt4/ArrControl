using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Persistence;

public sealed class ArrControlDbContext(DbContextOptions<ArrControlDbContext> options) : DbContext(options)
{
    public DbSet<InstanceEntity> Instances => Set<InstanceEntity>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<InstanceEntity>(entity => { entity.ToTable("service_instances"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.Name).IsUnique(); entity.Property(x => x.Name).HasMaxLength(120); entity.Property(x => x.BaseUrl).HasMaxLength(2048); });
    }
}

public sealed class InstanceEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string BaseUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
