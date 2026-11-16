namespace IdentityAccess.Contracts;

/// <summary>
/// Снимок глобальных настроек (схема <c>GlobalSettings</c> контракта). Отсутствующие в
/// <c>global_settings</c> ключи принимают значения по умолчанию, поэтому снимок всегда полный
/// и его можно читать другим модулям без проверок на <c>null</c>.
/// </summary>
/// <param name="ObservationRatePerHour">Лимит отметок на пользователя в час (по умолчанию 20).</param>
/// <param name="CommentMaxLength">Максимальная длина комментария к отметке (по умолчанию 500).</param>
/// <param name="ConfidenceThresholdDefault">Порог уверенности LLM по умолчанию (0..1, по умолчанию 0.7).</param>
/// <param name="AnomalyWindowMinutes">Окно детектора аномалий, минуты (по умолчанию 5).</param>
/// <param name="AnomalyThreshold">Порог отметок в окне, после которого фиксация замораживается (по умолчанию 30).</param>
/// <param name="FreshnessFreshRatio">Доля таймаута, до которой статус считается свежим (0..1, по умолчанию 0.5).</param>
/// <param name="ProxySecretId">Глобальный прокси (секрет вида <c>proxy</c>); <c>null</c> — без прокси.</param>
public sealed record GlobalSettingsSnapshot(
    int ObservationRatePerHour,
    int CommentMaxLength,
    double ConfidenceThresholdDefault,
    int AnomalyWindowMinutes,
    int AnomalyThreshold,
    double FreshnessFreshRatio,
    Guid? ProxySecretId);
