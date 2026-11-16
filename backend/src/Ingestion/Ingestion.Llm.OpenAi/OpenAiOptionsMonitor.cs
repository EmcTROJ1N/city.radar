using Sources.Contracts;

namespace Ingestion.Llm.OpenAi;

/// <summary>
/// Кэш настроек LLM: ключ, base URL, модель и прокси читаются из зашифрованного
/// секрета через <see cref="ISecretReader"/> не чаще, чем раз в 30 секунд. Смена ключа или модели
/// в админке подхватывается без перезапуска Worker-а — по истечении TTL, а не при старте процесса.
/// </summary>
/// <remarks>
/// Кэш нужен только для того, чтобы не читать и не расшифровывать секрет на каждое сообщение:
/// сам HTTP-запрос идёт с актуальным ключом. Значения не логируются.
/// </remarks>
public sealed class OpenAiOptionsMonitor : IDisposable
{
    /// <summary>Срок жизни кэша настроек LLM; после него секрет перечитывается.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly ILlmSettingsSource _settingsSource;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LlmSettings? _settings;
    private DateTimeOffset _loadedAt;
    private bool _loaded;
    private bool _disposed;

    /// <summary>Создаёт монитор с TTL по умолчанию (30 секунд).</summary>
    public OpenAiOptionsMonitor(ILlmSettingsSource settingsSource, TimeProvider timeProvider)
        : this(settingsSource, timeProvider, DefaultCacheTtl)
    {
    }

    /// <summary>Создаёт монитор с явным TTL; используется тестами для проверки перечитывания.</summary>
    /// <param name="settingsSource">Чтение настроек LLM из секретов (в своём scope).</param>
    /// <param name="timeProvider">Источник времени: TTL проверяется по нему, а не по <c>DateTime.UtcNow</c>.</param>
    /// <param name="cacheTtl">Срок жизни кэша; должен быть положительным.</param>
    /// <exception cref="ArgumentOutOfRangeException">TTL не положительный.</exception>
    public OpenAiOptionsMonitor(
        ILlmSettingsSource settingsSource,
        TimeProvider timeProvider,
        TimeSpan cacheTtl)
    {
        ArgumentNullException.ThrowIfNull(settingsSource);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (cacheTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheTtl),
                cacheTtl,
                "Срок жизни кэша настроек LLM должен быть положительным.");
        }

        _settingsSource = settingsSource;
        _timeProvider = timeProvider;
        _cacheTtl = cacheTtl;
    }

    /// <summary>
    /// Возвращает настройки LLM: из кэша, пока не истёк TTL, иначе — свежие из секретов.
    /// <c>null</c> означает, что секрет LLM ещё не задан; это тоже кэшируется на TTL,
    /// чтобы админ мог задать ключ без перезапуска процесса.
    /// </summary>
    public async Task<LlmSettings?> GetAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_loaded && now - _loadedAt < _cacheTtl)
            {
                return _settings;
            }

            _settings = await _settingsSource.ReadAsync(ct).ConfigureAwait(false);
            _loadedAt = now;
            _loaded = true;

            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
