using System;

namespace FractionalBlackScholes.Models
{
    /// <summary>
    /// Модель данных опциона, загруженного с MOEX или введённого вручную.
    /// </summary>
    public class OptionData
    {
        /// <summary>Тикер опциона (например, RI65000BC5).</summary>
        public string Ticker { get; set; } = string.Empty;

        /// <summary>Базовый актив (например, RTS, SBER).</summary>
        public string Underlying { get; set; } = string.Empty;

        /// <summary>Тикер базового актива.</summary>
        public string UnderlyingTicker { get; set; } = string.Empty;

        /// <summary>Страйк (цена исполнения).</summary>
        public double Strike { get; set; }

        /// <summary>Дата экспирации.</summary>
        public DateTime Expiration { get; set; }

        /// <summary>Время до экспирации в годах.</summary>
        public double TimeToExpiry => Math.Max((Expiration - DateTime.Today).TotalDays / 365.0, 1.0 / 365.0);

        /// <summary>Цена базового актива.</summary>
        public double UnderlyingPrice { get; set; }

        /// <summary>Подразумеваемая волатильность (IV) в долях единицы.</summary>
        public double ImpliedVolatility { get; set; }

        /// <summary>Рыночная цена опциона (последняя сделка или середина спреда).</summary>
        public double MarketPrice { get; set; }

        /// <summary>Тип опциона: "C" (call) или "P" (put).</summary>
        public string OptionType { get; set; } = "C";

        /// <summary>Источник волатильности: "IV" — подразумеваемая, "HV" — историческая.</summary>
        public string VolatilitySource { get; set; } = "IV";

        /// <summary>Безрисковая ставка (ключевая ставка ЦБ РФ, по умолчанию 16%).</summary>
        public double RiskFreeRate { get; set; } = 0.16;

        /// <summary>Время последнего обновления данных.</summary>
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Результат расчёта справедливой стоимости.
    /// </summary>
    public class PricingResult
    {
        /// <summary>Справедливая цена по дробной модели.</summary>
        public double FairPrice { get; set; }

        /// <summary>Рыночная цена.</summary>
        public double MarketPrice { get; set; }

        /// <summary>Абсолютное отклонение (модель − рынок).</summary>
        public double Difference => FairPrice - MarketPrice;

        /// <summary>Относительное отклонение в процентах.</summary>
        public double DifferencePercent =>
            MarketPrice > 0 ? (Difference / MarketPrice) * 100.0 : 0.0;

        /// <summary>Использованный порядок дробной производной α.</summary>
        public double Alpha { get; set; }

        /// <summary>Тип опциона.</summary>
        public string OptionType { get; set; } = "C";

        /// <summary>Время расчёта.</summary>
        public DateTime CalculatedAt { get; set; } = DateTime.Now;

        /// <summary>true — модель переоценивает опцион, false — недооценивает.</summary>
        public bool IsOverpriced => Difference > 0;
    }
}
