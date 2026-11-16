using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Модель outbox для единого <see cref="MayakDbContext"/>: таблица <c>outbox_messages</c>
/// и частичный индекс <c>outbox_pending_idx</c>. DDL — ручная миграция <c>Outbox</c>,
/// написанная дословно по <c>db/schema.sql</c>.
/// </summary>
public sealed class OutboxModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id).HasName("outbox_messages_pkey");

        builder.Property(message => message.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(message => message.Destination).HasColumnType("text").IsRequired();
        builder.Property(message => message.Type).HasColumnType("text").IsRequired();
        builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(message => message.AvailableAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(message => message.ProcessedAt).HasColumnType("timestamptz");
        builder.Property(message => message.Attempts)
            .HasColumnType("integer")
            .HasDefaultValue(0);
        builder.Property(message => message.LastError).HasColumnType("text");

        // Частичный индекс очереди: выбираются только необработанные сообщения destination.
        builder.HasIndex(message => new { message.Destination, message.AvailableAt })
            .HasDatabaseName("outbox_pending_idx")
            .HasFilter("processed_at is null");

        builder.ToTable(table => table.HasCheckConstraint(
            "outbox_messages_destination_check",
            "destination in ('api', 'worker')"));
    }
}
