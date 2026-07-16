using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArrControl.Infrastructure.Persistence.Health;

internal sealed class HealthIncidentConfiguration : IEntityTypeConfiguration<HealthIncidentEntity>
{
    public void Configure(EntityTypeBuilder<HealthIncidentEntity> entity)
    {
        entity.ToTable("health_incidents", table =>
        {
            table.HasCheckConstraint("ck_health_incidents_severity",
                "severity IN ('ok','notice','warning','error','unknown')");
            table.HasCheckConstraint("ck_health_incidents_seen_order", "last_seen_at >= first_seen_at");
            table.HasCheckConstraint("ck_health_incidents_resolution_order",
                "resolved_at IS NULL OR resolved_at >= first_seen_at");
        });
        entity.HasKey(x => x.Id).HasName("pk_health_incidents");
        entity.Property(x => x.GroupKey).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(x => x.ProviderKind).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Severity).HasMaxLength(16).IsRequired();
        entity.Property(x => x.RemediationUrl).HasMaxLength(2048);
        entity.HasIndex(x => new { x.InstanceId, x.GroupKey }).IsUnique()
            .HasDatabaseName("ux_health_incidents_instance_id_group_key");
        entity.HasIndex(x => new { x.InstanceId, x.ResolvedAt, x.LastSeenAt })
            .HasDatabaseName("ix_health_incidents_instance_state_seen");
        entity.HasIndex(x => x.AcknowledgedByUserId)
            .HasDatabaseName("ix_health_incidents_acknowledged_by_user_id");
        entity.HasIndex(x => x.SnoozedByUserId)
            .HasDatabaseName("ix_health_incidents_snoozed_by_user_id");
        entity.HasOne<InstanceEntity>().WithMany().HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_health_incidents_service_instances_instance_id");
        entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.AcknowledgedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_health_incidents_users_acknowledged_by_user_id");
        entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.SnoozedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_health_incidents_users_snoozed_by_user_id");
    }
}

internal sealed class HealthIncidentSourceConfiguration : IEntityTypeConfiguration<HealthIncidentSourceEntity>
{
    public void Configure(EntityTypeBuilder<HealthIncidentSourceEntity> entity)
    {
        entity.ToTable("health_incident_sources", table =>
        {
            table.HasCheckConstraint("ck_health_incident_sources_severity",
                "severity IN ('ok','notice','warning','error','unknown')");
            table.HasCheckConstraint("ck_health_incident_sources_seen_order", "last_seen_at >= first_seen_at");
        });
        entity.HasKey(x => new { x.IncidentId, x.SourceKey }).HasName("pk_health_incident_sources");
        entity.Property(x => x.SourceKey).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(x => x.Source).HasMaxLength(300).IsRequired();
        entity.Property(x => x.Severity).HasMaxLength(16).IsRequired();
        entity.Property(x => x.Message).HasMaxLength(4000);
        entity.Property(x => x.RemediationUrl).HasMaxLength(2048);
        entity.HasIndex(x => new { x.IncidentId, x.Active })
            .HasDatabaseName("ix_health_incident_sources_incident_id_active");
        entity.HasOne(x => x.Incident).WithMany(x => x.Sources).HasForeignKey(x => x.IncidentId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_health_incident_sources_health_incidents_incident_id");
    }
}
