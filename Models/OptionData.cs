using System;

namespace BlackScholesApp.Models;

public class OptionData
{
    public string Ticker { get; set; } = string.Empty;
    public string OptionType { get; set; } = "CALL";
    public double Strike { get; set; }
    public DateTime? MaturityDate { get; set; }
    public double UnderlyingPrice { get; set; }
    public string UnderlyingTicker { get; set; } = string.Empty;
    public double MarketPrice { get; set; }
    public double ImpliedVolatility { get; set; }
    public double HistoricalVolatility { get; set; }
    public bool IsVolatilityImplied { get; set; }
    public DateTime LoadedAt { get; set; } = DateTime.Now;

    public double EffectiveVolatility =>
        IsVolatilityImplied && ImpliedVolatility > 0 ? ImpliedVolatility : HistoricalVolatility;

    public double TimeToExpiryYears =>
        MaturityDate.HasValue
            ? Math.Max(0, (MaturityDate.Value - DateTime.Today).TotalDays / 365.0)
            : 0;

    public bool IsValid =>
        Strike > 0 && MaturityDate.HasValue && UnderlyingPrice > 0 &&
        EffectiveVolatility > 0 && TimeToExpiryYears > 0;
}
