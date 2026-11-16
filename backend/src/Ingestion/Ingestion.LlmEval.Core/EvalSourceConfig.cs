using System.Text.Json;

using Catalog.Contracts;

using Ingestion.Pipeline;

using Sources.Contracts;

namespace Ingestion.LlmEval.Core;

/// <summary>Статус источника в конфигурации набора.</summary>
/// <param name="Code">Код статуса, совпадающий с <c>source_statuses.code</c>.</param>
/// <param name="Name">Название для человека.</param>
/// <param name="Description">Описание для LLM.</param>
/// <param name="FakeKeywords">
/// Подстроки для эвристического fake-провайдера (<c>--provider fake</c>). На реальный pipeline
/// не влияют: это только способ прогнать инструмент без ключа LLM.
/// </param>
public sealed record EvalStatusConfig(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string> FakeKeywords);

/// <summary>Фиксация источника в конфигурации набора.</summary>
/// <param name="Code">Короткий код фиксации для набора и отчёта; в JSON-наборе — <c>objectCode</c>.</param>
/// <param name="Name">Название фиксации, попадающее в системный промпт.</param>
/// <param name="Address">Адрес; <c>null</c>, если не задан.</param>
/// <param name="FakeKeywords">Подстроки для эвристического fake-провайдера.</param>
public sealed record EvalObjectConfig(
    string Code,
    string Name,
    string? Address,
    IReadOnlyList<string> FakeKeywords);

