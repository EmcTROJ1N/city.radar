using System.Net;

using BuildingBlocks.Persistence;

using IdentityAccess.Core.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IdentityAccess.Core.Persistence;

/// <summary>
/// Модель Identity &amp; Access для единого <see cref="MayakDbContext"/>: таблицы
/// <c>users</c>, <c>roles</c>, <c>user_roles</c>, <c>role_claims</c>, <c>refresh_tokens</c>,
/// <c>audit_entries</c>, <c>global_settings</c>. Имена таблиц, колонок (snake_case), ключей,
/// индексов и типы колонок сверены с <c>db/schema.sql</c>.
/// </summary>
public sealed class IdentityModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new AppUserConfiguration());
        modelBuilder.ApplyConfiguration(new AppRoleConfiguration());
        modelBuilder.ApplyConfiguration(new UserRoleConfiguration());
        modelBuilder.ApplyConfiguration(new RoleClaimConfiguration());
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new GlobalSettingConfiguration());
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("users");

        builder.HasKey(user => user.Id).HasName("users_pkey");

        builder.Property(user => user.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(user => user.UserName).HasColumnType("text").IsRequired();
        builder.Property(user => user.NormalizedUserName).HasColumnType("text").IsRequired();
        builder.Property(user => user.SecurityStamp).HasColumnType("text");
        builder.Property(user => user.ConcurrencyStamp).HasColumnType("text");
        builder.Property(user => user.MaxUserId).HasColumnType("bigint").IsRequired();
        builder.Property(user => user.DisplayName).HasColumnType("text").IsRequired();
        builder.Property(user => user.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(user => user.LastLoginAt).HasColumnType("timestamptz");

        builder.HasAlternateKey(user => user.NormalizedUserName).HasName("users_normalized_user_name_key");
        builder.HasAlternateKey(user => user.MaxUserId).HasName("users_max_user_id_key");
    }
}

internal sealed class AppRoleConfiguration : IEntityTypeConfiguration<AppRole>
{
    public void Configure(EntityTypeBuilder<AppRole> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(role => role.Id).HasName("roles_pkey");

        builder.Property(role => role.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(role => role.Name).HasColumnType("text").IsRequired();
        builder.Property(role => role.NormalizedName).HasColumnType("text").IsRequired();
        builder.Property(role => role.ConcurrencyStamp).HasColumnType("text");

        builder.HasAlternateKey(role => role.NormalizedName).HasName("roles_normalized_name_key");
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");

        builder.HasKey(userRole => new { userRole.UserId, userRole.RoleId }).HasName("user_roles_pkey");

        builder.Property(userRole => userRole.UserId).HasColumnType("uuid");
        builder.Property(userRole => userRole.RoleId).HasColumnType("uuid");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AppRole>()
            .WithMany()
            .HasForeignKey(userRole => userRole.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleClaimConfiguration : IEntityTypeConfiguration<RoleClaim>
{
    public void Configure(EntityTypeBuilder<RoleClaim> builder)
    {
        builder.ToTable("role_claims");

        builder.HasKey(claim => claim.Id).HasName("role_claims_pkey");

        builder.Property(claim => claim.Id)
            .HasColumnType("integer")
            .UseIdentityAlwaysColumn();
        builder.Property(claim => claim.RoleId).HasColumnType("uuid");
        builder.Property(claim => claim.ClaimType)
            .HasColumnType("text")
            .HasDefaultValue("permission")
            .IsRequired();
        builder.Property(claim => claim.ClaimValue).HasColumnType("text").IsRequired();

        builder.HasAlternateKey(claim => new { claim.RoleId, claim.ClaimType, claim.ClaimValue })
            .HasName("role_claims_role_id_claim_type_claim_value_key");

        builder.HasOne<AppRole>()
            .WithMany()
            .HasForeignKey(claim => claim.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(token => token.Id).HasName("refresh_tokens_pkey");

        builder.Property(token => token.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(token => token.UserId).HasColumnType("uuid");
        builder.Property(token => token.TokenHash).HasColumnType("bytea").IsRequired();
        builder.Property(token => token.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(token => token.RevokedAt).HasColumnType("timestamptz");

        builder.HasAlternateKey(token => token.TokenHash).HasName("refresh_tokens_token_hash_key");
        builder.HasIndex(token => token.UserId).HasDatabaseName("refresh_tokens_user_idx");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");

        builder.HasKey(entry => entry.Id).HasName("audit_entries_pkey");

        builder.Property(entry => entry.Id)
            .HasColumnType("bigint")
            .UseIdentityAlwaysColumn();
        builder.Property(entry => entry.ActorId).HasColumnType("uuid");
        builder.Property(entry => entry.Action).HasColumnType("text").IsRequired();
        builder.Property(entry => entry.EntityType).HasColumnType("text");
        builder.Property(entry => entry.EntityId).HasColumnType("text");
        builder.Property(entry => entry.Before).HasColumnType("jsonb");
        builder.Property(entry => entry.After).HasColumnType("jsonb");
        builder.Property(entry => entry.Reason).HasColumnType("text");
        builder.Property(entry => entry.Ip).HasColumnType("inet");
        builder.Property(entry => entry.UserAgent).HasColumnType("text");
        builder.Property(entry => entry.At)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasIndex(entry => entry.At)
            .IsDescending()
            .HasDatabaseName("audit_at_idx");
        builder.HasIndex(entry => new { entry.ActorId, entry.At })
            .IsDescending(false, true)
            .HasDatabaseName("audit_actor_idx");
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId })
            .HasDatabaseName("audit_entity_idx");

        // Ссылка на актора не каскадная: журнал нельзя чистить вместе с пользователем.
        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(entry => entry.ActorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GlobalSettingConfiguration : IEntityTypeConfiguration<GlobalSetting>
{
    public void Configure(EntityTypeBuilder<GlobalSetting> builder)
    {
        builder.ToTable("global_settings");

        builder.HasKey(setting => setting.Key).HasName("global_settings_pkey");

        builder.Property(setting => setting.Key).HasColumnType("text");
        builder.Property(setting => setting.Value).HasColumnType("jsonb").IsRequired();
        builder.Property(setting => setting.UpdatedBy).HasColumnType("uuid");
        builder.Property(setting => setting.UpdatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(setting => setting.UpdatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
