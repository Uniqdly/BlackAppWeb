using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FractionalBlackScholes.Models;
using Microsoft.Extensions.Logging;

namespace FractionalBlackScholes.Services
{
    public interface IMoexApiService
    {
        Task<List<OptionSuggestion>> GetOptionSuggestionsAsync(string query, CancellationToken ct = default);
        Task<OptionData?> LoadOptionDataAsync(string ticker, bool forceRefresh = false, CancellationToken ct = default);
        Task<double> GetUnderlyingPriceAsync(string underlyingTicker, CancellationToken ct = default);

        /// <summary>Получить текущую дату/время из сети (worldtimeapi.org).</summary>
        Task<DateTime> GetNetworkDateAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Сервис для работы с MOEX ISS REST API.
    ///
    /// Ключевая особенность: текущая дата берётся из сети (worldtimeapi.org / timeapi.io),
    /// а не из системных часов, — чтобы фильтрация по экспирации была точной
    /// независимо от локальных настроек сервера.
    /// </summary>
    public class MoexApiService : IMoexApiService
    {
        private readonly HttpClient          _http;
        private readonly ICacheService       _cache;
        private readonly ILogger<MoexApiService> _logger;

        private const string BaseUrl = "https://iss.moex.com/iss";

        // Кэш сетевой даты: обновляем не чаще раза в 5 минут
        private DateTime _cachedNetworkDate    = DateTime.MinValue;
        private DateTime _networkDateFetchedAt = DateTime.MinValue;

