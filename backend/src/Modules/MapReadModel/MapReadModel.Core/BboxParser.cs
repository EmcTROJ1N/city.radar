using System.Globalization;
using System.Text.RegularExpressions;

namespace MapReadModel.Core;

/// <summary>
/// Разбор query-параметра <c>bbox</c> (OpenAPI <c>/map/features</c>):
/// четыре числа через запятую в порядке <c>minLon,minLat,maxLon,maxLat</c>.
/// Неверный формат, перевёрнутые границы, координаты вне WGS 84 и слишком большая
/// область отклоняются — эндпоинт отвечает 400.
/// </summary>
public static partial class BboxParser
{
    /// <summary>
    /// Максимальная ширина и высота области в градусах. Ориентир — регион ДНР; запас ×2
    /// допускает захват соседних территорий при отдалении карты, но не всю планету.
    /// </summary>
    public const double MaxSpanDegrees = 6.0;

    /// <summary>Диапазон допустимых долгот (WGS 84).</summary>
    public const double MinLongitude = -180.0;

    /// <summary>Диапазон допустимых долгот (WGS 84).</summary>
    public const double MaxLongitude = 180.0;

    /// <summary>Диапазон допустимых широт (WGS 84).</summary>
    public const double MinLatitude = -90.0;

    /// <summary>Диапазон допустимых широт (WGS 84).</summary>
    public const double MaxLatitude = 90.0;

    /// <summary>
    /// Пытается разобрать <c>bbox</c>. Возвращает <c>false</c> для пустого, неполного или
    /// нечислового значения, для перевёрнутых границ, координат вне WGS 84, нулевой и
    /// чрезмерно большой области (ширина или высота больше <see cref="MaxSpanDegrees"/>).
    /// </summary>
    /// <param name="value">Сырое значение query-параметра.</param>
    /// <param name="bbox">Разобранная область при успехе.</param>
    public static bool TryParse(string? value, out Bbox bbox)
    {
        bbox = default;

        if (string.IsNullOrWhiteSpace(value) || !BboxPattern().IsMatch(value))
        {
            return false;
        }

        var parts = value.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        var numbers = new double[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(
                    parts[index],
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out numbers[index])
                || !double.IsFinite(numbers[index]))
            {
                return false;
            }
        }

        var candidate = new Bbox(numbers[0], numbers[1], numbers[2], numbers[3]);

        if (candidate.MinLon < MinLongitude || candidate.MaxLon > MaxLongitude
            || candidate.MinLat < MinLatitude || candidate.MaxLat > MaxLatitude)
        {
            return false;
        }

        // Строгое неравенство: вырожденная область (нулевая ширина или высота) бессмысленна.
        if (candidate.MinLon >= candidate.MaxLon || candidate.MinLat >= candidate.MaxLat)
        {
            return false;
        }

        if (candidate.Width > MaxSpanDegrees || candidate.Height > MaxSpanDegrees)
        {
            return false;
        }

        bbox = candidate;

        return true;
    }

    /// <summary>Формат из контракта: четыре числа через запятую, без пробелов и экспоненты.</summary>
    [GeneratedRegex(@"^-?\d+(\.\d+)?(,-?\d+(\.\d+)?){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex BboxPattern();
}
