using System.Text.Json;

namespace IngestionWorker.Host;

/// <summary>
/// Конфигурация одного включённого источника на момент опроса.
/// <see cref="Hash"/> считается по всем полям, влияющим на работу адаптера: смена любого из них
/// перезапускает только этот источник, остальные продолжают работать.
/// </summary>
/// <param name="SourceId">Идентификатор источника.</param>
/// <param name="Hash">Хеш конфигурации (SHA-256, hex) — сравнение «изменилось / не изменилось».</param>
/// <param name="SourceType">Код типа источника из <c>source_types.code</c>.</param>
/// <param name="Settings">Настройки типа источника (<c>sources.settings</c>) для адаптера.</param>
public sealed record SourceConfigSnapshot(
    Guid SourceId,
    string Hash,
    string SourceType,
    JsonElement Settings);
