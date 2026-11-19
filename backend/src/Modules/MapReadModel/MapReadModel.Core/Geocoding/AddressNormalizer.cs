using System.Text.RegularExpressions;

namespace MapReadModel.Core.Geocoding;

/// <summary>
/// Ключ адреса: разные написания одного дома («ул. Тренёва, 3в», «Тренева 3в») дают один ключ —
/// по нему группируются сообщения и кэшируется геокодирование.
/// </summary>
public static partial class AddressNormalizer
{
    /// <summary>Нижний регистр, <c>ё→е</c>, без слов-типов улицы, без запятых и лишних пробелов.</summary>
    /// <param name="address">Адрес из сообщения.</param>
    public static string Normalize(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var value = address.ToLowerInvariant().Replace('ё', 'е');
        value = StreetTypeWords().Replace(value, " ");
        value = Separators().Replace(value, " ");

        return value.Trim();
    }

    [GeneratedRegex(@"(?<!\p{L})(ул\.?|улица|пр-т|пр\.|проспект|пер\.?|переулок|бул\.?|бульвар|пл\.?|площадь)(?!\p{L})")]
    private static partial Regex StreetTypeWords();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex Separators();
}
