using System.Globalization;
using System.Text.Json;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Загрузка размеченного набора в формате JSONL: одна строка — один случай,
/// пустые строки пропускаются. Формат строки:
/// <c>{"text":"...","expected":{"relevant":true,"status":"no_water","objectCode":"center"}}</c>.
/// </summary>
/// <remarks>
/// Ошибка в любой строке — это <see cref="EvalDatasetException"/> с номером строки, а не молчаливый
/// пропуск: набор готовит заказчик, и опечатка в разметке должна быть видна сразу.
/// </remarks>
public static class EvalDataset
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Читает и проверяет набор из файла.</summary>
    /// <param name="path">Путь к <c>.jsonl</c>.</param>
    /// <exception cref="EvalConfigurationException">Файл не найден или недоступен.</exception>
    /// <exception cref="EvalDatasetException">Строка набора некорректна.</exception>
    public static IReadOnlyList<EvalCase> LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new EvalConfigurationException("Не задан путь к набору (--dataset).");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EvalConfigurationException(
                $"Не удалось прочитать набор \"{path}\": {ex.Message}",
                ex);
        }

        return Parse(lines, path);
    }

    /// <summary>Разбирает строки набора; удобно для тестов и для чтения из потока.</summary>
    /// <param name="lines">Строки JSONL без ограничения на пустые.</param>
    /// <param name="origin">Имя файла или источника для сообщений об ошибках.</param>
    /// <exception cref="EvalDatasetException">Строка набора некорректна.</exception>
    public static IReadOnlyList<EvalCase> Parse(IReadOnlyList<string> lines, string origin)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var cases = new List<EvalCase>(lines.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = ParseLine(line, origin, lineNumber);

            if (!ids.Add(parsed.Id))
            {
                throw new EvalDatasetException(
                    origin,
                    lineNumber,
                    $"идентификатор случая \"{parsed.Id}\" повторяется; поле id должно быть уникальным.");
            }

            cases.Add(parsed);
        }

        if (cases.Count == 0)
        {
            throw new EvalConfigurationException(
                $"Набор \"{origin}\" пуст: нужна хотя бы одна непустая JSONL-строка.");
        }

        return cases;
    }

    /// <summary>
    /// Сверяет коды статусов и фиксаций из разметки с конфигурацией источника. Опечатка
    /// в <c>objectCode</c> или <c>status</c> — ошибка набора, а не «модель ошиблась».
    /// </summary>
    /// <param name="cases">Разобранные случаи.</param>
    /// <param name="source">Конфигурация источника, на которой будет прогон.</param>
    /// <exception cref="EvalConfigurationException">Код из набора отсутствует в конфигурации.</exception>
    public static void ValidateAgainst(IReadOnlyList<EvalCase> cases, EvalSourceConfig source)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(source);

        foreach (var item in cases)
        {
            var expected = item.Expected;

            if (expected.Status is not null && !source.HasStatusCode(expected.Status))
            {
                throw new EvalConfigurationException(
                    $"Случай \"{item.Id}\": статус \"{expected.Status}\" отсутствует в конфигурации источника. " +
                    "Допустимые коды: " + string.Join(", ", source.Statuses.Select(status => status.Code)) + ".");
            }

            if (expected.ObjectCode is not null && !source.HasObjectCode(expected.ObjectCode))
            {
                throw new EvalConfigurationException(
                    $"Случай \"{item.Id}\": фиксация \"{expected.ObjectCode}\" отсутствует в конфигурации источника. " +
                    "Допустимые коды: " + string.Join(", ", source.Objects.Select(mapObject => mapObject.Code)) + ".");
            }
        }
    }

    private static EvalCase ParseLine(string line, string origin, int lineNumber)
    {
        DatasetLineDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DatasetLineDto>(line, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new EvalDatasetException(
                origin,
                lineNumber,
                $"строка не является корректным JSON ({ex.Message}).");
        }

        if (dto is null)
        {
            throw Error(origin, lineNumber, "строка не содержит JSON-объект.");
        }

        var text = dto.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Error(origin, lineNumber, "поле \"text\" обязательно и не может быть пустым.");
        }

        if (dto.Expected is null)
        {
            throw Error(origin, lineNumber, "поле \"expected\" обязательно.");
        }

        var expected = dto.Expected;
        if (expected.Relevant is null)
        {
            throw Error(origin, lineNumber, "поле \"expected.relevant\" обязательно и должно быть boolean.");
        }

        var relevant = expected.Relevant.Value;
        var status = NormalizeCode(expected.Status);
        var objectCode = NormalizeCode(expected.ObjectCode);

        if (!relevant && status is not null)
        {
            throw Error(
                origin,
                lineNumber,
                "для случая с relevant = false поле \"expected.status\" должно быть null: " +
                "нерелевантное сообщение не имеет статуса.");
        }

        if (!relevant && objectCode is not null)
        {
            throw Error(
                origin,
                lineNumber,
                "для случая с relevant = false поле \"expected.objectCode\" должно быть null.");
        }

        var id = NormalizeCode(dto.Id)
            ?? string.Create(CultureInfo.InvariantCulture, $"case-{lineNumber}");

        return new EvalCase(
            id,
            text,
            new EvalExpectation(relevant, status, objectCode),
            NormalizeCode(dto.Note));
    }

    private static string? NormalizeCode(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static EvalDatasetException Error(string origin, int lineNumber, string reason) =>
        new(origin, lineNumber, reason);

    private sealed class DatasetLineDto
    {
        public string? Id { get; set; }

        public string? Text { get; set; }

        public ExpectedDto? Expected { get; set; }

        public string? Note { get; set; }
    }

    private sealed class ExpectedDto
    {
        public bool? Relevant { get; set; }

        public string? Status { get; set; }

        public string? ObjectCode { get; set; }
    }
}
