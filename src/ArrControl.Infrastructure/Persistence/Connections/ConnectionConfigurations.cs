using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArrControl.Infrastructure.Persistence.Connections;

internal sealed class InstanceGroupConfiguration : IEntityTypeConfiguration<InstanceGroupEntity>
{
    public void Configure(EntityTypeBuilder<InstanceGroupEntity> entity)
    {
        entity.ToTable("instance_groups");
        entity.HasKey(x => x.Id).HasName("pk_instance_groups");
        entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_instance_groups_name");
    }
}

internal sealed class InstanceConfiguration : IEntityTypeConfiguration<InstanceEntity>
{
    public void Configure(EntityTypeBuilder<InstanceEntity> entity)
    {
        entity.ToTable("service_instances");
        entity.HasKey(x => x.Id).HasName("pk_service_instances");
        entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
        entity.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        entity.Property(x => x.BaseUrl).HasMaxLength(2048).IsRequired();
        entity.Property(x => x.Enabled).HasDefaultValue(true);
        entity.Property(x => x.TlsVerificationEnabled).HasDefaultValue(true);
        entity.Property(x => x.AllowPrivateNetworkAccess).HasDefaultValue(false);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        entity.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_service_instances_name");
        entity.HasIndex(x => x.GroupId).HasDatabaseName("ix_service_instances_group_id");
        entity.HasOne(x => x.Group)
            .WithMany(x => x.Instances)
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_service_instances_instance_groups_group_id");
    }
}

internal sealed class CredentialConfiguration : IEntityTypeConfiguration<CredentialEntity>
{
    public void Configure(EntityTypeBuilder<CredentialEntity> entity)
    {
        entity.ToTable(
            "credentials",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_credentials_ciphertext_not_empty",
                    "octet_length(ciphertext) > 0");
                table.HasCheckConstraint("ck_credentials_nonce_length", "octet_length(nonce) = 12");
                table.HasCheckConstraint("ck_credentials_tag_length", "octet_length(tag) = 16");
                table.HasCheckConstraint("ck_credentials_key_version", "key_version > 0");
            });
        entity.HasKey(x => x.Id).HasName("pk_credentials");
        entity.Property(x => x.Purpose).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Ciphertext).IsRequired();
        entity.Property(x => x.Nonce).IsRequired();
        entity.Property(x => x.Tag).IsRequired();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        entity.HasIndex(x => new { x.InstanceId, x.Purpose })
            .IsUnique()
            .HasDatabaseName("ux_credentials_instance_id_purpose");
        entity.HasOne(x => x.Instance)
            .WithMany(x => x.Credentials)
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_credentials_service_instances_instance_id");
    }
}

internal sealed class ProviderCapabilityConfiguration : IEntityTypeConfiguration<ProviderCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<ProviderCapabilityEntity> entity)
    {
        entity.ToTable("provider_capabilities");
        entity.HasKey(x => new { x.InstanceId, x.Capability }).HasName("pk_provider_capabilities");
        entity.Property(x => x.Capability).HasMaxLength(64).IsRequired();
        entity.Property(x => x.ObservedAt).HasDefaultValueSql("now()");
        entity.HasOne(x => x.Instance)
            .WithMany(x => x.Capabilities)
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_provider_capabilities_service_instances_instance_id");
    }
}
