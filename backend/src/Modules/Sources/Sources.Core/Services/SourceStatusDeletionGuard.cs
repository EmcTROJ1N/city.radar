using Sources.Contracts;

namespace Sources.Core.Services;

/// <summary>
/// Реализация по умолчанию: наблюдений нет, удаление статуса разрешено. Модуль наблюдений
/// заменит регистрацию через <c>TryAddScoped</c>, и удаление начнёт возвращать 409
/// без изменения кода источников.
/// </summary>
internal sealed class SourceStatusDeletionGuard : ISourceStatusDeletionGuard
{
    public Task<bool> HasObservationsAsync(Guid sourceId, Guid sourceStatusId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
