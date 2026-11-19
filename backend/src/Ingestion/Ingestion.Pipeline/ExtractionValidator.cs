namespace Ingestion.Pipeline;

/// <summary>
/// Результат проверки ответа LLM кодом (шаги 3–4). Всё, кроме <see cref="Valid"/>,
/// означает «не на карту»: предложение уходит в очередь модерации.
/// </summary>
public enum ValidationResult
{
    /// <summary>Ответ можно публиковать: статус, цитата и (если нужна) фиксация корректны.</summary>
    Valid,

    /// <summary>Код статуса не совпадает точно ни с одним кодом статуса этого источника.</summary>
    InvalidStatus,

    /// <summary><c>evidence_quote</c> пуста или не является точной подстрокой текста сообщения.</summary>
    EvidenceNotSubstring,

    /// <summary>Режим «по тексту» включён при нескольких фиксациях, а <c>object_id</c> не из их списка.</summary>
    ObjectNotFound,
}

/// <summary>
/// Проверка ответа LLM кодом. Текст сообщения
/// недоверенный ввод: доверять можно только ответу, который прошёл эти проверки.
/// </summary>
/// <remarks>
/// <para>
/// Цитата <c>evidence_quote</c> принимается только как точная подстрока исходного полного текста
/// сообщения: сравнение порядковое (<see cref="StringComparison.Ordinal"/>), регистр и пробелы
/// не нормализуются. Единственная нормализация — схлопывание <c>\r\n</c> в <c>\n</c> в обеих
/// строках: переводы строк приходят из разных транспортов в разном виде, и без этого сравнение
/// теряло бы смысл. Если нормализовать регистр или пробелы, проверка перестала бы ловить
/// пересказ и подгонку цитаты под ответ.
/// </para>
/// <para>
/// Проверка фиксации выполняется только в режиме «определять фиксацию по тексту» и только при
/// нескольких фиксациях источника: при выключенном режиме или единственной фиксации
/// <c>object_id</c> из ответа игнорируется.
/// </para>
/// </remarks>
public static class ExtractionValidator
{
    /// <summary>
    /// Проверяет разобранный ответ LLM на соответствие источнику и тексту сообщения.
    /// </summary>
    /// <param name="response">Разобранный ответ; нерелевантный ответ считается валидным.</param>
    /// <param name="messageText">Полный исходный текст сообщения (не усечённый для LLM).</param>
    /// <param name="statusCodes">Коды статусов источника; сравнение — точное, порядковое.</param>
    /// <param name="sourceObjectIds">Идентификаторы фиксаций, привязанных к источнику.</param>
    /// <param name="linkByText">Включён ли режим «определять фиксацию по тексту».</param>
    /// <param name="multipleObjects">Привязано ли к источнику больше одной фиксации.</param>
    /// <returns>Итог проверки; <see cref="ValidationResult.Valid"/> — можно публиковать.</returns>
    public static ValidationResult Validate(
        ParsedLlmResponse response,
        string messageText,
        IReadOnlySet<string> statusCodes,
        IReadOnlySet<Guid> sourceObjectIds,
        bool linkByText,
        bool multipleObjects)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(messageText);
        ArgumentNullException.ThrowIfNull(statusCodes);
        ArgumentNullException.ThrowIfNull(sourceObjectIds);

        if (!response.Relevant)
        {
            // Нерелевантное сообщение не содержит предложения: проверять нечего.
            return ValidationResult.Valid;
        }

        if (!IsStatusCodeAllowed(response.Status, statusCodes))
        {
            return ValidationResult.InvalidStatus;
        }

        if (!IsEvidenceSubstring(response.EvidenceQuote, messageText))
        {
            return ValidationResult.EvidenceNotSubstring;
        }

        if (linkByText && multipleObjects && !IsLinkedObject(response.ObjectId, sourceObjectIds))
        {
            return ValidationResult.ObjectNotFound;
        }

        return ValidationResult.Valid;
    }

    /// <summary>
    /// Точное (порядковое) совпадение кода статуса с одним из кодов источника. Компаратор
    /// переданного множества не используется намеренно: <c>"OK"</c> не должен совпасть с <c>"ok"</c>.
    /// </summary>
    /// <summary>Максимальная длина адреса из ответа LLM.</summary>
    public const int MaxAddressLength = 120;

    /// <summary>
    /// Проверяет адрес из ответа LLM: слишком длинный или с украинскими буквами (C-01) отбрасывается.
    /// Статус от этого не зависит — адрес нужен только для точек на карте.
    /// </summary>
    /// <param name="address">Адрес из ответа LLM.</param>
    /// <returns>Тот же адрес либо <c>null</c>, если он недопустим.</returns>
    public static string? SanitizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > MaxAddressLength)
        {
            return null;
        }

        return address.AsSpan().IndexOfAny("іїєґІЇЄҐ") >= 0 ? null : address;
    }

    private static bool IsStatusCodeAllowed(string? status, IReadOnlySet<string> statusCodes)
    {
        if (string.IsNullOrEmpty(status))
        {
            return false;
        }

        foreach (var code in statusCodes)
        {
            if (string.Equals(code, status, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Пустая цитата невалидна; иначе — точная подстрока после схлопывания <c>\r\n</c>.</summary>
    private static bool IsEvidenceSubstring(string? quote, string messageText)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return false;
        }

        return Normalize(messageText).Contains(Normalize(quote), StringComparison.Ordinal);
    }

    /// <summary>Схлопывает <c>\r\n</c> в <c>\n</c>; больше ничего не меняет (регистр и пробелы — как есть).</summary>
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// <c>object_id</c> должен быть корректным <see cref="Guid"/> из списка фиксаций источника;
    /// <c>null</c>, пустая строка и любой другой идентификатор — «не найдено».
    /// </summary>
    private static bool IsLinkedObject(string? objectId, IReadOnlySet<Guid> sourceObjectIds)
    {
        if (!Guid.TryParse(objectId, out var id))
        {
            return false;
        }

        foreach (var candidate in sourceObjectIds)
        {
            if (candidate == id)
            {
                return true;
            }
        }

        return false;
    }
}