/// <summary>
/// Конфигурация источника для прогона набора: статусы с кодами и описаниями, фиксации
/// с кодами и названиями, режим определения фиксации по тексту, порог confidence и необязательный
/// префильтр. Собирается из JSON (<c>--sources</c>) либо из встроенного примера.
/// </summary>
/// <remarks>
/// Идентификаторы статусов и фиксаций не хранятся в JSON: раннер выводит их детерминированно
/// из кода, поэтому отчёт воспроизводим и не зависит от содержимого БД.
/// </remarks>
public sealed record EvalSourceConfig(
    Guid SourceId,
    string Name,
    string UserPrompt,
    bool LinkByText,
    double ConfidenceThreshold,
    IReadOnlyList<EvalStatusConfig> Statuses,
    IReadOnlyList<EvalObjectConfig> Objects,
    PrefilterConfig? Prefilter)
{
    /// <summary>Идентификатор источника встроенного примера.</summary>
    public static readonly Guid DefaultSourceId = Guid.Parse("9f1c7d2e-4b8a-4c3d-9e5f-1a2b3c4d5e6f");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Встроенный пример конфигурации (водоснабжение, два района, три статуса). Совпадает
    /// с <c>datasets/llm-eval/sample.sources.json</c>; используется, если <c>--sources</c> не задан.
    /// </summary>
    public static EvalSourceConfig Default { get; } = Parse(DefaultJson, "встроенная конфигурация");

    /// <summary>
    /// Загружает конфигурацию источника из JSON-файла.
    /// </summary>
    /// <param name="path">Путь к файлу <c>--sources</c>.</param>
    /// <exception cref="EvalConfigurationException">Файл не найден или конфигурация некорректна.</exception>
    public static EvalSourceConfig LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new EvalConfigurationException("Не задан путь к конфигурации источника (--sources).");
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EvalConfigurationException(
                $"Не удалось прочитать конфигурацию источника \"{path}\": {ex.Message}",
                ex);
        }

        return Parse(text, path);
    }

    /// <summary>Разбирает и проверяет JSON конфигурации источника.</summary>
    /// <param name="json">Содержимое конфигурации.</param>
    /// <param name="origin">Имя файла для сообщений об ошибках.</param>
    /// <exception cref="EvalConfigurationException">JSON или значения полей некорректны.</exception>
    public static EvalSourceConfig Parse(string json, string origin)
    {
        SourceConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SourceConfigDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new EvalConfigurationException(
                $"Конфигурация источника \"{origin}\" не является корректным JSON: {ex.Message}",
                ex);
        }

        if (dto is null)
        {
            throw new EvalConfigurationException(
                $"Конфигурация источника \"{origin}\" пуста.");
        }

        var statuses = ReadStatuses(dto.Statuses, origin);
        var objects = ReadObjects(dto.Objects, origin);

        var threshold = dto.ConfidenceThreshold ?? 0.7;
        if (!double.IsFinite(threshold) || threshold is < 0d or > 1d)
        {
            throw new EvalConfigurationException(
                $"Конфигурация источника \"{origin}\": confidenceThreshold должен быть числом от 0 до 1.");
        }

        var sourceId = dto.SourceId ?? DefaultSourceId;
        if (sourceId == Guid.Empty)
        {
            sourceId = DefaultSourceId;
        }

        var prefilter = ReadPrefilter(dto.Prefilter);

        return new EvalSourceConfig(
            sourceId,
            string.IsNullOrWhiteSpace(dto.Name) ? "Пример источника" : dto.Name.Trim(),
            dto.UserPrompt?.Trim() ?? string.Empty,
            dto.LinkByText ?? true,
            threshold,
            statuses,
            objects,
            prefilter);
    }

    /// <summary>Строит снимок конфигурации для <see cref="MessagePipeline"/>.</summary>
    public SourceRuntimeSnapshot ToSnapshot()
    {
        var statuses = Statuses
            .Select(status => new SourceStatusInfo(
                EvalIdentity.For(SourceId, "status", status.Code),
                status.Code,
                status.Name,
                status.Description,
                "#6B7280",
                "circle"))
            .ToArray();

        var linkedObjects = Objects
            .Select(mapObject => new LinkedObject(
                EvalIdentity.For(SourceId, "object", mapObject.Code),
                mapObject.Name,
                mapObject.Address))
            .ToArray();

        return new SourceRuntimeSnapshot(
            SourceId,
            statuses,
            UserPrompt,
            LinkByText,
            linkedObjects,
            ConfidenceThreshold,
            Prefilter);
    }

    /// <summary>Идентификатор фиксации по её коду из конфигурации.</summary>
    /// <param name="code">Код фиксации.</param>
    /// <exception cref="EvalConfigurationException">Код отсутствует в конфигурации.</exception>
    public Guid ObjectId(string code) =>
        TryObjectId(code)
        ?? throw new EvalConfigurationException(
            $"В конфигурации источника \"{Name}\" нет фиксации с кодом \"{code}\".");

    /// <summary>Идентификатор фиксации по коду; <c>null</c>, если кода нет в конфигурации.</summary>
    /// <param name="code">Код фиксации.</param>
    public Guid? TryObjectId(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        foreach (var mapObject in Objects)
        {
            if (string.Equals(mapObject.Code, code, StringComparison.Ordinal))
            {
                return EvalIdentity.For(SourceId, "object", mapObject.Code);
            }
        }

        return null;
    }

    /// <summary>Код фиксации по её идентификатору; <c>null</c>, если идентификатор чужой.</summary>
    /// <param name="id">Идентификатор фиксации.</param>
    public string? ObjectCode(Guid id)
    {
        foreach (var mapObject in Objects)
        {
            if (EvalIdentity.For(SourceId, "object", mapObject.Code) == id)
            {
                return mapObject.Code;
            }
        }

        return null;
    }

    /// <summary>Есть ли в конфигурации фиксация с таким кодом.</summary>
    /// <param name="code">Код фиксации.</param>
    public bool HasObjectCode(string? code) =>
        code is not null && TryObjectId(code) is not null;

    /// <summary>Есть ли в конфигурации статус с таким кодом.</summary>
    /// <param name="code">Код статуса.</param>
    public bool HasStatusCode(string? code)
    {
        if (code is null)
        {
            return false;
        }

        foreach (var status in Statuses)
        {
            if (string.Equals(status.Code, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<EvalStatusConfig> ReadStatuses(
        List<StatusDto>? items,
        string origin)
    {
        if (items is not { Count: > 0 })
        {
            throw new EvalConfigurationException(
                $"Конфигурация источника \"{origin}\": список statuses пуст — pipeline не сможет принять ни один ответ.");
        }

        var result = new List<EvalStatusConfig>(items.Count);
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var code = item.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                throw new EvalConfigurationException(
                    $"Конфигурация источника \"{origin}\": у статуса не задан code.");
            }

            if (!codes.Add(code))
            {
                throw new EvalConfigurationException(
                    $"Конфигурация источника \"{origin}\": код статуса \"{code}\" повторяется.");
            }

            result.Add(new EvalStatusConfig(
                code,
                string.IsNullOrWhiteSpace(item.Name) ? code : item.Name.Trim(),
                item.Description?.Trim() ?? string.Empty,
                ReadKeywords(item.FakeKeywords)));
        }

        return result;
    }

    private static IReadOnlyList<EvalObjectConfig> ReadObjects(
        List<ObjectDto>? items,
        string origin)
    {
        if (items is not { Count: > 0 })
        {
            throw new EvalConfigurationException(
                $"Конфигурация источника \"{origin}\": список objects пуст — pipeline отбросит все сообщения.");
        }

        var result = new List<EvalObjectConfig>(items.Count);
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var code = item.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                throw new EvalConfigurationException(
                    $"Конфигурация источника \"{origin}\": у фиксации не задан code.");
            }

            if (!codes.Add(code))
            {
                throw new EvalConfigurationException(
                    $"Конфигурация источника \"{origin}\": код фиксации \"{code}\" повторяется.");
            }

            result.Add(new EvalObjectConfig(
                code,
                string.IsNullOrWhiteSpace(item.Name) ? code : item.Name.Trim(),
                string.IsNullOrWhiteSpace(item.Address) ? null : item.Address.Trim(),
                ReadKeywords(item.FakeKeywords)));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadKeywords(List<string>? keywords)
    {
        if (keywords is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                result.Add(keyword.Trim());
            }
        }

        return result;
    }

    private static PrefilterConfig? ReadPrefilter(PrefilterDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var keywords = ReadKeywords(dto.Keywords);
        var regexes = ReadKeywords(dto.Regexes);

        return keywords.Count == 0 && regexes.Count == 0
            ? null
            : new PrefilterConfig(keywords, regexes);
    }

    private sealed class SourceConfigDto
    {
        public Guid? SourceId { get; set; }

        public string? Name { get; set; }

        public string? UserPrompt { get; set; }

        public bool? LinkByText { get; set; }

        public double? ConfidenceThreshold { get; set; }

        public List<StatusDto>? Statuses { get; set; }

        public List<ObjectDto>? Objects { get; set; }

        public PrefilterDto? Prefilter { get; set; }
    }

    private sealed class StatusDto
    {
        public string? Code { get; set; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<string>? FakeKeywords { get; set; }
    }

    private sealed class ObjectDto
    {
        public string? Code { get; set; }

        public string? Name { get; set; }

        public string? Address { get; set; }

        public List<string>? FakeKeywords { get; set; }
    }

    private sealed class PrefilterDto
    {
        public List<string>? Keywords { get; set; }

        public List<string>? Regexes { get; set; }
    }

    private const string DefaultJson =
        """
        {
          "sourceId": "9f1c7d2e-4b8a-4c3d-9e5f-1a2b3c4d5e6f",
          "name": "Водоканал (пример)",
          "userPrompt": "Ищи сообщения о наличии, отсутствии или ограничении подачи воды.",
          "linkByText": true,
          "confidenceThreshold": 0.7,
          "statuses": [
            {
              "code": "water_ok",
              "name": "Вода есть",
              "description": "Подача воды идёт в обычном режиме, отключений нет.",
              "fakeKeywords": ["вода есть", "воду дали", "подача восстановлена", "вода появилась", "холодная есть"]
            },
            {
              "code": "no_water",
              "name": "Нет воды",
              "description": "Подача воды прекращена, воды нет.",
              "fakeKeywords": ["нет воды", "воды нет", "воду отключили", "отключение воды", "без воды", "отключили воду"]
            },
            {
              "code": "water_limited",
              "name": "Ограничение подачи",
              "description": "Вода подаётся по графику или с пониженным давлением.",
              "fakeKeywords": ["по графику", "слабое давление", "ограничили", "ограничение подачи", "вода по часам", "напор слабый", "горячую воду"]
            }
          ],
          "objects": [
            {
              "code": "center",
              "name": "Центральный район",
              "address": "ул. Ленина, 1–20",
              "fakeKeywords": ["центр", "ленина"]
            },
            {
              "code": "north",
              "name": "Северный район",
              "address": "ул. Мира, 1–15",
              "fakeKeywords": ["север", "мира"]
            }
          ],
          "prefilter": {
            "keywords": [],
            "regexes": []
          }
        }
        """;
}
