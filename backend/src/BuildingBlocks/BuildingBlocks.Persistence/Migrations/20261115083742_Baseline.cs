using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Базовая миграция: расширения PostgreSQL и общие функции, на которые опираются
    /// таблицы и триггеры последующих миграций. SQL сверен с db/schema.sql.
    /// </summary>
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create extension if not exists postgis;
                create extension if not exists pg_trgm;
                """);

            migrationBuilder.Sql(
                """
                create function set_updated_at() returns trigger language plpgsql as $$
                begin
                  new.updated_at = now();
                  return new;
                end $$;
                """);

            migrationBuilder.Sql(
                """
                create function forbid_change() returns trigger language plpgsql as $$
                begin
                  raise exception 'Таблица % только для добавления записей', tg_table_name;
                end $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Расширения не удаляются: они общие для всей БД и могли существовать до миграции.
            migrationBuilder.Sql("drop function if exists forbid_change();");
            migrationBuilder.Sql("drop function if exists set_updated_at();");
        }
    }
}
