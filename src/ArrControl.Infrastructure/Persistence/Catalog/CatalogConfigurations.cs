using ArrControl.Infrastructure.Persistence.Connections;
using ArrControl.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArrControl.Infrastructure.Persistence.Catalog;

internal sealed class ProviderItemConfiguration : IEntityTypeConfiguration<ProviderItemEntity>
{
    public void Configure(EntityTypeBuilder<ProviderItemEntity> entity)
    {
        entity.ToTable("provider_items");
        entity.HasKey(x => new { x.InstanceId, x.ProviderKey }).HasName("pk_provider_items");
        entity.Property(x => x.ProviderKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.ProviderKind).HasMaxLength(64).IsRequired();
        entity.Property(x => x.RawKind).HasMaxLength(32).IsRequired();
        entity.Property(x => x.ParentProviderKey).HasMaxLength(200);
        entity.Property(x => x.ProviderDataJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.Fingerprint).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.HasIndex(x => new { x.InstanceId, x.RawKind })
            .HasDatabaseName("ix_provider_items_instance_id_raw_kind");
        entity.HasOne<InstanceEntity>()
            .WithMany()
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_provider_items_service_instances_instance_id");
    }
}

internal sealed class MissingItemConfiguration : IEntityTypeConfiguration<MissingItemEntity>
{
    public void Configure(EntityTypeBuilder<MissingItemEntity> entity)
    {
        entity.ToTable(
            "missing_items",
            table => table.HasCheckConstraint(
                "ck_missing_items_reason",
                "reason IN ('missing', 'not_available')"));
        entity.HasKey(x => new { x.InstanceId, x.ProviderKey }).HasName("pk_missing_items");
        entity.Property(x => x.ProviderKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Reason).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Monitored).HasDefaultValue(true);
        entity.HasIndex(x => new { x.Reason, x.UpdatedAt })
            .HasDatabaseName("ix_missing_items_reason_updated_at");
        entity.HasOne(x => x.ProviderItem)
            .WithOne(x => x.MissingItem)
            .HasForeignKey<MissingItemEntity>(x => new { x.InstanceId, x.ProviderKey })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_missing_items_provider_items_instance_provider_key");
    }
}

internal sealed class MissingSavedViewConfiguration : IEntityTypeConfiguration<MissingSavedViewEntity>
{
    public void Configure(EntityTypeBuilder<MissingSavedViewEntity> entity)
    {
        entity.ToTable("missing_saved_views");
        entity.HasKey(x => x.Id).HasName("pk_missing_saved_views");
        entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
        entity.Property(x => x.NormalizedName).HasMaxLength(120).IsRequired();
        entity.Property(x => x.FilterJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => new { x.UserId, x.NormalizedName })
            .IsUnique()
            .HasDatabaseName("ux_missing_saved_views_user_id_name");
        entity.HasOne<UserEntity>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_missing_saved_views_users_user_id");
    }
}

internal sealed class MediaEntityConfiguration : IEntityTypeConfiguration<MediaEntityEntity>
{
    public void Configure(EntityTypeBuilder<MediaEntityEntity> entity)
    {
        entity.ToTable(
            "media_entities",
            table =>
            {
                table.HasCheckConstraint("ck_media_entities_year", "year IS NULL OR year > 0");
                table.HasCheckConstraint(
                    "ck_media_entities_season_number",
                    "season_number IS NULL OR season_number >= 0");
                table.HasCheckConstraint(
                    "ck_media_entities_episode_number",
                    "episode_number IS NULL OR episode_number >= 0");
            });
        entity.HasKey(x => x.Id).HasName("pk_media_entities");
        entity.Property(x => x.ProviderKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.CanonicalKind).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Title).HasMaxLength(1000).IsRequired();
        entity.Property(x => x.SortTitle)
            .HasMaxLength(1000)
            .HasComputedColumnSql("lower(title)", stored: true);
        entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
        entity.Property(x => x.ExternalIdsJson).HasColumnType("jsonb").IsRequired();
        entity.HasIndex(x => new { x.InstanceId, x.ProviderKey })
            .IsUnique()
            .HasDatabaseName("ux_media_entities_instance_id_provider_key");
        entity.HasIndex(x => new { x.SortTitle, x.Id })
            .HasDatabaseName("ix_media_entities_sort_title_id");
        entity.HasIndex(x => new
            {
                x.InstanceId,
                x.CanonicalKind,
                x.Monitored,
                x.HasFile,
                x.AvailableAt,
            })
            .HasDatabaseName("ix_media_entities_missing_projection");
        entity.HasOne(x => x.ProviderItem)
            .WithOne(x => x.MediaEntity)
            .HasForeignKey<MediaEntityEntity>(x => new { x.InstanceId, x.ProviderKey })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_media_entities_provider_items_instance_provider_key");
    }
}
