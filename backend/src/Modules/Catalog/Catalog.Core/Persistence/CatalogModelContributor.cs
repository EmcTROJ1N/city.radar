using BuildingBlocks.Persistence;

using Catalog.Core.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Core.Persistence;

/// <summary>
/// Модель каталога для единого <see cref="MayakDbContext"/>: таблицы <c>categories</c>,
/// <c>object_types</c>, <c>object_groups</c>, <c>status_definitions</c>. Имена таблиц, колонок
/// (snake_case), ключей, индексов и типы колонок сверены с <c>db/schema.sql</c>.
/// </summary>
public sealed class CatalogModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new CategoryConfiguration());
        modelBuilder.ApplyConfiguration(new ObjectTypeConfiguration());
        modelBuilder.ApplyConfiguration(new ObjectGroupConfiguration());
        modelBuilder.ApplyConfiguration(new StatusDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new MapObjectConfiguration());
        modelBuilder.ApplyConfiguration(new MapObjectSourceConfiguration());
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(category => category.Id).HasName("categories_pkey");

        builder.Property(category => category.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(category => category.Code).HasColumnType("text").IsRequired();
        builder.Property(category => category.Name).HasColumnType("text").IsRequired();
        builder.Property(category => category.Icon).HasColumnType("text");
        builder.Property(category => category.SortOrder)
            .HasColumnType("integer")
            .HasDefaultValue(0);
        builder.Property(category => category.IsActive)
            .HasColumnType("boolean")
            .HasDefaultValue(true);

        // В эталоне уникальность задана в колонке: Postgres создаёт индекс categories_code_key.
        builder.HasIndex(category => category.Code)
            .IsUnique()
            .HasDatabaseName("categories_code_key");

    }
}

