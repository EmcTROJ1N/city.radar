using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;

namespace BuildingBlocks.Security;

/// <summary>
/// Мастер-ключи шифрования секретов: только файл/Docker secret, не БД и не
/// репозиторий. Версия текущего ключа задаётся <c>MAYAK_MASTER_KEY_VERSION</c> (по умолчанию 1),
/// предыдущий ключ — файлом в <c>MAYAK_MASTER_KEY_FILE_PREV</c> и получает версию на единицу
/// меньше (расшифровка старых секретов).
/// </summary>
/// <remarks>
/// Порядок ротации: новый ключ кладётся в <c>MAYAK_MASTER_KEY_FILE</c>, старый — в
/// <c>MAYAK_MASTER_KEY_FILE_PREV</c>, версия увеличивается на 1. Старые секреты
/// (<c>secrets.key_version</c> = предыдущая версия) читаются старым ключом, новые пишутся новым;
/// после перезаписи всех секретов через админку предыдущий ключ можно убрать.
/// Формат ключа однозначен: файл содержит base64 ровно 32 байт (AES-256). Пробелы и переводы
/// строк по краям обрезаются; любой другой формат (сырые байты, base64 другой длины, мусор) —
/// <see cref="InvalidOperationException"/> с понятным сообщением. Текст ключа никогда не попадает
/// в сообщения об ошибках.
/// </remarks>
public sealed class MasterKeyProvider : IDisposable
{
    /// <summary>Размер ключа AES-256 в байтах.</summary>
    public const int KeySize = 32;

    /// <summary>Переменная окружения с путём к файлу текущего мастер-ключа.</summary>
    public const string KeyFileVariable = "MAYAK_MASTER_KEY_FILE";

    /// <summary>Переменная окружения с путём к файлу предыдущего мастер-ключа (ротация).</summary>
    public const string PreviousKeyFileVariable = "MAYAK_MASTER_KEY_FILE_PREV";

    /// <summary>Переменная окружения с версией текущего ключа (целое ≥ 1, по умолчанию 1).</summary>
    public const string KeyVersionVariable = "MAYAK_MASTER_KEY_VERSION";

    /// <summary>Ключ конфигурации с base64-ключом; допустим только вне Production.</summary>
    public const string ConfigurationKey = "Security:MasterKey";

    private const int CurrentVersion = 1;

    private readonly Dictionary<int, byte[]> _keys;
    private bool _disposed;

    private MasterKeyProvider(Dictionary<int, byte[]> keys)
    {
        _keys = keys;
        CurrentKeyVersion = keys.Keys.Max();
    }

    /// <summary>Максимальная загруженная версия ключа: ею шифруются новые секреты.</summary>
    public int CurrentKeyVersion { get; }

    /// <summary>
    /// Читает текущий (и, если задан, предыдущий) мастер-ключ из файлов, пути к которым берутся из
    /// переменных окружения <see cref="KeyFileVariable"/> и <see cref="PreviousKeyFileVariable"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Переменная не задана, файл отсутствует/недоступен или содержимое не является base64 32 байт.
    /// </exception>
    public static MasterKeyProvider FromEnvironment()
    {
        var currentPath = ReadRequiredPath(KeyFileVariable);
        var previousPath = ReadOptionalPath(PreviousKeyFileVariable);
        var currentVersion = ReadCurrentVersion();
        var previousVersion = currentVersion - 1;

        var keys = new Dictionary<int, byte[]>(previousPath is null ? 1 : 2);
        if (previousPath is not null)
        {
            keys[previousVersion] = ReadKeyFile(previousPath, previousVersion, PreviousKeyFileVariable);
        }

        keys[currentVersion] = ReadKeyFile(currentPath, currentVersion, KeyFileVariable);

        return new MasterKeyProvider(keys);
    }

