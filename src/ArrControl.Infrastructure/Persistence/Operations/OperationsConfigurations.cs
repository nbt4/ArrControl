using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using ArrControl.Application.Operations;

namespace ArrControl.Infrastructure.Persistence.Operations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    public void Configure(EntityTypeBuilder<AuditEventEntity> entity)
    {
        entity.ToTable("audit_events");
        entity.HasKey(x => new { x.OccurredAt, x.Id }).HasName("pk_audit_events");
        entity.Property(x => x.OccurredAt).HasDefaultValueSql("now()");
        entity.Property(x => x.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(x => x.ActorIdentifier).HasMaxLength(320).IsRequired();
        entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
        entity.Property(x => x.ScopeJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
        entity.Property(x => x.SummaryJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.IpAddress).HasColumnType("inet");

        entity.HasIndex(x => new { x.ActorUserId, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_actor_user_id_occurred_at");
        entity.HasIndex(x => new { x.Action, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_action_occurred_at");
        entity.HasIndex(x => new { x.Outcome, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_outcome_occurred_at");
        entity.HasIndex(x => new { x.ActorIdentifier, x.Action, x.Outcome, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_login_account_throttle");
        entity.HasIndex(x => new { x.IpAddress, x.Action, x.Outcome, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_login_ip_throttle");
        entity.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_audit_events_correlation_id");

        entity.HasOne(x => x.ActorUser)
            .WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_audit_events_users_actor_user_id");
    }
}

internal sealed class OperationConfiguration : IEntityTypeConfiguration<OperationEntity>
{
    public void Configure(EntityTypeBuilder<OperationEntity> entity)
    {
        entity.ToTable("operations", table => table.HasCheckConstraint(
            "ck_operations_state",
            "state IN ('pending','running','succeeded','partial','failed','cancelled')"));
        entity.HasKey(x => x.Id).HasName("pk_operations");
        entity.Property(x => x.Type).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Route).HasMaxLength(200).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        entity.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(x => x.State).HasMaxLength(32).IsRequired();
        entity.HasIndex(x => new { x.ActorUserId, x.Route, x.IdempotencyKey })
            .IsUnique().HasDatabaseName("ux_operations_actor_route_idempotency_key");
        entity.HasIndex(x => new { x.State, x.CreatedAt })
            .HasDatabaseName("ix_operations_state_created_at");
        entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_operations_users_actor_user_id");
    }
}

internal sealed class OperationTargetConfiguration : IEntityTypeConfiguration<OperationTargetEntity>
{
    public void Configure(EntityTypeBuilder<OperationTargetEntity> entity)
    {
        entity.ToTable("operation_targets", table => table.HasCheckConstraint(
            "ck_operation_targets_state",
            "state IN ('pending','running','succeeded','failed','cancelled','skipped')"));
        entity.HasKey(x => new { x.OperationId, x.InstanceId, x.TargetKey })
            .HasName("pk_operation_targets");
        entity.Property(x => x.TargetKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.State).HasMaxLength(32).IsRequired();
        entity.Property(x => x.ErrorCode).HasMaxLength(128);
        entity.Property(x => x.ResultJson).HasColumnType("jsonb");
        entity.HasIndex(x => new { x.InstanceId, x.State })
            .HasDatabaseName("ix_operation_targets_instance_id_state");
        entity.HasOne(x => x.Operation).WithMany(x => x.Targets).HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_operation_targets_operations_operation_id");
        entity.HasOne<InstanceEntity>().WithMany().HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_operation_targets_service_instances_instance_id");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessageEntity>
{
    public void Configure(EntityTypeBuilder<OutboxMessageEntity> entity)
    {
        entity.ToTable(
            "outbox_messages",
            table => table.HasCheckConstraint(
                "ck_outbox_messages_attempt_count_nonnegative",
                "attempt_count >= 0"));
        entity.HasKey(x => x.Id).HasName("pk_outbox_messages");
        entity.Property(x => x.Type).HasMaxLength(160).IsRequired();
        entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.OccurredAt).HasDefaultValueSql("now()");
        entity.Property(x => x.AttemptCount).HasDefaultValue(0);
        entity.Property(x => x.LastErrorCode).HasMaxLength(128);

        entity.HasIndex(x => new { x.NextAttemptAt, x.OccurredAt })
            .HasFilter("published_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_unpublished");
    }
}