internal sealed class ObjectTypeConfiguration : IEntityTypeConfiguration<ObjectType>
{
    public void Configure(EntityTypeBuilder<ObjectType> builder)
    {
        builder.ToTable("object_types");

        builder.HasKey(objectType => objectType.Id).HasName("object_types_pkey");

        builder.Property(objectType => objectType.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(objectType => objectType.CategoryId).HasColumnType("uuid");
        builder.Property(objectType => objectType.Code).HasColumnType("text").IsRequired();
        builder.Property(objectType => objectType.Name).HasColumnType("text").IsRequired();
        builder.Property(objectType => objectType.AttributesSchema)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'{}'");
        builder.Property(objectType => objectType.GeometryKind)
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue("any");

        builder.HasIndex(objectType => objectType.Code)
            .IsUnique()
            .HasDatabaseName("object_types_code_key");

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(objectType => objectType.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

    }
}

internal sealed class ObjectGroupConfiguration : IEntityTypeConfiguration<ObjectGroup>
{
    public void Configure(EntityTypeBuilder<ObjectGroup> builder)
    {
        builder.ToTable("object_groups");

        builder.HasKey(group => group.Id).HasName("object_groups_pkey");

        builder.Property(group => group.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(group => group.CategoryId).HasColumnType("uuid");
        builder.Property(group => group.Name).HasColumnType("text").IsRequired();
        builder.Property(group => group.Color).HasColumnType("text");
        builder.Property(group => group.IsVisibleByDefault)
            .HasColumnType("boolean")
            .HasDefaultValue(true);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(group => group.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

    }
}

internal sealed class StatusDefinitionConfiguration : IEntityTypeConfiguration<StatusDefinition>
{
    public void Configure(EntityTypeBuilder<StatusDefinition> builder)
    {
        builder.ToTable("status_definitions");

        builder.HasKey(status => status.Id).HasName("status_definitions_pkey");

        builder.Property(status => status.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(status => status.CategoryId).HasColumnType("uuid");
        builder.Property(status => status.Code).HasColumnType("text").IsRequired();
        builder.Property(status => status.Name).HasColumnType("text").IsRequired();
        builder.Property(status => status.Color).HasColumnType("text").IsRequired();
        builder.Property(status => status.Icon).HasColumnType("text").IsRequired();
        builder.Property(status => status.Severity)
            .HasColumnType("integer")
            .HasDefaultValue(0);
        builder.Property(status => status.IsPositive)
            .HasColumnType("boolean")
            .HasDefaultValue(false);

        builder.HasIndex(status => status.Code)
            .IsUnique()
            .HasDatabaseName("status_definitions_code_key");

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(status => status.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>
/// Таблица <c>map_objects</c>: геометрия <c>geography(Geometry, 4326)</c>, GiST/GIN/trgm-индексы
/// и check соответствия <c>coverage_kind</c> типу геометрии. Физический DDL — миграция
/// <c>MapObjects</c> (написана вручную по <c>db/schema.sql</c>).
/// </summary>
internal sealed class MapObjectConfiguration : IEntityTypeConfiguration<MapObject>
{
    public void Configure(EntityTypeBuilder<MapObject> builder)
    {
        builder.ToTable("map_objects");

        builder.HasKey(mapObject => mapObject.Id).HasName("map_objects_pkey");

        builder.Property(mapObject => mapObject.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(mapObject => mapObject.ObjectTypeId).HasColumnType("uuid");
        builder.Property(mapObject => mapObject.GroupId).HasColumnType("uuid");
        builder.Property(mapObject => mapObject.Name).HasColumnType("text").IsRequired();
        builder.Property(mapObject => mapObject.CoverageKind).HasColumnType("text").IsRequired();
        builder.Property(mapObject => mapObject.Geometry)
            .HasColumnType("geography (Geometry, 4326)")
            .IsRequired();
        builder.Property(mapObject => mapObject.Attributes)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'{}'");
        builder.Property(mapObject => mapObject.Address).HasColumnType("text");
        builder.Property(mapObject => mapObject.IsActive)
            .HasColumnType("boolean")
            .HasDefaultValue(true);
        builder.Property(mapObject => mapObject.CreatedBy).HasColumnType("uuid");
        builder.Property(mapObject => mapObject.CreatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");
        builder.Property(mapObject => mapObject.UpdatedAt)
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasIndex(mapObject => mapObject.Geometry)
            .HasMethod("gist")
            .HasDatabaseName("map_objects_geometry_gist");
        builder.HasIndex(mapObject => mapObject.Attributes)
            .HasMethod("gin")
            .HasDatabaseName("map_objects_attributes_gin");
        builder.HasIndex(mapObject => mapObject.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("map_objects_name_trgm");
        builder.HasIndex(mapObject => mapObject.GroupId)
            .HasDatabaseName("map_objects_group_idx");

        builder.HasOne<ObjectType>()
            .WithMany()
            .HasForeignKey(mapObject => mapObject.ObjectTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ObjectGroup>()
            .WithMany()
            .HasForeignKey(mapObject => mapObject.GroupId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "map_objects_coverage_kind_check",
                "coverage_kind in ('point', 'polygon')");
            table.HasCheckConstraint(
                "map_objects_coverage_geometry_check",
                "((coverage_kind = 'point' and geometrytype(geometry::geometry) = 'POINT')"
                + " or (coverage_kind = 'polygon' and geometrytype(geometry::geometry) = 'POLYGON'))");
        });
    }
}

/// <summary>
/// Таблица <c>map_object_sources</c>: привязки источников к фиксациям. Внешний ключ на
/// <c>sources(id)</c> (<c>map_object_sources_source_id_fkey</c>, on delete cascade) создаётся
/// миграцией <c>Sources</c>; в модели его нет, потому что сущность источника
/// принадлежит другому модулю, а модули видят друг друга только через <c>*.Contracts</c>.
/// </summary>
internal sealed class MapObjectSourceConfiguration : IEntityTypeConfiguration<MapObjectSource>
{
    public void Configure(EntityTypeBuilder<MapObjectSource> builder)
    {
        builder.ToTable("map_object_sources");

        builder.HasKey(link => new { link.MapObjectId, link.SourceId })
            .HasName("map_object_sources_pkey");

        builder.Property(link => link.MapObjectId).HasColumnType("uuid");
        builder.Property(link => link.SourceId).HasColumnType("uuid");

        builder.HasOne<MapObject>()
            .WithMany()
            .HasForeignKey(link => link.MapObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(link => link.SourceId)
            .HasDatabaseName("map_object_sources_source_idx");
    }
}
