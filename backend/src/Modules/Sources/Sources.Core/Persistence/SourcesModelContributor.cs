using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sources.Core.Domain;

namespace Sources.Core.Persistence;

/// <summary>
/// Модель источников для единого <see cref="MayakDbContext"/>: таблицы <c>source_types</c>,
/// <c>sources</c>, <c>source_policies</c>, <c>source_statuses</c> и <c>secrets</c>. Имена таблиц,
/// колонок (snake_case), ключей и типы колонок сверены с <c>db/schema.sql</c>; физический DDL —
/// миграции <c>Sources</c>, <c>SourceStatuses</c> и <c>Secrets</c> (написаны вручную по эталону).
/// </summary>
public sealed class SourcesModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new SourceTypeConfiguration());
        modelBuilder.ApplyConfiguration(new SourceConfiguration());
        modelBuilder.ApplyConfiguration(new SourcePolicyConfiguration());
        modelBuilder.ApplyConfiguration(new SourceStatusConfiguration());
        modelBuilder.ApplyConfiguration(new SecretConfiguration());
    }
}

internal sealed class SourceTypeConfiguration : IEntityTypeConfiguration<SourceType>
{
    public void Configure(EntityTypeBuilder<SourceType> builder)
    {
        builder.ToTable("source_types");

        builder.HasKey(sourceType => sourceType.Code).HasName("source_types_pkey");

        builder.Property(sourceType => sourceType.Code).HasColumnType("text");
        builder.Property(sourceType => sourceType.Name).HasColumnType("text").IsRequired();
        builder.Property(sourceType => sourceType.IsEnabled)
            .HasColumnType("boolean")
            .HasDefaultValue(true);
    }
}

/// <summary>
/// Таблица <c>sources</c>. Колонки <c>telegram_session_id</c> и <c>proxy_id</c> ссылаются на
/// <c>secrets</c> и добавлены миграцией <c>Secrets</c>.
/// </summary>
internal sealed class SourceConfiguration : IEntityTypeConfiguration<Source>
{
    public void Configure(EntityTypeBuilder<Source> builder)
    {
        builder.ToTable("sources");

        builder.HasKey(source => source.Id).HasName("sources_pkey");

        builder.Property(source => source.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(source => source.Name).HasColumnType("text").IsRequired();
        builder.Property(source => source.SourceType).HasColumnType("text").IsRequired();
        builder.Property(source => source.CategoryId).HasColumnType("uuid");
        builder.Property(source => source.Settings)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'{}'");
        builder.Property(source => source.UserPrompt)
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue(string.Empty);
        builder.Property(source => source.LinkByText)
            .HasColumnType("boolean")
            .HasDefaultValue(true);
        builder.Property(source => source.IsEnabled)
            .HasColumnType("boolean")
            .HasDefaultValue(true);
        builder.Property(source => source.State)
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue("unknown");
        builder.Property(source => source.LastMessageAt).HasColumnType("timestamptz");
        builder.Property(source => source.StateDetail).HasColumnType("text");
        builder.Property(source => source.TelegramSessionId).HasColumnType("uuid");
        builder.Property(source => source.ProxyId).HasColumnType("uuid");
        builder.Property(source => source.CreatedBy).HasColumnType("uuid");
        builder.Property(source => source.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(source => source.UpdatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasOne<SourceType>()
            .WithMany()
            .HasForeignKey(source => source.SourceType)
            .OnDelete(DeleteBehavior.Restrict);

        // «Сайт» — один системный источник на категорию; привязка категории живёт только у него.
        builder.HasIndex(source => source.CategoryId)
            .IsUnique()
            .HasDatabaseName("sources_site_category_uq")
            .HasFilter("source_type = 'site'");

        // Ссылки на секреты: удаление используемого секрета запрещено (409),
        // как и в эталоне схемы, где FK объявлен без on delete.
        builder.HasOne<Secret>()
            .WithMany()
            .HasForeignKey(source => source.TelegramSessionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Secret>()
            .WithMany()
            .HasForeignKey(source => source.ProxyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(source => source.Policy)
            .WithOne()
            .HasForeignKey<SourcePolicy>(policy => policy.SourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "sources_state_check",
                "state in ('unknown', 'ok', 'silent', 'flood_wait', 'auth_error', 'error')");
            table.HasCheckConstraint(
                "sources_site_category_check",
                "(source_type = 'site') = (category_id is not null)");
        });
    }
}

/// <summary>
/// Таблица <c>source_statuses</c>: статусы источника. Ссылка на библиотеку стилей
/// <c>status_definitions</c> в модели не описана — таблица принадлежит модулю каталога
///, а существование записи проверяет сервис статусов. Уникальность
/// <c>(source_id, code)</c> — независимые списки у разных источников; <c>(source_id, id)</c>
/// нужна составному внешнему ключу из наблюдений.
/// </summary>
internal sealed class SourceStatusConfiguration : IEntityTypeConfiguration<SourceStatus>
{
    public void Configure(EntityTypeBuilder<SourceStatus> builder)
    {
        builder.ToTable("source_statuses");

        builder.HasKey(status => status.Id).HasName("source_statuses_pkey");

        builder.Property(status => status.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(status => status.SourceId).HasColumnType("uuid").IsRequired();
        builder.Property(status => status.StatusDefinitionId).HasColumnType("uuid");
        builder.Property(status => status.Code).HasColumnType("text").IsRequired();
        builder.Property(status => status.Name).HasColumnType("text").IsRequired();
        builder.Property(status => status.Description)
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue(string.Empty);
        builder.Property(status => status.Color).HasColumnType("text").IsRequired();
        builder.Property(status => status.Icon).HasColumnType("text").IsRequired();
        builder.Property(status => status.SortOrder)
            .HasColumnType("integer")
            .HasDefaultValue(0);

        builder.HasIndex(status => new { status.SourceId, status.Code })
            .IsUnique()
            .HasDatabaseName("source_statuses_source_id_code_key");
        builder.HasIndex(status => new { status.SourceId, status.Id })
            .IsUnique()
            .HasDatabaseName("source_statuses_source_id_id_key");

        builder.HasOne<Source>()
            .WithMany()
            .HasForeignKey(status => status.SourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "source_statuses_color_check",
                "color in ('ok', 'limited', 'none', 'queue', 'schedule', 'stale')");
        });
    }
}

internal sealed class SourcePolicyConfiguration : IEntityTypeConfiguration<SourcePolicy>
{
    public void Configure(EntityTypeBuilder<SourcePolicy> builder)
    {
        builder.ToTable("source_policies");

        builder.HasKey(policy => policy.SourceId).HasName("source_policies_pkey");

        builder.Property(policy => policy.SourceId).HasColumnType("uuid");
        builder.Property(policy => policy.Timeout)
            .HasColumnType("interval")
            .IsRequired();
        builder.Property(policy => policy.ConfidenceThreshold).HasColumnType("real");
        builder.Property(policy => policy.Prefilter).HasColumnType("jsonb");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("source_policies_timeout_check", "timeout > interval '0'");
            table.HasCheckConstraint(
                "source_policies_confidence_threshold_check",
                "confidence_threshold between 0 and 1");
        });
    }
}

