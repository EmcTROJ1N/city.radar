namespace IdentityAccess.Contracts;

/// <summary>
/// Чтение глобальных настроек другими модулями: лимит длины комментария,
/// пороги аномалий, порог confidence, доля Fresh, параметры rate limit и глобальный прокси.
/// Значения берутся из <c>global_settings</c>, а отсутствующие ключи — из значений по умолчанию.
/// </summary>
public interface IGlobalSettings
{
    /// <summary>Возвращает полный снимок настроек; метод безопасен для чтения на каждом запросе.</summary>
    Task<GlobalSettingsSnapshot> GetAsync(CancellationToken ct);
}
