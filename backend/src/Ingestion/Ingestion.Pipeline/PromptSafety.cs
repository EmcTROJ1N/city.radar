namespace Ingestion.Pipeline;

/// <summary>
/// Безопасная передача недоверенного текста в LLM (раздел про prompt injection):
/// сообщение чата заключается в уникальные маркеры, а попытки подделать маркер внутри текста
/// нейтрализуются, чтобы сообщение не могло «закрыть» свой блок и подменить инструкции.
/// </summary>
public static class PromptSafety
{
    /// <summary>Маркер начала блока с текстом сообщения.</summary>
    public const string MessageStartMarker = "<<<CHAT_MESSAGE_BEGIN>>>";

    /// <summary>Маркер конца блока с текстом сообщения.</summary>
    public const string MessageEndMarker = "<<<CHAT_MESSAGE_END>>>";

    /// <summary>
    /// Разумный предел длины сообщения для запроса к LLM. Полный текст сохраняется в
    /// <c>raw_messages</c>, и проверка <c>evidence_quote</c> выполняется по нему.
    /// </summary>
    public const int DefaultMaxMessageLength = 4000;

    /// <summary>
    /// Ключевое слово маркеров без обрамляющих скобок. Встречается в тексте в любом регистре —
    /// значит, сообщение пытается изобразить границу блока; такие вхождения обезвреживаются.
    /// </summary>
    private const string MarkerKeyword = "CHAT_MESSAGE_";

    /// <summary>Безопасная замена ключевого слова: дефисы вместо подчёркиваний.</summary>
    private const string NeutralizedKeyword = "CHAT-MESSAGE-";

    /// <summary>
    /// Оборачивает текст сообщения в маркеры. Любое вхождение ключевого слова маркеров в тексте
    /// (в том числе полные и частичные маркеры в любом регистре) обезвреживается, поэтому внутри
    /// блока не остаётся ни одного маркера, кроме обрамляющих.
    /// </summary>
    /// <param name="text">Текст сообщения; <c>null</c> трактуется как пустая строка.</param>
    /// <returns>Текст между <see cref="MessageStartMarker"/> и <see cref="MessageEndMarker"/>.</returns>
    public static string WrapMessage(string? text)
    {
        var safe = Neutralize(text);
        return $"{MessageStartMarker}\n{safe}\n{MessageEndMarker}";
    }

    /// <summary>
    /// Усекает текст сообщения до предела, отправляемого в LLM. Усечение — не более чем
    /// предохранитель от раздувания запроса: <c>evidence_quote</c> проверяется по полному тексту.
    /// Суррогатная пара на границе не разрывается.
    /// </summary>
    /// <param name="text">Текст сообщения; <c>null</c> трактуется как пустая строка.</param>
    /// <param name="maxLength">Максимальная длина результата; неположительное значение даёт пустую строку.</param>
    /// <returns>Текст не длиннее <paramref name="maxLength"/> символов.</returns>
    public static string TruncateForLlm(string? text, int maxLength = DefaultMaxMessageLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }

    private static string Neutralize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace(MarkerKeyword, NeutralizedKeyword, StringComparison.OrdinalIgnoreCase);
    }
}
