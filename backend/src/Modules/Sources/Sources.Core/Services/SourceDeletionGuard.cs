using Sources.Contracts;

namespace Sources.Core.Services;

/// <summary>
/// Реализация по умолчанию: наблюдений нет, удаление источника разрешено. Модуль наблюдений
/// заменит регистрацию через <c>TryAddScoped</c>, и удаление начнёт возвращать 409
/// без изменения кода источников.
/// </summary>
internal sealed class SourceDeletionGuard : ISourceDeletionGuard
{
    public Task<bool> HasObservationsAsync(Guid sourceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
