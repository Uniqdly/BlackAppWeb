using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlackScholesApp.Models;
using ILogger = Serilog.ILogger;

namespace BlackScholesApp.Services;

public interface IOptionSearchService
{
    Task<List<OptionSuggestion>> SearchAsync(string query, CancellationToken ct = default);
    Task<List<OptionSuggestion>> GetPopularAsync(CancellationToken ct = default);
}

/// <summary>
/// Поиск опционов через ISS MOEX API.
/// Используем эндпоинт /iss/engines/futures/markets/options/securities.json
/// для получения реального списка торгуемых опционов.
/// </summary>
public class OptionSearchService : IOptionSearchService
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private const string BaseUrl = "https://iss.moex.com/iss";

    // Кэш результатов поиска на сессию (не сохраняется на диск)
    private readonly Dictionary<string, (List<OptionSuggestion> Items, DateTime At)> _searchCache = new();
    private List<OptionSuggestion>? _allOptionsCache;
    private DateTime _allOptionsCachedAt;

    public OptionSearchService(HttpClient http, ILogger log)
    {
        _http = http;
        _log  = log;
    }

    /// <summary>
    /// Ищет опционы по введённой строке (тикер, базовый актив, страйк)
    /// </summary>
    public async Task<List<OptionSuggestion>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return await GetPopularAsync(ct);

        query = query.Trim().ToUpperInvariant();

        // Проверяем кэш
        if (_searchCache.TryGetValue(query, out var cached) &&
            DateTime.Now - cached.At < TimeSpan.FromMinutes(5))
            return cached.Items;

        var all = await GetAllOptionsAsync(ct);
        var result = all
            .Where(o =>
                o.Ticker.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                o.Underlying.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Ticker)
            .Take(50)
            .ToList();

        _searchCache[query] = (result, DateTime.Now);
        return result;
    }

    /// <summary>
    /// Возвращает популярные/часто используемые опционы для начального списка
    /// </summary>
    public async Task<List<OptionSuggestion>> GetPopularAsync(CancellationToken ct = default)
    {
        var all = await GetAllOptionsAsync(ct);

        // Приоритет: Si (доллар), RTS, GOLD, GAZP, SBER
        var priority = new[] { "SI", "RTS", "GOLD", "GZ", "GAZP", "SR", "SBER", "BR", "NG" };

        var result = all
            .OrderBy(o =>
            {
                for (int i = 0; i < priority.Length; i++)
                    if (o.Underlying.StartsWith(priority[i], StringComparison.OrdinalIgnoreCase) ||
                        o.Ticker.StartsWith(priority[i], StringComparison.OrdinalIgnoreCase))
                        return i;
                return priority.Length;
            })
            .ThenBy(o => o.Expiry)
            .ThenBy(o => o.Strike)
            .Take(100)
            .ToList();

        return result;
    }

    /// <summary>
    /// Загружает полный список опционов с MOEX (кэшируется на 10 минут)
    /// </summary>
    private async Task<List<OptionSuggestion>> GetAllOptionsAsync(CancellationToken ct)
    {
        if (_allOptionsCache != null && DateTime.Now - _allOptionsCachedAt < TimeSpan.FromMinutes(10))
            return _allOptionsCache;

        _log.Information("Loading full options list from MOEX...");
        var result = new List<OptionSuggestion>();

        try
        {
            // Загружаем список всех опционов на FORTS
            // start=0, count=100 (API ограничивает 100 записей за запрос)
            int start = 0;
            const int pageSize = 100;
            bool hasMore = true;

            while (hasMore && start < 500) // максимум 500 записей
            {
                var url = $"{BaseUrl}/engines/futures/markets/options/securities.json" +
                          $"?iss.meta=off&iss.only=securities&start={start}&securities.columns=" +
                          "SECID,SHORTNAME,ASSETCODE,STRIKE,LASTTRADEDATE,OPTIONTYPE";

                _log.Debug("Loading options page start={Start}: GET {Url}", start, url);
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);

                var root = doc.RootElement;
                if (!root.TryGetProperty("securities", out var sec)) break;

                var columns = GetColumns(sec);
                var rows    = GetRows(sec);

                if (rows.Count == 0) break;

                foreach (var row in rows)
                {
                    var s = ParseSuggestion(row, columns);
                    if (s != null) result.Add(s);
                }

                hasMore = rows.Count == pageSize;
                start  += pageSize;
            }

            _log.Information("Loaded {Count} options from MOEX", result.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load options list from MOEX, using fallback list");
            result = GetFallbackList();
        }

        // Если API ничего не вернул — используем заготовленный список
        if (result.Count == 0)
        {
            _log.Warning("Empty options list from API, using fallback");
            result = GetFallbackList();
        }

        _allOptionsCache   = result;
        _allOptionsCachedAt = DateTime.Now;
        return result;
    }

    private OptionSuggestion? ParseSuggestion(JsonElement row, List<string> columns)
    {
        try
        {
            string ticker     = GetString(row, columns, "SECID")        ?? string.Empty;
            string shortName  = GetString(row, columns, "SHORTNAME")    ?? string.Empty;
            string underlying = GetString(row, columns, "ASSETCODE")    ?? string.Empty;
            string optType    = GetString(row, columns, "OPTIONTYPE")   ?? string.Empty;
            string expiryStr  = GetString(row, columns, "LASTTRADEDATE") ?? string.Empty;
            double strike     = GetDouble(row, columns, "STRIKE");

            if (string.IsNullOrEmpty(ticker)) return null;

            // Определяем тип если не пришёл из API
            if (string.IsNullOrEmpty(optType))
            {
                char last = ticker[^1];
                optType = (last == 'P' || last == 'p') ? "PUT" : "CALL";
            }
            else
            {
                optType = optType.ToUpperInvariant().StartsWith("C") ? "CALL" : "PUT";
            }

            DateTime? expiry = null;
            if (DateTime.TryParse(expiryStr, out var dt)) expiry = dt;

            return new OptionSuggestion
            {
                Ticker      = ticker,
                OptionType  = optType,
                Underlying  = underlying,
                Strike      = strike,
                Expiry      = expiry,
                Description = shortName,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Резервный список популярных опционов на случай недоступности API
    /// </summary>
    private static List<OptionSuggestion> GetFallbackList()
    {
        var now = DateTime.Today;
        // Генерируем типичные тикеры для ближайших экспираций
        var list = new List<OptionSuggestion>();

        // Si (USD/RUB фьючерс) — самый ликвидный
        foreach (var (month, year, date) in GetNearestExpiries())
        {
            string code = $"{month:D2}.{year % 100:D2}";
            foreach (var (suffix, type) in new[] { ("C", "CALL"), ("P", "PUT") })
            {
                list.Add(new OptionSuggestion
                {
                    Ticker     = $"Si-{code}{suffix}",
                    OptionType = type,
                    Underlying = "Si",
                    Strike     = 0,
                    Expiry     = date,
                    Description = $"Опцион на Si (USD/RUB), {date:MMMM yyyy}",
                });
            }
        }

        // RTS
        foreach (var (month, year, date) in GetNearestExpiries())
        {
            string code = $"{month:D2}.{year % 100:D2}";
            foreach (var (suffix, type) in new[] { ("C", "CALL"), ("P", "PUT") })
            {
                list.Add(new OptionSuggestion
                {
                    Ticker     = $"RTS-{code}{suffix}",
                    OptionType = type,
                    Underlying = "RTS",
                    Strike     = 0,
                    Expiry     = date,
                    Description = $"Опцион на RTS (индекс), {date:MMMM yyyy}",
                });
            }
        }

        // GOLD
        foreach (var (month, year, date) in GetNearestExpiries().Take(2))
        {
            string code = $"{month:D2}.{year % 100:D2}";
            foreach (var (suffix, type) in new[] { ("C", "CALL"), ("P", "PUT") })
            {
                list.Add(new OptionSuggestion
                {
                    Ticker     = $"GOLD-{code}{suffix}",
                    OptionType = type,
                    Underlying = "GOLD",
                    Strike     = 0,
                    Expiry     = date,
                    Description = $"Опцион на золото, {date:MMMM yyyy}",
                });
            }
        }

        // BR (нефть Brent)
        foreach (var (month, year, date) in GetNearestExpiries().Take(2))
        {
            string code = $"{month:D2}.{year % 100:D2}";
            foreach (var (suffix, type) in new[] { ("C", "CALL"), ("P", "PUT") })
            {
                list.Add(new OptionSuggestion
                {
                    Ticker     = $"BR-{code}{suffix}",
                    OptionType = type,
                    Underlying = "BR",
                    Strike     = 0,
                    Expiry     = date,
                    Description = $"Опцион на нефть Brent, {date:MMMM yyyy}",
                });
            }
        }

        return list;
    }

    /// <summary>
    /// Возвращает 4 ближайших квартальных экспирации FORTS (март, июнь, сентябрь, декабрь)
    /// </summary>
    private static List<(int Month, int Year, DateTime Date)> GetNearestExpiries()
    {
        var result = new List<(int, int, DateTime)>();
        var today = DateTime.Today;
        int[] quarterMonths = { 3, 6, 9, 12 };

        int year = today.Year;
        foreach (int m in quarterMonths)
        {
            // Третья пятница месяца экспирации
            var expiry = GetThirdFriday(year, m);
            if (expiry >= today)
                result.Add((m, year, expiry));
        }
        // Если нашли меньше 4 — добавляем следующий год
        foreach (int m in quarterMonths)
        {
            if (result.Count >= 4) break;
            var expiry = GetThirdFriday(year + 1, m);
            result.Add((m, year + 1, expiry));
        }
        return result.Take(4).ToList();
    }

    private static DateTime GetThirdFriday(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        int daysUntilFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(daysUntilFriday + 14); // +14 = третья пятница
    }

    // Вспомогательные методы парсинга ISS JSON
    private static List<string> GetColumns(JsonElement block)
    {
        var r = new List<string>();
        if (block.TryGetProperty("columns", out var cols))
            foreach (var c in cols.EnumerateArray())
                r.Add(c.GetString() ?? string.Empty);
        return r;
    }

    private static List<JsonElement> GetRows(JsonElement block)
    {
        var r = new List<JsonElement>();
        if (block.TryGetProperty("data", out var data))
            foreach (var row in data.EnumerateArray())
                r.Add(row);
        return r;
    }

    private static string? GetString(JsonElement row, List<string> cols, string field)
    {
        int idx = cols.FindIndex(c => string.Equals(c, field, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || row.ValueKind != JsonValueKind.Array) return null;
        var arr = row.EnumerateArray().ToList();
        return idx < arr.Count && arr[idx].ValueKind != JsonValueKind.Null
            ? arr[idx].GetString() : null;
    }

    private static double GetDouble(JsonElement row, List<string> cols, string field)
    {
        int idx = cols.FindIndex(c => string.Equals(c, field, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || row.ValueKind != JsonValueKind.Array) return 0;
        var arr = row.EnumerateArray().ToList();
        if (idx >= arr.Count) return 0;
        if (arr[idx].ValueKind == JsonValueKind.Number) return arr[idx].GetDouble();
        if (arr[idx].ValueKind == JsonValueKind.String &&
            double.TryParse(arr[idx].GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v)) return v;
        return 0;
    }
}