/// <summary>
/// Таблица <c>secrets</c>: только шифртекст, версия
/// ключа и метаданные. Частичный уникальный индекс <c>secrets_single_llm</c> гарантирует не более
/// одного секрета вида <c>llm</c>; повторная запись заменяет его.
/// </summary>
internal sealed class SecretConfiguration : IEntityTypeConfiguration<Secret>
{
    public void Configure(EntityTypeBuilder<Secret> builder)
    {
        builder.ToTable("secrets");

        builder.HasKey(secret => secret.Id).HasName("secrets_pkey");

        builder.Property(secret => secret.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(secret => secret.Kind).HasColumnType("text").IsRequired();
        builder.Property(secret => secret.Name).HasColumnType("text").IsRequired();
        builder.Property(secret => secret.Ciphertext).HasColumnType("bytea").IsRequired();
        builder.Property(secret => secret.KeyVersion)
            .HasColumnType("integer")
            .HasDefaultValue(1)
            .IsRequired();
        builder.Property(secret => secret.ProxyId).HasColumnType("uuid");
        builder.Property(secret => secret.CreatedBy).HasColumnType("uuid");
        builder.Property(secret => secret.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(secret => secret.UpdatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasIndex(secret => new { secret.Kind, secret.Name })
            .IsUnique()
            .HasDatabaseName("secrets_kind_name_key");
        builder.HasIndex(secret => secret.Kind)
            .IsUnique()
            .HasDatabaseName("secrets_single_llm")
            .HasFilter("kind = 'llm'");

        // Прокси аккаунта или LLM; у самого прокси прокси быть не может (check в БД).
        builder.HasOne<Secret>()
            .WithMany()
            .HasForeignKey(secret => secret.ProxyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "secrets_kind_check",
                "kind in ('telegram_session', 'proxy', 'llm')");
            table.HasCheckConstraint(
                "secrets_check",
                "proxy_id is null or kind <> 'proxy'");
        });
    }
}