    /// <summary>
    /// Ключи для Development/Testing: файл из переменных окружения, если он задан; иначе
    /// base64-ключ из <see cref="ConfigurationKey"/>; иначе случайный ключ на процесс.
    /// </summary>
    /// <remarks>
    /// Со случайным ключом секреты не переживают перезапуск процесса — это допустимо только в
    /// разработке и тестах. В Production используйте <see cref="FromEnvironment"/>.
    /// </remarks>
    public static MasterKeyProvider FromEnvironmentOrGenerate(IConfiguration? configuration)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyFileVariable)))
        {
            return FromEnvironment();
        }

        var configured = configuration?[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return FromBase64(configured);
        }

        return Generate();
    }

    /// <summary>Создаёт провайдер с одним ключом из base64-строки.</summary>
    /// <exception cref="InvalidOperationException">
    /// Строка не является base64 ровно 32 байт.
    /// </exception>
    public static MasterKeyProvider FromBase64(string base64, int keyVersion = CurrentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);

        var key = ParseBase64(base64, keyVersion, "строка base64");
        return new MasterKeyProvider(new Dictionary<int, byte[]> { [keyVersion] = key });
    }

    /// <summary>Создаёт провайдер с ключом из файла.</summary>
    /// <exception cref="InvalidOperationException">
    /// Файл отсутствует/недоступен или содержит не base64 ровно 32 байт.
    /// </exception>
    public static MasterKeyProvider FromFile(string path, int keyVersion = CurrentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var key = ReadKeyFile(path, keyVersion, KeyFileVariable);
        return new MasterKeyProvider(new Dictionary<int, byte[]> { [keyVersion] = key });
    }

    /// <summary>Случайный ключ версии 1, живущий только в памяти процесса.</summary>
    public static MasterKeyProvider Generate()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        return new MasterKeyProvider(new Dictionary<int, byte[]> { [CurrentVersion] = key });
    }

    /// <summary>Возвращает ключ запрошенной версии; массив принадлежит провайдеру.</summary>
    /// <exception cref="UnknownKeyVersionException">Версия не загружена.</exception>
    internal ReadOnlySpan<byte> GetKey(int keyVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_keys.TryGetValue(keyVersion, out var key))
        {
            throw new UnknownKeyVersionException(
                keyVersion,
                _keys.Keys.Order().ToArray());
        }

        return key;
    }

    /// <summary>Обнуляет все загруженные ключи.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var key in _keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _keys.Clear();
    }

    private static string ReadRequiredPath(string variable)
    {
        var path = ReadOptionalPath(variable);
        if (path is null)
        {
            throw new InvalidOperationException(
                $"Не задана переменная окружения {variable}: мастер-ключ шифрования секретов " +
                "должен лежать в файле/Docker secret вне репозитория.");
        }

        return path;
    }

    private static string? ReadOptionalPath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static byte[] ReadKeyFile(string path, int keyVersion, string variableName)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"Файл мастер-ключа не найден: {path}. Проверьте {variableName}.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Не удалось прочитать файл мастер-ключа {path}: {exception.Message}",
                exception);
        }

        return ParseBase64(content, keyVersion, $"файл мастер-ключа {path}");
    }

    private static byte[] ParseBase64(string content, int keyVersion, string source)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(
                $"{source}: пустое содержимое, ожидается base64 ровно {KeySize} байт (AES-256).");
        }

        // Буфер под декодирование с запасом; после декодирования он обнуляется, как и при ошибке.
        var buffer = new byte[((trimmed.Length / 4) + 1) * 3];
        if (!Convert.TryFromBase64String(trimmed, buffer, out var written))
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidOperationException(
                $"{source}: содержимое не является корректным base64. " +
                $"Ожидается base64 ровно {KeySize} байт (AES-256), без сырых байтов.");
        }

        if (written != KeySize)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidOperationException(
                $"{source}: ключ версии {keyVersion} длиной {written} байт, " +
                $"а требуется ровно {KeySize} байт (AES-256).");
        }

        var key = buffer.AsSpan(0, written).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return key;
    }

    /// <summary>
    /// Версия текущего ключа из <see cref="KeyVersionVariable"/>. По умолчанию 1; при ротации
    /// версия увеличивается, а старый ключ попадает в <see cref="PreviousKeyFileVariable"/>.
    /// </summary>
    private static int ReadCurrentVersion()
    {
        var raw = Environment.GetEnvironmentVariable(KeyVersionVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CurrentVersion;
        }

        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidOperationException(
                $"{KeyVersionVariable}: ожидается целое число ≥ 1, получено '{raw.Trim()}'.");
        }

        return version;
    }
}
