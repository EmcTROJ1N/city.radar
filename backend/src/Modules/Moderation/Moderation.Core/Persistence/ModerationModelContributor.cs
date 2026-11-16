using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Moderation.Core.Domain;

namespace Moderation.Core.Persistence;

/// <summary>
/// Модель очереди модерации для единого <see cref="MayakDbContext"/>: таблица
/// <c>moderation_cases</c>. Имена таблицы/колонок (snake_case), типы, индексы и
/// check-ограничения сверены с <c>db/schema.sql</c>; физический DDL — ручная миграция
/// <c>Moderation</c>.
/// </summary>
/// <remarks>
/// Кроме эталонных колонок миграция добавляет <c>outbox_message_id uuid unique</c> — ключ
/// идемпотентности обработчика <c>ModerationCaseRequested</c> (описано в миграции и отчёте).
/// </remarks>
public sealed class ModerationModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new ModerationCaseConfiguration());
    }
}

internal sealed class ModerationCaseConfiguration : IEntityTypeConfiguration<ModerationCase>
{
    public void Configure(EntityTypeBuilder<ModerationCase> builder)
    {
        builder.ToTable("moderation_cases");

        builder.HasKey(moderationCase => moderationCase.Id).HasName("moderation_cases_pkey");

        builder.Property(moderationCase => moderationCase.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(moderationCase => moderationCase.Kind).HasColumnType("text").IsRequired();
        builder.Property(moderationCase => moderationCase.State)
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue(ModerationCaseStates.Open);
        builder.Property(moderationCase => moderationCase.SourceId).HasColumnType("uuid");
        builder.Property(moderationCase => moderationCase.RawMessageId).HasColumnType("uuid");
        builder.Property(moderationCase => moderationCase.ObservationId).HasColumnType("uuid");
        builder.Property(moderationCase => moderationCase.MapObjectId).HasColumnType("uuid");
        builder.Property(moderationCase => moderationCase.ReporterId).HasColumnType("uuid");
        builder.Property(moderationCase => moderationCase.Comment).HasColumnType("text");
        builder.Property(moderationCase => moderationCase.Proposed).HasColumnType("jsonb");
        builder.Property(moderationCase => moderationCase.Resolution).HasColumnType("jsonb");
        builder.Property(moderationCase => moderationCase.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(moderationCase => moderationCase.ResolvedBy).HasColumnType("uuid");
        builder.Property(moderationCase => moderationCase.ResolvedAt).HasColumnType("timestamptz");
        builder.Property(moderationCase => moderationCase.OutboxMessageId).HasColumnType("uuid");

        // Очередь: открытые кейсы по времени создания (старые сверху).
        builder.HasIndex(moderationCase => moderationCase.CreatedAt)
            .HasDatabaseName("moderation_cases_open_idx")
            .HasFilter($"state = '{ModerationCaseStates.Open}'");

        // Ключ идемпотентности обработчика outbox (добавка к эталону схемы).
        builder.HasIndex(moderationCase => moderationCase.OutboxMessageId)
            .IsUnique()
            .HasDatabaseName("moderation_cases_outbox_message_id_key");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "moderation_cases_kind_check",
                "kind in ('low_confidence', 'invalid_status', 'object_not_found', "
                + "'invalid_response', 'anomaly', 'complaint')");
            table.HasCheckConstraint(
                "moderation_cases_state_check",
                "state in ('open', 'approved', 'rejected', 'dismissed')");
            table.HasCheckConstraint(
                "moderation_cases_check",
                "(state = 'open') = (resolved_at is null)");
            table.HasCheckConstraint(
                "moderation_cases_check1",
                "(kind = 'complaint') = (reporter_id is not null)");
        });
    }
}
