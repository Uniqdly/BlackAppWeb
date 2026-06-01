using System;

namespace FractionalBlackScholes.Models
{
    /// <summary>
    /// Результат скрининга одного опциона:
    /// содержит исходные данные + расчёт по дробной модели + классификацию.
    /// </summary>
    public class ScreenerResult
    {
        /// <summary>Исходные данные опциона.</summary>
        public OptionData Option { get; set; } = new();

        /// <summary>Справедливая цена по дробной модели.</summary>
        public double FairPrice { get; set; }

        /// <summary>Рыночная цена.</summary>
        public double MarketPrice => Option.MarketPrice;

        /// <summary>Абсолютное отклонение (модель − рынок).</summary>
        public double Difference => FairPrice - MarketPrice;

        /// <summary>Относительное отклонение в процентах.</summary>
        public double DiffPercent =>
            MarketPrice > 0 ? (Difference / MarketPrice) * 100.0 : 0.0;

        /// <summary>true — рынок переоценивает (модель &lt; рынка), недооценён с т.з. покупателя.</summary>
        public bool IsUndervalued => Difference > 0;  // модель > рынка → рынок дешевле модели

        /// <summary>Порядок дробной производной, использованный при расчёте.</summary>
        public double Alpha { get; set; }

        /// <summary>Время расчёта.</summary>
        public DateTime CalculatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Краткое обоснование на основе числовых характеристик.
        /// Генерируется автоматически по соотношению параметров опциона.
        /// </summary>
        public string Rationale
        {
            get
            {
                double t   = Option.TimeToExpiry;
                double iv  = Option.ImpliedVolatility;
                double pct = Math.Abs(DiffPercent);

                if (IsUndervalued)
                {
                    if (iv < 0.20)  return $"IV={iv*100:F0}% ниже исторической нормы — рынок недооценивает волатильность";
                    if (t < 0.05)   return $"До экспирации {t*365:F0} дн. — временна́я стоимость занижена";
                    if (pct > 30)   return $"Сильное расхождение {pct:F0}% — вероятен арбитраж";
                    return $"Дробная модель (α={Alpha:F2}) даёт справедливую цену выше рыночной на {pct:F1}%";
                }
                else
                {
                    if (iv > 0.45)  return $"IV={iv*100:F0}% аномально высока — рынок переоценивает риск";
                    if (t > 0.5)    return $"Длинный горизонт {t*365:F0} дн. — временна́я премия завышена";
                    if (pct > 30)   return $"Рынок завышает цену на {pct:F0}% — возможна продажа опциона";
                    return $"Дробная модель (α={Alpha:F2}) даёт справедливую цену ниже рыночной на {pct:F1}%";
                }
            }
        }
    }
}
