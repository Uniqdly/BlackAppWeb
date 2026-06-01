using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FractionalBlackScholes.Models;
using Microsoft.Extensions.Logging;

namespace FractionalBlackScholes.Services
{
    public interface IOptionSearchService
    {
        Task<List<OptionSuggestion>> SearchAsync(string query, CancellationToken ct = default);
        Task<OptionData?> LoadOptionAsync(string ticker, bool forceRefresh = false, CancellationToken ct = default);
        PricingResult CalculatePrice(OptionData option, double alpha, string optionType);
        PricingResult CalculateManual(
            double S, double K, double T,
            double sigma, double r, double alpha,
            string optionType, double marketPrice = 0);
        /// <summary>Получить текущую дату из сети.</summary>
        Task<DateTime> GetNetworkDateAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Фасад, объединяющий MOEX API, кэш и численный решатель.
    ///
    /// Расчёт делегируется FractionalBlackScholesEngine, который внутри
    /// создаёт экземпляр FractionalBlackScholes (метод Грюнваля–Летникова
    /// + неявная схема Томаса) и вызывает Solve().
    /// </summary>
    public class OptionSearchService : IOptionSearchService
    {
        private readonly IMoexApiService             _moex;
        private readonly ICacheService               _cache;
        private readonly FractionalBlackScholesEngine _engine;
        private readonly ILogger<OptionSearchService> _logger;

        public OptionSearchService(
            IMoexApiService moex,
            ICacheService cache,
            FractionalBlackScholesEngine engine,
            ILogger<OptionSearchService> logger)
        {
            _moex   = moex;
            _cache  = cache;
            _engine = engine;
            _logger = logger;
        }

        public Task<List<OptionSuggestion>> SearchAsync(string query, CancellationToken ct = default)
            => _moex.GetOptionSuggestionsAsync(query, ct);

        public Task<OptionData?> LoadOptionAsync(string ticker, bool forceRefresh = false, CancellationToken ct = default)
            => _moex.LoadOptionDataAsync(ticker, forceRefresh, ct);

        public PricingResult CalculatePrice(OptionData option, double alpha, string optionType)
        {
            if (option == null) throw new ArgumentNullException(nameof(option));
            return CalculateManual(
                S:           option.UnderlyingPrice > 0 ? option.UnderlyingPrice : option.Strike,
                K:           option.Strike,
                T:           option.TimeToExpiry,
                sigma:       option.ImpliedVolatility > 0 ? option.ImpliedVolatility : 0.25,
                r:           option.RiskFreeRate,
                alpha:       alpha,
                optionType:  optionType,
                marketPrice: option.MarketPrice);
        }

        public Task<DateTime> GetNetworkDateAsync(CancellationToken ct = default)
            => _moex.GetNetworkDateAsync(ct);

        public PricingResult CalculateManual(
            double S, double K, double T,
            double sigma, double r, double alpha,
            string optionType, double marketPrice = 0)
        {
            _logger.LogInformation(
                "Расчёт (численный): S={S} K={K} T={T:F4} σ={Sigma:F4} r={R:F4} α={Alpha:F4} тип={Type}",
                S, K, T, sigma, r, alpha, optionType);

            bool isCall  = optionType.Trim().ToUpperInvariant() != "P";
            double price = isCall
                ? _engine.CalculateCallPrice(S, K, T, sigma, r, alpha)
                : _engine.CalculatePutPrice (S, K, T, sigma, r, alpha);

            return new PricingResult
            {
                FairPrice    = Math.Round(price, 4),
                MarketPrice  = marketPrice,
                Alpha        = alpha,
                OptionType   = isCall ? "C" : "P",
                CalculatedAt = DateTime.Now
            };
        }
    }
}
