namespace Ingestion.LlmEval.Core;

/// <summary>
/// Ошибка набора или конфигурации источника: файл не найден, поле отсутствует, код статуса
/// или фиксации не совпадает с конфигурацией. Сообщение пригодно для показа пользователю.
/// </summary>
public sealed class EvalConfigurationException : Exception
{
    /// <summary>Создаёт ошибку с пояснением для человека.</summary>
    /// <param name="message">Что именно не так и как это исправить.</param>
    public EvalConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт ошибку с пояснением и внутренним исключением.</summary>
    /// <param name="message">Что именно не так и как это исправить.</param>
    /// <param name="innerException">Исходная ошибка.</param>
    public EvalConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Ошибка разбора JSONL-набора: сообщение содержит имя файла и номер строки, чтобы разметчик
/// мог сразу найти проблемный случай.
/// </summary>
public sealed class EvalDatasetException : Exception
{
    /// <summary>Создаёт ошибку разбора одной строки набора.</summary>
    /// <param name="origin">Путь к файлу набора или иное имя источника строк.</param>
    /// <param name="lineNumber">Номер строки в файле, начиная с 1.</param>
    /// <param name="reason">Причина: невалидный JSON, отсутствующее поле, неверный тип.</param>
    public EvalDatasetException(string origin, int lineNumber, string reason)
        : base($"Файл \"{origin}\", строка {lineNumber}: {reason}")
    {
        Origin = origin;
        LineNumber = lineNumber;
        Reason = reason;
    }

    /// <summary>Путь к файлу набора или иное имя источника строк.</summary>
    public string Origin { get; }

    /// <summary>Номер строки в файле, начиная с 1.</summary>
    public int LineNumber { get; }

    /// <summary>Короткая причина без имени файла и номера строки.</summary>
    public string Reason { get; }
}
