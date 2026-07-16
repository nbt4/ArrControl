using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArrControl.Infrastructure.Persistence.Activity;

internal sealed class QueueItemConfiguration : IEntityTypeConfiguration<QueueItemEntity>
{
    public void Configure(EntityTypeBuilder<QueueItemEntity> entity)
    {
        entity.ToTable("queue_items", table =>
        {
            table.HasCheckConstraint("ck_queue_items_size", "size_bytes >= 0");
            table.HasCheckConstraint("ck_queue_items_remaining", "remaining_bytes >= 0");
        });
        entity.HasKey(x => new { x.InstanceId, x.ProviderKey }).HasName("pk_queue_items");
        ConfigureCommon(entity);
        entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
        entity.Property(x => x.TrackedStatus).HasMaxLength(64).IsRequired();
        entity.Property(x => x.TrackedState).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Protocol).HasMaxLength(32);
        entity.Property(x => x.DownloadClient).HasMaxLength(200);
        entity.Property(x => x.Indexer).HasMaxLength(200);
        entity.HasIndex(x => new { x.InstanceId, x.CorrelationKey })
            .HasDatabaseName("ix_queue_items_instance_id_correlation_key");
        entity.HasOne<InstanceEntity>().WithMany().HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_queue_items_service_instances_instance_id");
    }

    private static void ConfigureCommon(EntityTypeBuilder<QueueItemEntity> entity)
    {
        entity.Property(x => x.ProviderKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.ProviderKind).HasMaxLength(64).IsRequired();
        entity.Property(x => x.MediaProviderKey).HasMaxLength(200);
        entity.Property(x => x.DownloadId).HasMaxLength(512);
        entity.Property(x => x.CorrelationKey).HasMaxLength(64).IsFixedLength();
        entity.Property(x => x.Title).HasMaxLength(1000).IsRequired();
    }
}

internal sealed class HistoryItemConfiguration : IEntityTypeConfiguration<HistoryItemEntity>
{
    public void Configure(EntityTypeBuilder<HistoryItemEntity> entity)
    {
        entity.ToTable("history_items");
        entity.HasKey(x => new { x.InstanceId, x.ProviderKey }).HasName("pk_history_items");
        entity.Property(x => x.ProviderKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.ProviderKind).HasMaxLength(64).IsRequired();
        entity.Property(x => x.MediaProviderKey).HasMaxLength(200);
        entity.Property(x => x.DownloadId).HasMaxLength(512);
        entity.Property(x => x.CorrelationKey).HasMaxLength(64).IsFixedLength();
        entity.Property(x => x.Title).HasMaxLength(1000).IsRequired();
        entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
        entity.HasIndex(x => new { x.InstanceId, x.EventAt })
            .HasDatabaseName("ix_history_items_instance_id_event_at");
        entity.HasIndex(x => new { x.InstanceId, x.CorrelationKey, x.EventAt })
            .HasDatabaseName("ix_history_items_instance_id_correlation_key_event_at");
        entity.HasOne<InstanceEntity>().WithMany().HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_history_items_service_instances_instance_id");
    }
}
