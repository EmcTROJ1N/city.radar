namespace BuildingBlocks.Web;

/// <summary>
/// Проверка текста перед записью в PostgreSQL: недопустимы нулевой символ и одиночные
/// суррогаты, из которых нельзя получить корректный UTF-8.
/// </summary>
public static class TextValidator
{
    /// <summary>Текст ошибки для поля с недопустимыми символами.</summary>
    public const string InvalidCharactersMessage =
        "Текст содержит недопустимые символы";

    /// <summary>Текст ошибки для строки, которую нельзя сохранить в PostgreSQL.</summary>
    public const string UnstorableMessage =
        "Текст содержит нулевой символ или неверную суррогатную пару";

    /// <summary>
    /// <c>true</c>, если строку можно записать в PostgreSQL: нет нулевого символа (0x00)
    /// и нет одиночных суррогатов, из которых нельзя получить корректный UTF-8.
    /// </summary>
    public static bool IsValid(string? value) => IsStorable(value);

    /// <summary>
    /// <c>true</c>, если строку можно записать в PostgreSQL: нет нулевого символа (0x00)
    /// и нет одиночных суррогатов, из которых нельзя получить корректный UTF-8.
    /// </summary>
    public static bool IsStorable(string? value) =>
        string.IsNullOrEmpty(value) || !HasUnstorableCharacters(value);

    private static bool HasUnstorableCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];

            if (current == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Проверяет набор текстовых полей и возвращает ошибки для тех, которые нельзя сохранить.
    /// Результат передаётся в <see cref="ProblemResponses.Validation"/> для ответа 422
    /// по схеме <c>ValidationProblem</c>.
    /// </summary>
    public static IReadOnlyList<ValidationError> Validate(
        params (string Field, string? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var errors = new List<ValidationError>();

        foreach (var (field, value) in fields)
        {
            if (!IsStorable(value))
            {
                errors.Add(new ValidationError(field, UnstorableMessage));
            }
        }

        return errors;
    }
}
