using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Детерминированные идентификаторы для прогона набора: один и тот же код фиксации или статуса
/// всегда даёт один и тот же <see cref="Guid"/> при том же источнике. Это делает отчёты
/// воспроизводимыми: сравнивать прогоны можно без базы данных.
/// </summary>
internal static class EvalIdentity
{
    /// <summary>Строит стабильный идентификатор из источника, вида сущности и кода.</summary>
    /// <param name="sourceId">Идентификатор источника.</param>
    /// <param name="kind">Вид сущности: <c>status</c>, <c>object</c> или <c>raw</c>.</param>
    /// <param name="code">Код сущности или номер сообщения.</param>
    public static Guid For(Guid sourceId, string kind, string code)
    {
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{sourceId:D}:{kind}:{code}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);

        // Версия 4 и вариант RFC 4122 — чтобы значение было похоже на обычный UUID.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
