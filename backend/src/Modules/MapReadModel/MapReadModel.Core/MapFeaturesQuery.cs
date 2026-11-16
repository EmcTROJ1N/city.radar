using System.Globalization;

namespace MapReadModel.Core;

/// <summary>
/// Разобранные параметры <c>GET /map/features</c>.
/// Пустые списки означают «без фильтра»; <c>groups=none</c> включает в выдачу
/// фиксации без группы.
/// </summary>
/// <param name="Bbox">Видимая область карты.</param>
/// <param name="Zoom">Масштаб 0…22; влияет на упрощение полигонов.</param>
/// <param name="CategoryIds">Категории (через тип объекта); пусто — все.</param>
/// <param name="StatusCodes">Коды статусов источника; пусто — все.</param>
/// <param name="Kinds">Виды геометрии <c>point</c>/<c>polygon</c>; пусто — все.</param>
/// <param name="GroupIds">Идентификаторы групп; пусто — все.</param>
/// <param name="IncludeObjectsWithoutGroup">Добавлять фиксации без группы (<c>groups=none</c>).</param>
public sealed record MapFeaturesQuery(
    Bbox Bbox,
    int Zoom,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<string> StatusCodes,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<Guid> GroupIds,
    bool IncludeObjectsWithoutGroup)
{
    /// <summary>Масштаб по умолчанию, если <c>zoom</c> не передан.</summary>
    public const int DefaultZoom = 10;

    /// <summary>Минимальный масштаб из контракта.</summary>
    public const int MinZoom = 0;

    /// <summary>Максимальный масштаб из контракта.</summary>
    public const int MaxZoom = 22;

    /// <summary>Значение фильтра <c>groups</c> для фиксаций без группы.</summary>
    public const string NoGroupValue = "none";

    private static readonly string[] AllowedKinds = ["point", "polygon"];

    /// <summary>
    /// Разбирает query-параметры фильтров. Возвращает <c>false</c> и текст ошибки для 400,
    /// если <c>zoom</c> вне 0…22, категория/группа не UUID или вид геометрии неизвестен.
    /// </summary>
    /// <param name="bbox">Уже разобранная область (см. <see cref="BboxParser"/>).</param>
    /// <param name="zoom">Сырое значение <c>zoom</c>.</param>
    /// <param name="categories">Сырое значение <c>categories</c> (UUID через запятую).</param>
    /// <param name="statuses">Сырое значение <c>statuses</c> (коды через запятую).</param>
    /// <param name="kinds">Сырое значение <c>kinds</c> (<c>point,polygon</c>).</param>
    /// <param name="groups">Сырое значение <c>groups</c> (UUID и/или <c>none</c>).</param>
    /// <param name="query">Разобранные параметры при успехе.</param>
    /// <param name="error">Текст ошибки для Problem Details при неудаче.</param>
    public static bool TryCreate(
        Bbox bbox,
        int? zoom,
        string? categories,
        string? statuses,
        string? kinds,
        string? groups,
        out MapFeaturesQuery query,
        out string? error)
    {
        query = null!;
        error = null;

        var scale = zoom ?? DefaultZoom;
        if (scale is < MinZoom or > MaxZoom)
        {
            error = $"Параметр zoom должен быть от {MinZoom} до {MaxZoom}";

            return false;
        }

        var categoryIds = new List<Guid>();
        foreach (var item in SplitList(categories))
        {
            if (!Guid.TryParse(item, out var categoryId))
            {
                error = "Параметр categories должен содержать идентификаторы категорий через запятую";

                return false;
            }

            categoryIds.Add(categoryId);
        }

        var kindsList = new List<string>();
        foreach (var item in SplitList(kinds))
        {
            var kind = item.ToLowerInvariant();
            if (!AllowedKinds.Contains(kind, StringComparer.Ordinal))
            {
                error = "Параметр kinds принимает значения point и polygon";

                return false;
            }

            kindsList.Add(kind);
        }

        var statusCodes = SplitList(statuses);

        var groupIds = new List<Guid>();
        var includeWithoutGroup = false;
        foreach (var item in SplitList(groups))
        {
            if (string.Equals(item, NoGroupValue, StringComparison.OrdinalIgnoreCase))
            {
                includeWithoutGroup = true;
            }
            else if (Guid.TryParse(item, out var groupId))
            {
                groupIds.Add(groupId);
            }
            else
            {
                error = "Параметр groups принимает идентификаторы групп и значение none через запятую";

                return false;
            }
        }

        query = new MapFeaturesQuery(
            bbox,
            scale,
            categoryIds,
            statusCodes,
            kindsList,
            groupIds,
            includeWithoutGroup);

        return true;
    }

    /// <summary>
    /// Допуск упрощения полигона в градусах для масштаба: примерно один пиксель на широте
    /// региона (49°). На мелком масштабе контур огрубляется, на крупном (≥ 15) не меняется.
    /// </summary>
    /// <param name="zoom">Масштаб 0…22.</param>
    public static double SimplifyTolerance(int zoom)
    {
        if (zoom >= 15)
        {
            return 0.0;
        }

        const double WebMercatorMetersPerPixelAtZoom0 = 156543.03392;
        const double DegreesPerMeter = 1.0 / 111_320.0;
        const double RegionLatitudeCosine = 0.656; // cos(49°), ориентир ДНР.

        var metersPerPixel = WebMercatorMetersPerPixelAtZoom0
            * RegionLatitudeCosine
            / Math.Pow(2.0, zoom);

        return metersPerPixel * DegreesPerMeter;
    }

    /// <summary>
    /// Каноническое представление запроса для ETag: bbox с точностью 6 знаков,
    /// zoom и отсортированные (без дублей) списки фильтров. Порядок параметров и повторы
    /// в query-строке не влияют на результат — <c>?categories=A,B</c> и <c>?categories=B,A</c>
    /// дают одинаковый ETag.
    /// </summary>
    public string ToNormalizedString()
    {
        var categories = Normalize(CategoryIds.Select(id => id.ToString("D")));
        var statuses = Normalize(StatusCodes);
        var kinds = Normalize(Kinds);

        var groups = GroupIds.Select(id => id.ToString("D"));
        if (IncludeObjectsWithoutGroup)
        {
            groups = groups.Append(NoGroupValue);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"bbox={FormatCoordinate(Bbox.MinLon)},{FormatCoordinate(Bbox.MinLat)},{FormatCoordinate(Bbox.MaxLon)},{FormatCoordinate(Bbox.MaxLat)};zoom={Zoom};categories={categories};statuses={statuses};kinds={kinds};groups={Normalize(groups)}");
    }

    private static string Normalize(IEnumerable<string> values) =>
        string.Join(
            ',',
            values
                .OrderBy(value => value, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal));

    /// <summary>Округление координаты до 6 знаков без хвостовых нулей и «минус нуля».</summary>
    private static string FormatCoordinate(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);

        if (rounded == 0)
        {
            rounded = 0;
        }

        return rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static List<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"bbox={Bbox.MinLon},{Bbox.MinLat},{Bbox.MaxLon},{Bbox.MaxLat};zoom={Zoom}");
}
