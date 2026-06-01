using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FractionalBlackScholes.Models;
using Microsoft.Extensions.Logging;

namespace FractionalBlackScholes.Services
{
    /// <summary>
    /// Интерфейс скринера опционов.
    /// </summary>
    public interface IScreenerService
    {
        /// <summary>
        /// Запускает скрининг по заданным тикерам-запросам.
        /// Возвращает два отсортированных списка:
        ///   undervalued  — опционы, которые рынок недооценивает (FairPrice > MarketPrice)
        ///   overvalued   — опционы, которые рынок переоценивает (FairPrice &lt; MarketPrice)
        /// </summary>
        Task<ScreenerReport> RunAsync(
            ScreenerSettings settings,
            IProgress<string>? progress = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Настройки скрининга.
    /// </summary>
    public class ScreenerSettings
    {
        /// <summary>Поисковые запросы (тикеры или префиксы: "RI", "SR", "GZ").</summary>
        public List<string> Queries { get; set; } = new() { "RI", "SR", "GZ", "LK" };

        /// <summary>Порядок дробной производной для расчёта.</summary>
        public double Alpha { get; set; } = 0.85;

        /// <summary>Минимальный порог отклонения (%), ниже которого опцион не включается в топ.</summary>
        public double MinDiffPercent { get; set; } = 5.0;

        /// <summary>Максимум строк в каждом из двух блоков.</summary>
        public int TopN { get; set; } = 10;
    }

    /// <summary>
    /// Итог скрининга: два списка + метаданные.
    /// </summary>
    public class ScreenerReport
    {
        public List<ScreenerResult> Undervalued { get; set; } = new();
        public List<ScreenerResult> Overvalued  { get; set; } = new();
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public double Alpha { get; set; }
        public int TotalScanned { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Реализация скринера.
    ///
    /// Алгоритм:
    ///   1. По каждому запросу из settings.Queries получает список активных опционов.
    ///   2. Для каждого уникального тикера загружает OptionData.
    ///   3. Считает справедливую цену через FractionalBlackScholesEngine.
    ///   4. Сортирует по |DiffPercent| и разбивает на два топа.
    /// </summary>
    public class ScreenerService : IScreenerService
    {
        private readonly IMoexApiService              _moex;
        private readonly IOptionSearchService         _pricer;
        private readonly ILogger<ScreenerService>     _logger;

        public ScreenerService(
            IMoexApiService moex,
            IOptionSearchService pricer,
            ILogger<ScreenerService> logger)
        {
            _moex   = moex;
            _pricer = pricer;
            _logger = logger;
        }

        public async Task<ScreenerReport> RunAsync(
            ScreenerSettings settings,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = new ScreenerReport { Alpha = settings.Alpha };
            var results = new List<ScreenerResult>();

            // ── Шаг 1: собираем уникальные тикеры ──────────────────────────────
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tickers = new List<string>();

            foreach (var query in settings.Queries)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report($"Поиск опционов по «{query}»…");

                try
                {
                    var suggestions = await _moex.GetOptionSuggestionsAsync(query, ct);
                    foreach (var s in suggestions)
                    {
                        if (seen.Add(s.Ticker))
                            tickers.Add(s.Ticker);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ошибка поиска по запросу {Query}", query);
                }
            }

            report.TotalScanned = tickers.Count;
            _logger.LogInformation("Скринер: найдено {Count} уникальных тикеров", tickers.Count);

            // ── Шаг 2: загружаем данные и считаем ──────────────────────────────
            int processed = 0;
            foreach (var ticker in tickers)
            {
                if (ct.IsCancellationRequested) break;

                processed++;
                progress?.Report($"Расчёт {processed}/{tickers.Count}: {ticker}");

                try
                {
                    var option = await _moex.LoadOptionDataAsync(ticker, false, ct);
                    if (option == null) continue;

                    // Пропускаем опционы без рыночной цены — нечего сравнивать
                    if (option.MarketPrice <= 0) continue;

                    // Пропускаем опционы без цены базового актива
                    double S = option.UnderlyingPrice > 0 ? option.UnderlyingPrice : 0;
                    if (S <= 0) continue;

                    // Считаем в Task.Run, чтобы не блокировать SignalR-поток
                    var pricing = await Task.Run(() =>
                        _pricer.CalculatePrice(option, settings.Alpha, option.OptionType), ct);

                    double diffPct = Math.Abs(pricing.DifferencePercent);
                    if (diffPct < settings.MinDiffPercent) continue;

                    results.Add(new ScreenerResult
                    {
                        Option       = option,
                        FairPrice    = pricing.FairPrice,
                        Alpha        = settings.Alpha,
                        CalculatedAt = DateTime.Now
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ошибка расчёта для {Ticker}", ticker);
                }
            }

            // ── Шаг 3: разбиваем и сортируем ───────────────────────────────────
            report.Undervalued = results
                .Where(r => r.IsUndervalued)
                .OrderByDescending(r => r.DiffPercent)
                .Take(settings.TopN)
                .ToList();

            report.Overvalued = results
                .Where(r => !r.IsUndervalued)
                .OrderBy(r => r.DiffPercent)           // самое большое отрицательное = сильнее всего переоценён
                .Take(settings.TopN)
                .ToList();

            sw.Stop();
            report.Elapsed      = sw.Elapsed;
            report.GeneratedAt  = DateTime.Now;

            _logger.LogInformation(
                "Скрининг завершён: {Under} недооценённых, {Over} переоценённых, время {Ms} мс",
                report.Undervalued.Count, report.Overvalued.Count, sw.ElapsedMilliseconds);

            return report;
        }
    }
}
