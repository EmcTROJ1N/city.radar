namespace IdentityAccess.Core.Domain;

/// <summary>Глобальная настройка (таблица <c>global_settings</c>): ключ и значение jsonb.</summary>
internal sealed class GlobalSetting
{
    public string Key { get; set; } = null!;

    /// <summary>Значение настройки — jsonb.</summary>
    public string Value { get; set; } = null!;

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
