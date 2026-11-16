namespace Observations.Domain;

/// <summary>
/// Нарушение доменного правила. Прикладной слой переводит исключение
/// в ответ API (422/409), домен при этом не знает ни о HTTP, ни о БД.
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>Создаёт исключение с описанием нарушения правила.</summary>
    /// <param name="message">Описание нарушения на русском языке.</param>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с описанием нарушения и внутренней причиной.</summary>
    /// <param name="message">Описание нарушения на русском языке.</param>
    /// <param name="innerException">Причина, из-за которой правило не выполнено.</param>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
