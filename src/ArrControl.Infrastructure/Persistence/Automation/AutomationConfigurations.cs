using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArrControl.Infrastructure.Persistence.Automation;

internal sealed class ScheduleConfiguration : IEntityTypeConfiguration<ScheduleEntity>
{
    public void Configure(EntityTypeBuilder<ScheduleEntity> entity)
    {
        entity.ToTable("schedules");
        entity.HasKey(x => x.Id).HasName("pk_schedules");
        entity.Property(x => x.Type).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Cron).HasMaxLength(160).IsRequired();
        entity.Property(x => x.TimeZone).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ScopeJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.ScopeKey).HasMaxLength(200);
        entity.Property(x => x.Enabled).HasDefaultValue(true);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => new { x.Type, x.ScopeKey })
            .IsUnique()
            .HasFilter("scope_key IS NOT NULL")
            .HasDatabaseName("ux_schedules_type_scope_key");
    }
}

internal sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRunEntity>
{
    public void Configure(EntityTypeBuilder<JobRunEntity> entity)
    {
        entity.ToTable(
            "job_runs",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_job_runs_attempts_nonnegative",
                    "attempts >= 0");
                table.HasCheckConstraint(
                    "ck_job_runs_lease_pair",
                    "(lease_owner IS NULL AND lease_until IS NULL AND lease_token IS NULL AND last_heartbeat_at IS NULL) OR " +
                    "(lease_owner IS NOT NULL AND lease_until IS NOT NULL AND lease_token IS NOT NULL AND last_heartbeat_at IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_job_runs_state",
                    "state IN ('pending', 'running', 'retry', 'succeeded', 'failed')");
                table.HasCheckConstraint(
                    "ck_job_runs_state_lease",
                    "(state = 'running') = (lease_token IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_job_runs_state_completion",
                    "(state IN ('succeeded', 'failed')) = (completed_at IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_job_runs_available_order",
                    "available_at >= scheduled_for");
                table.HasCheckConstraint(
                    "ck_job_runs_completion_order",
                    "completed_at IS NULL OR " +
                    "(started_at IS NOT NULL AND completed_at >= started_at)");
            });
        entity.HasKey(x => x.Id).HasName("pk_job_runs");
        entity.Property(x => x.State).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Attempts).HasDefaultValue(0);
        entity.Property(x => x.LeaseOwner).HasMaxLength(200);
        entity.Property(x => x.ErrorCode).HasMaxLength(128);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        entity.HasIndex(x => new { x.ScheduleId, x.ScheduledFor })
            .IsUnique()
            .HasDatabaseName("ux_job_runs_schedule_id_scheduled_for");
        entity.HasIndex(x => new { x.State, x.AvailableAt, x.LeaseUntil })
            .HasFilter("completed_at IS NULL")
            .HasDatabaseName("ix_job_runs_claim");

        entity.HasOne(x => x.Schedule)
            .WithMany(x => x.JobRuns)
            .HasForeignKey(x => x.ScheduleId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_job_runs_schedules_schedule_id");
    }
}

internal sealed class SyncCheckpointConfiguration : IEntityTypeConfiguration<SyncCheckpointEntity>
{
    public void Configure(EntityTypeBuilder<SyncCheckpointEntity> entity)
    {
        entity.ToTable("sync_checkpoints");
        entity.HasKey(x => new { x.InstanceId, x.Stream }).HasName("pk_sync_checkpoints");
        entity.Property(x => x.Stream).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Cursor).HasMaxLength(4096);
        entity.HasOne<InstanceEntity>()
            .WithMany()
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_sync_checkpoints_service_instances_instance_id");
    }
}
