namespace MapReadModel.Core;

/// <summary>
/// Прямоугольная область карты в WGS 84: <c>minLon,minLat,maxLon,maxLat</c>
///). Долгота растёт на восток, широта — на север, поэтому левый нижний угол
/// обязан быть меньше правого верхнего по обеим осям.
/// </summary>
/// <param name="MinLon">Минимальная долгота (западная граница).</param>
/// <param name="MinLat">Минимальная широта (южная граница).</param>
/// <param name="MaxLon">Максимальная долгота (восточная граница).</param>
/// <param name="MaxLat">Максимальная широта (северная граница).</param>
public readonly record struct Bbox(double MinLon, double MinLat, double MaxLon, double MaxLat)
{
    /// <summary>Ширина области в градусах.</summary>
    public double Width => MaxLon - MinLon;

    /// <summary>Высота области в градусах.</summary>
    public double Height => MaxLat - MinLat;
}
