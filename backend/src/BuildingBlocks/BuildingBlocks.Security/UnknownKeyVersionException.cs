namespace BuildingBlocks.Security;

/// <summary>
/// Запрошена версия мастер-ключа, которой нет в <see cref="MasterKeyProvider"/>: секрет записан
/// старым ключом, который уже не сконфигурирован, или указана несуществующая версия.
/// </summary>
public sealed class UnknownKeyVersionException : Exception
{
    /// <summary>Создаёт исключение для версии <paramref name="keyVersion"/>.</summary>
    /// <param name="keyVersion">Запрошенная версия ключа.</param>
    /// <param name="availableVersions">Загруженные версии ключа.</param>
    public UnknownKeyVersionException(int keyVersion, IReadOnlyCollection<int> availableVersions)
        : base(
            $"Неизвестная версия ключа шифрования секретов: {keyVersion}. " +
            $"Доступные версии: {string.Join(", ", availableVersions)}.")
    {
        KeyVersion = keyVersion;
        AvailableVersions = availableVersions;
    }

    /// <summary>Версия ключа, которой не оказалось.</summary>
    public int KeyVersion { get; }

    /// <summary>Загруженные версии ключа (по возрастанию).</summary>
    public IReadOnlyCollection<int> AvailableVersions { get; }
}