        public MoexApiService(
            IHttpClientFactory factory,
            ICacheService cache,
            ILogger<MoexApiService> logger)
        {
            _http   = factory.CreateClient("moex");
            _cache  = cache;
            _logger = logger;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Текущая дата из сети
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc/>
        public async Task<DateTime> GetNetworkDateAsync(CancellationToken ct = default)
        {
            // Если недавно получали — отдаём из кэша
            if (_cachedNetworkDate != DateTime.MinValue &&
                (DateTime.UtcNow - _networkDateFetchedAt).TotalMinutes < 5)
            {
                return _cachedNetworkDate;
            }

            // Попытка 1: worldtimeapi.org (Moscow time)
            try
            {
                using var resp = await _http.GetAsync(
                    "http://worldtimeapi.org/api/timezone/Europe/Moscow", ct);

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("datetime", out var dtEl))
                    {
                        string? dtStr = dtEl.GetString();
                        if (DateTime.TryParse(dtStr, out var dt))
                        {
                            _cachedNetworkDate    = dt;
                            _networkDateFetchedAt = DateTime.UtcNow;
                            _logger.LogInformation("Сетевая дата (worldtimeapi): {Date}", dt.ToShortDateString());
                            return dt;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "worldtimeapi.org недоступен, пробуем запасной источник");
            }

            // Попытка 2: timeapi.io
            try
            {
                using var resp = await _http.GetAsync(
                    "https://timeapi.io/api/Time/current/zone?timeZone=Europe/Moscow", ct);

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    // Поля: year, month, day, hour, minute, seconds
                    if (doc.RootElement.TryGetProperty("year",  out var y) &&
                        doc.RootElement.TryGetProperty("month", out var mo) &&
                        doc.RootElement.TryGetProperty("day",   out var d))
                    {
                        var dt = new DateTime(y.GetInt32(), mo.GetInt32(), d.GetInt32());
                        _cachedNetworkDate    = dt;
                        _networkDateFetchedAt = DateTime.UtcNow;
                        _logger.LogInformation("Сетевая дата (timeapi.io): {Date}", dt.ToShortDateString());
                        return dt;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "timeapi.io недоступен, используем DateTime.Now");
            }

            // Фолбэк: системное время (с предупреждением)
            _logger.LogWarning("Не удалось получить дату из сети, используем системное время");
            return DateTime.Now;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Поиск опционов
        // ═══════════════════════════════════════════════════════════════════════

        public async Task<List<OptionSuggestion>> GetOptionSuggestionsAsync(
            string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                return new List<OptionSuggestion>();

            // Получаем текущую дату из сети для фильтрации по экспирации
            DateTime now = await GetNetworkDateAsync(ct);
            _logger.LogDebug("Фильтрация по дате: {Date}", now.ToShortDateString());

            List<OptionSuggestion> result;

            try
            {
                string url = $"{BaseUrl}/securities.json" +
                             $"?q={Uri.EscapeDataString(query)}" +
                             $"&engine=futures&market=options&limit=50&iss.meta=off";

                _logger.LogDebug("MOEX search: {Url}", url);

                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                result = ParseSecuritiesSearch(doc);

                if (result.Count == 0)
                    result = GetDemoSuggestions(query, now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MOEX поиск недоступен, возвращаем демо");
                result = GetDemoSuggestions(query, now);
            }

            // ── Фильтруем: только опционы с не истёкшей экспирацией ──
            var active = result.Where(s => s.IsActive(now)).ToList();
            _logger.LogDebug("Найдено {Total} опционов, активных: {Active}", result.Count, active.Count);

            return active;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Загрузка данных опциона
        // ═══════════════════════════════════════════════════════════════════════

        public async Task<OptionData?> LoadOptionDataAsync(
            string ticker, bool forceRefresh = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return null;

            ticker = ticker.Trim().ToUpperInvariant();

            if (!forceRefresh)
            {
                var cached = _cache.Get(ticker);
                if (cached != null)
                {
                    _logger.LogInformation("Опцион {Ticker} загружен из кэша", ticker);
                    return cached;
                }
            }

            OptionData? data = null;

            try
            {
                string url = $"{BaseUrl}/engines/futures/markets/options/securities/{ticker}.json" +
                             $"?iss.meta=off&iss.only=securities,marketdata";

                _logger.LogDebug("MOEX load option: {Url}", url);

                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                data = ParseOptionData(doc, ticker);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MOEX API недоступен для {Ticker}, используем демо", ticker);
            }

            if (data == null)
            {
                _logger.LogInformation("Парсинг не дал результата для {Ticker}, используем демо", ticker);
                data = GetDemoOptionData(ticker);
            }

            if (data != null && !string.IsNullOrEmpty(data.UnderlyingTicker) && data.UnderlyingPrice <= 0)
                data.UnderlyingPrice = await GetUnderlyingPriceAsync(data.UnderlyingTicker, ct);

            if (data != null)
                _cache.Set(ticker, data);

            return data;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Цена базового актива
        // ═══════════════════════════════════════════════════════════════════════

        public async Task<double> GetUnderlyingPriceAsync(
            string underlyingTicker, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(underlyingTicker)) return 0;

            try
            {
                string url = $"{BaseUrl}/engines/futures/markets/forts/securities/{underlyingTicker}.json" +
                             $"?iss.meta=off&iss.only=marketdata&marketdata.columns=SECID,LAST,SETTLEPRICE";

                using var r1 = await _http.GetAsync(url, ct);
                if (r1.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await r1.Content.ReadAsStringAsync(ct));
                    double price = ParseLastPrice(doc);
                    if (price > 0) return price;
                }

                url = $"{BaseUrl}/engines/stock/markets/shares/securities/{underlyingTicker}.json" +
                      $"?iss.meta=off&iss.only=marketdata&marketdata.columns=SECID,LAST";

                using var r2 = await _http.GetAsync(url, ct);
                if (r2.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await r2.Content.ReadAsStringAsync(ct));
                    return ParseLastPrice(doc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось загрузить цену базового актива {Ticker}", underlyingTicker);
            }

            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Парсинг JSON
        // ═══════════════════════════════════════════════════════════════════════

        private static List<OptionSuggestion> ParseSecuritiesSearch(JsonDocument doc)
        {
            var result = new List<OptionSuggestion>();
            try
            {
                var securities = doc.RootElement.GetProperty("securities");
                string[] cols  = GetColumns(securities);
                var data       = securities.GetProperty("data");

                int secIdx     = FindColumn(cols, "secid");
                int nameIdx    = FindColumn(cols, "shortname");
                int expiryIdx  = FindColumn(cols, "lastdeldate"); // дата экспирации

                foreach (var row in data.EnumerateArray())
                {
                    string ticker = GetString(row, secIdx);
                    string name   = GetString(row, nameIdx);
                    if (string.IsNullOrEmpty(ticker)) continue;

                    // Парсим дату экспирации из ответа поиска
                    DateTime expiration = DateTime.MaxValue;
                    string expStr = GetString(row, expiryIdx);
                    if (!string.IsNullOrEmpty(expStr))
                        DateTime.TryParse(expStr, out expiration);

                    // Если дата не в ответе — пробуем извлечь из тикера
                    // MOEX тикер вида RI65000BC5: последние цифры = месяц+год
                    if (expiration == DateTime.MaxValue)
                        expiration = ExtractExpirationFromTicker(ticker);

                    string optType = DetectOptionType(ticker, name);

                    result.Add(new OptionSuggestion
                    {
                        Ticker      = ticker,
                        Description = name,
                        OptionType  = optType,
                        Expiration  = expiration
                    });
                }
            }
            catch { }
            return result;
        }

        private OptionData? ParseOptionData(JsonDocument doc, string ticker)
        {
            try
            {
                var securities  = doc.RootElement.GetProperty("securities");
                string[] secCols = GetColumns(securities);
                var secData      = securities.GetProperty("data");

                bool hasRows = false;
                foreach (var _ in secData.EnumerateArray()) { hasRows = true; break; }
                if (!hasRows) { _logger.LogWarning("Пустой data для {Ticker}", ticker); return null; }

                var row = secData[0];

                int strikeIdx     = FindColumn(secCols, "strike");
                int expiryIdx     = FindColumn(secCols, "lastdeldate");
                int underlyingIdx = FindColumn(secCols, "assetcode");
                int optTypeIdx    = FindColumn(secCols, "optiontype");
                int shortNameIdx  = FindColumn(secCols, "shortname");

                double strike     = GetDouble(row, strikeIdx);
                string expStr     = GetString(row, expiryIdx);
                string underlying = GetString(row, underlyingIdx);
                string shortName  = GetString(row, shortNameIdx);
                string rawOptType = GetString(row, optTypeIdx);
                string optType    = rawOptType.ToUpperInvariant() == "C" ? "C" : "P";
                if (string.IsNullOrEmpty(rawOptType))
                    optType = DetectOptionType(ticker, shortName);

                if (strike <= 0)
                    for (int i = 0; i < secCols.Length; i++) { double v = GetDouble(row, i); if (v > 100) { strike = v; break; } }

                double lastPrice = 0, iv = 0;
                try
                {
                    var marketData  = doc.RootElement.GetProperty("marketdata");
                    string[] mdCols = GetColumns(marketData);
                    var mdData      = marketData.GetProperty("data");
                    foreach (var mdRow in mdData.EnumerateArray())
                    {
                        lastPrice = GetDouble(mdRow, FindColumn(mdCols, "last"));
                        iv        = GetDouble(mdRow, FindColumn(mdCols, "volatility"));
                        if (iv > 1) iv /= 100.0;
                        break;
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "marketdata для {Ticker}", ticker); }

                if (!DateTime.TryParse(expStr, out var expiration) || expiration == default)
                    expiration = ExtractExpirationFromTicker(ticker);
                if (expiration == DateTime.MaxValue)
                    expiration = DateTime.Today.AddMonths(3);

                _logger.LogInformation("Разобран {Ticker}: strike={S}, expiry={E}, type={T}, iv={IV}",
                    ticker, strike, expiration.ToShortDateString(), optType, iv);

                return new OptionData
                {
                    Ticker            = ticker,
                    Underlying        = string.IsNullOrEmpty(underlying) ? "RTS" : underlying,
                    UnderlyingTicker  = string.IsNullOrEmpty(underlying) ? "RIH5" : underlying,
                    Strike            = strike > 0 ? strike : 65000,
                    Expiration        = expiration,
                    ImpliedVolatility = iv > 0 ? iv : 0.25,
                    MarketPrice       = lastPrice,
                    OptionType        = optType,
                    VolatilitySource  = iv > 0 ? "IV (MOEX)" : "HV (по умолчанию)",
                    RiskFreeRate      = 0.145,
                    LastUpdated       = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга {Ticker}", ticker);
                return null;
            }
        }

        private static double ParseLastPrice(JsonDocument doc)
        {
            try
            {
                var md    = doc.RootElement.GetProperty("marketdata");
                string[] cols = GetColumns(md);
                var data  = md.GetProperty("data");
                int lastIdx   = FindColumn(cols, "last");
                int settleIdx = FindColumn(cols, "settleprice");
                foreach (var row in data.EnumerateArray())
                {
                    double p = GetDouble(row, lastIdx);
                    if (p > 0) return p;
                    if (settleIdx >= 0) { p = GetDouble(row, settleIdx); if (p > 0) return p; }
                }
            }
            catch { }
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Вспомогательные методы
        // ═══════════════════════════════════════════════════════════════════════

        private static string[] GetColumns(JsonElement el)
        {
            var list = new List<string>();
            foreach (var c in el.GetProperty("columns").EnumerateArray())
                list.Add(c.GetString() ?? "");
            return list.ToArray();
        }

        private static int FindColumn(string[] cols, string name)
        {
            for (int i = 0; i < cols.Length; i++)
                if (string.Equals(cols[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string GetString(JsonElement row, int idx)
        {
            if (idx < 0 || idx >= row.GetArrayLength()) return "";
            var el = row[idx];
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        }

        private static double GetDouble(JsonElement row, int idx)
        {
            if (idx < 0 || idx >= row.GetArrayLength()) return 0;
            var el = row[idx];
            if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
            if (el.ValueKind == JsonValueKind.String &&
                double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)) return v;
            return 0;
        }

        private static string DetectOptionType(string ticker, string name)
        {
            if (ticker.Contains("BC", StringComparison.OrdinalIgnoreCase) ||
                ticker.Contains("CA", StringComparison.OrdinalIgnoreCase)) return "C";
            if (ticker.Contains("BP", StringComparison.OrdinalIgnoreCase) ||
                ticker.Contains("PU", StringComparison.OrdinalIgnoreCase)) return "P";
            if (name.Contains("call", StringComparison.OrdinalIgnoreCase)) return "C";
            if (name.Contains("put",  StringComparison.OrdinalIgnoreCase)) return "P";
            return "C";
        }

        /// <summary>
        /// Пытается извлечь дату экспирации из тикера MOEX.
        /// Формат: [BASE][STRIKE][C/P][B/P][MONTH][YEAR_LAST_DIGIT]
        /// Пример: RI65000BC5 → март 2025 (B=2й квартал, C=3й месяц, 5=2025)
        ///
        /// Точная декодировка кода месяца MOEX:
        ///   Буква серии A..L = январь..декабрь (call)
        ///   Буква серии M..X = январь..декабрь (put)
        /// </summary>
        private static DateTime ExtractExpirationFromTicker(string ticker)
        {
            if (string.IsNullOrEmpty(ticker) || ticker.Length < 3)
                return DateTime.MaxValue;

            try
            {
                // Последний символ = последняя цифра года (5=2025, 6=2026 и т.д.)
                char lastChar = ticker[^1];
                if (!char.IsDigit(lastChar)) return DateTime.MaxValue;

                int yearDigit = lastChar - '0';
                int year      = 2020 + yearDigit; // 5 → 2025, 6 → 2026

                // Предпоследний символ = код месяца
                char monthCode = ticker[^2];

                // MOEX Call серия: A=1, B=2, C=3, D=4, E=5, F=6, G=7, H=8, I=9, J=10, K=11, L=12
                // MOEX Put серия:  M=1, N=2, O=3, P=4, Q=5, R=6, S=7, T=8, U=9, V=10, W=11, X=12
                int month = monthCode switch
                {
                    'A' or 'M' => 1,
                    'B' or 'N' => 2,
                    'C' or 'O' => 3,
                    'D' or 'P' => 4,
                    'E' or 'Q' => 5,
                    'F' or 'R' => 6,
                    'G' or 'S' => 7,
                    'H' or 'T' => 8,
                    'I' or 'U' => 9,
                    'J' or 'V' => 10,
                    'K' or 'W' => 11,
                    'L' or 'X' => 12,
                    _          => 0
                };

                if (month == 0) return DateTime.MaxValue;

                // Экспирация MOEX опционов: третья пятница месяца
                return GetThirdFriday(year, month);
            }
            catch
            {
                return DateTime.MaxValue;
            }
        }

        /// <summary>Возвращает третью пятницу месяца (стандартная дата экспирации MOEX).</summary>
        private static DateTime GetThirdFriday(int year, int month)
        {
            var firstDay = new DateTime(year, month, 1);
            // Сдвигаемся до первой пятницы
            int daysToFriday = ((int)DayOfWeek.Friday - (int)firstDay.DayOfWeek + 7) % 7;
            return firstDay.AddDays(daysToFriday + 14); // +14 = третья пятница
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Демо-данные
        // ═══════════════════════════════════════════════════════════════════════

        private static List<OptionSuggestion> GetDemoSuggestions(string query, DateTime now)
        {
            // Генерируем ближайшие 3 квартальные экспирации от текущей даты
            var expirations = GetUpcomingExpirations(now, 3);

            var all = new List<OptionSuggestion>();

            foreach (var exp in expirations)
            {
                // Код месяца MOEX
                string callCode = GetMoexMonthCode(exp.Month, isCall: true);
                string putCode  = GetMoexMonthCode(exp.Month, isCall: false);
                string yearCode = (exp.Year % 10).ToString();

                all.Add(new OptionSuggestion
                {
                    Ticker      = $"RI65000B{callCode}{yearCode}",
                    Description = $"RTS Call 65000 {exp:MMM yyyy}",
                    OptionType  = "C",
                    Expiration  = exp
                });
                all.Add(new OptionSuggestion
                {
                    Ticker      = $"RI65000B{putCode}{yearCode}",
                    Description = $"RTS Put 65000 {exp:MMM yyyy}",
                    OptionType  = "P",
                    Expiration  = exp
                });
                all.Add(new OptionSuggestion
                {
                    Ticker      = $"SR270B{callCode}{yearCode}",
                    Description = $"SBER Call 270 {exp:MMM yyyy}",
                    OptionType  = "C",
                    Expiration  = exp
                });
                all.Add(new OptionSuggestion
                {
                    Ticker      = $"GZ75B{callCode}{yearCode}",
                    Description = $"GAZP Call 75 {exp:MMM yyyy}",
                    OptionType  = "C",
                    Expiration  = exp
                });
            }

            if (string.IsNullOrWhiteSpace(query)) return all;

            string q = query.Trim().ToUpperInvariant();
            var filtered = all.Where(d =>
                d.Ticker.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            return filtered.Count > 0 ? filtered : all;
        }

        /// <summary>Возвращает ближайшие квартальные экспирации (3-я пятница марта/июня/сентября/декабря).</summary>
        private static List<DateTime> GetUpcomingExpirations(DateTime from, int count)
        {
            var result = new List<DateTime>();
            int[] quarterMonths = { 3, 6, 9, 12 };
            int year  = from.Year;
            int month = from.Month;

            while (result.Count < count)
            {
                foreach (int qm in quarterMonths)
                {
                    if (year > from.Year || (year == from.Year && qm >= month))
                    {
                        var expDate = GetThirdFriday(year, qm);
                        if (expDate.Date > from.Date)
                        {
                            result.Add(expDate);
                            if (result.Count >= count) break;
                        }
                    }
                }
                year++;
            }
            return result;
        }

        private static string GetMoexMonthCode(int month, bool isCall)
        {
            // Call: A-L (1-12), Put: M-X (1-12)
            char[] callCodes = { 'A','B','C','D','E','F','G','H','I','J','K','L' };
            char[] putCodes  = { 'M','N','O','P','Q','R','S','T','U','V','W','X' };
            var arr = isCall ? callCodes : putCodes;
            return arr[Math.Clamp(month - 1, 0, 11)].ToString();
        }

        private static OptionData GetDemoOptionData(string ticker)
        {
            string optType = DetectOptionType(ticker, "");
            string underlying = ticker switch
            {
                var t when t.StartsWith("RI", StringComparison.OrdinalIgnoreCase) => "RIH5",
                var t when t.StartsWith("SR", StringComparison.OrdinalIgnoreCase) => "SBER",
                var t when t.StartsWith("GZ", StringComparison.OrdinalIgnoreCase) => "GAZP",
                var t when t.StartsWith("LK", StringComparison.OrdinalIgnoreCase) => "LKOH",
                _ => "RIH5"
            };
            double strike = underlying switch
            {
                "RIH5" => 65000, "SBER" => 270, "GAZP" => 75, "LKOH" => 6500, _ => 65000
            };
            double underlyingPrice = underlying switch
            {
                "RIH5" => 64500, "SBER" => 268, "GAZP" => 73.5, "LKOH" => 6450, _ => 64500
            };

            // Дата: ближайшая квартальная экспирация
            var expDate = GetUpcomingExpirations(DateTime.Now, 1).FirstOrDefault(DateTime.Today.AddMonths(3));

            return new OptionData
            {
                Ticker            = ticker,
                Underlying        = underlying,
                UnderlyingTicker  = underlying,
                Strike            = strike,
                Expiration        = expDate,
                UnderlyingPrice   = underlyingPrice,
                ImpliedVolatility = 0.28,
                MarketPrice       = optType == "C" ? strike * 0.035 : strike * 0.028,
                OptionType        = optType,
                VolatilitySource  = "IV (демо)",
                RiskFreeRate      = 0.16,
                LastUpdated       = DateTime.Now
            };
        }
    }
}
