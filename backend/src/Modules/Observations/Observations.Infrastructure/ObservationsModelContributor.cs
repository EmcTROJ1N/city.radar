using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Observations.Infrastructure;

/// <summary>
/// Модель ядра наблюдений для единого <c>MayakDbContext</c>: таблицы <c>raw_messages</c>,
/// <c>observations</c> и <c>current_statuses</c>. Имена таблиц, колонок (snake_case) и типы
/// сверены с <c>db/schema.sql</c>; физический DDL — миграция <c>Observations</c>,
/// написанная вручную по эталону.
/// </summary>
/// <remarks>
/// Составной внешний ключ <c>observations (source_id, source_status_id)</c> и часть
/// check-констрейнтов/индексов живут только в БД: сущности других модулей (<c>source_statuses</c>)
/// недоступны модели наблюдений (— только через <c>*.Contracts</c>).
/// </remarks>
public sealed class ObservationsModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new ObservationConfiguration());
        modelBuilder.ApplyConfiguration(new CurrentStatusConfiguration());
        modelBuilder.ApplyConfiguration(new RawMessageConfiguration());
    }
}

internal sealed class ObservationConfiguration : IEntityTypeConfiguration<ObservationRecord>
{
    public void Configure(EntityTypeBuilder<ObservationRecord> builder)
    {
        builder.ToTable("observations");

        builder.HasKey(observation => observation.Id).HasName("observations_pkey");

        builder.Property(observation => observation.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(observation => observation.MapObjectId).HasColumnType("uuid").IsRequired();
        builder.Property(observation => observation.SourceId).HasColumnType("uuid").IsRequired();
        builder.Property(observation => observation.SourceStatusId).HasColumnType("uuid").IsRequired();
        builder.Property(observation => observation.SourceType).HasColumnType("text").IsRequired();
        builder.Property(observation => observation.ReporterId).HasColumnType("uuid");
        builder.Property(observation => observation.ObservedAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(observation => observation.ReceivedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(observation => observation.ExpiresAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(observation => observation.Confidence).HasColumnType("real");
        builder.Property(observation => observation.RawMessageId).HasColumnType("uuid");
        builder.Property(observation => observation.Comment).HasColumnType("text");
        builder.Property(observation => observation.GeoDistanceM).HasColumnType("integer");
        builder.Property(observation => observation.IsRetracted)
            .HasColumnType("boolean")
            .HasDefaultValue(false);
        builder.Property(observation => observation.RetractedBy).HasColumnType("uuid");
        builder.Property(observation => observation.RetractedAt).HasColumnType("timestamptz");
        builder.Property(observation => observation.RetractReason).HasColumnType("text");
    }
}

internal sealed class CurrentStatusConfiguration : IEntityTypeConfiguration<CurrentStatusRecord>
{
    public void Configure(EntityTypeBuilder<CurrentStatusRecord> builder)
    {
        builder.ToTable("current_statuses");

        builder.HasKey(status => status.MapObjectId).HasName("current_statuses_pkey");

        builder.Property(status => status.MapObjectId).HasColumnType("uuid");
        builder.Property(status => status.SourceStatusId).HasColumnType("uuid");
        builder.Property(status => status.SourceId).HasColumnType("uuid");
        builder.Property(status => status.ObservationId).HasColumnType("uuid");
        builder.Property(status => status.ObservedAt).HasColumnType("timestamptz");
        builder.Property(status => status.Freshness).HasColumnType("text").IsRequired();
        builder.Property(status => status.SourceType).HasColumnType("text");
        builder.Property(status => status.ObservationCount)
            .HasColumnType("integer")
            .HasDefaultValue(0);
        builder.Property(status => status.UpdatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
    }
}

internal sealed class RawMessageConfiguration : IEntityTypeConfiguration<RawMessageRecord>
{
    public void Configure(EntityTypeBuilder<RawMessageRecord> builder)
    {
        builder.ToTable("raw_messages");

        builder.HasKey(message => message.Id).HasName("raw_messages_pkey");

        builder.Property(message => message.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(message => message.SourceId).HasColumnType("uuid").IsRequired();
        builder.Property(message => message.ExternalId).HasColumnType("text").IsRequired();
        builder.Property(message => message.Text).HasColumnType("text").IsRequired();
        builder.Property(message => message.Author).HasColumnType("text");
        builder.Property(message => message.Chat).HasColumnType("text");
        builder.Property(message => message.Link).HasColumnType("text");
        builder.Property(message => message.PostedAt).HasColumnType("timestamptz").IsRequired();
        builder.Property(message => message.ReceivedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasIndex(message => new { message.SourceId, message.ExternalId })
            .IsUnique()
            .HasDatabaseName("raw_messages_source_id_external_id_key");
    }
}
