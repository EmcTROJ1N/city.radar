using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Ключ дедупликации открытых кейсов аномалий.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Частичный уникальный индекс не даёт создать второй открытый кейс
    /// <c>anomaly</c> на тот же район, даже если два обработчика обошли advisory lock
    /// (например, при будущем обработчике в другом процессе). Ключ района хранится в
    /// <c>moderation_cases.proposed-&gt;&gt;'key'</c>; закрытые кейсы (state ≠ open) в индекс
    /// не попадают, поэтому после решения новый всплеск создаёт новый кейс.
    /// </para>
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115190000_AnomalyCaseKey")]
    public partial class AnomalyCaseKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create unique index moderation_cases_open_anomaly_key_uq
                  on moderation_cases ((proposed->>'key'))
                  where kind = 'anomaly'
                    and state = 'open'
                    and proposed ? 'key';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop index if exists moderation_cases_open_anomaly_key_uq;");
        }
    }
}
