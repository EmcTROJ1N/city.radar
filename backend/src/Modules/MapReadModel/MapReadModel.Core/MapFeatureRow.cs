using System.Text.Json;

namespace MapReadModel.Core;

/// <summary>
/// Строка read-only проекции для карты: фиксация, её геометрия и итоговый статус из
/// <c>current_statuses</c>. Поля статуса — <c>null</c>, если строки проекции нет или в ней
/// «неизвестно» (<c>source_status_id is null</c>).
/// </summary>
/// <param name="Id">Идентификатор фиксации.</param>
/// <param name="Name">Название фиксации.</param>
/// <param name="CoverageKind">Вид геометрии: <c>point</c> или <c>polygon</c>.</param>
/// <param name="CategoryId">Категория типа объекта (может отсутствовать).</param>
/// <param name="ObjectTypeId">Тип объекта (может отсутствовать).</param>
/// <param name="GroupId">Группа фиксации (может отсутствовать).</param>
/// <param name="Address">Адрес (может отсутствовать).</param>
/// <param name="Geometry">Геометрия в GeoJSON, уже упрощённая по zoom.</param>
/// <param name="SourceStatusId">Статус источника — итог проекции.</param>
/// <param name="StatusCode">Код статуса.</param>
/// <param name="StatusName">Название статуса.</param>
/// <param name="StatusColor">Ключ палитры оформления.</param>
/// <param name="StatusIcon">Иконка статуса.</param>
/// <param name="Freshness">Свежесть итогового статуса: <c>fresh</c>, <c>aging</c>, <c>stale</c>.</param>
/// <param name="ObservedAt">Время наблюдения-победителя.</param>
/// <param name="SourceType">Тип источника наблюдения.</param>
/// <param name="ObservationCount">Сколько актуальных наблюдений учтено.</param>
public sealed record MapFeatureRow(
    Guid Id,
    string Name,
    string CoverageKind,
    Guid? CategoryId,
    Guid? ObjectTypeId,
    Guid? GroupId,
    string? Address,
    JsonElement Geometry,
    Guid? SourceStatusId,
    string? StatusCode,
    string? StatusName,
    string? StatusColor,
    string? StatusIcon,
    string? Freshness,
    DateTimeOffset? ObservedAt,
    string? SourceType,
    int ObservationCount);
