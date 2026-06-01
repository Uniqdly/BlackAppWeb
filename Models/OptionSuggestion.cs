using System;

namespace FractionalBlackScholes.Models
{
    /// <summary>
    /// Подсказка для автодополнения при поиске тикера опциона.
    /// Содержит дату экспирации для фильтрации истёкших инструментов.
    /// </summary>
    public class OptionSuggestion
    {
        /// <summary>Тикер опциона.</summary>
        public string Ticker { get; set; } = string.Empty;

        /// <summary>Читаемое описание (страйк, экспирация, тип).</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Тип: "C" или "P".</summary>
        public string OptionType { get; set; } = "C";

        /// <summary>
        /// Дата экспирации опциона. Используется для фильтрации:
        /// показываем только те опционы, у которых Expiration > текущая дата.
        /// DateTime.MaxValue — если дата неизвестна (пропускаем фильтр).
        /// </summary>
        public DateTime Expiration { get; set; } = DateTime.MaxValue;

        /// <summary>
        /// true — срок опциона ещё не истёк относительно переданной даты.
        /// </summary>
        public bool IsActive(DateTime now) =>
            Expiration == DateTime.MaxValue || Expiration.Date > now.Date;

        /// <summary>Строка для отображения в выпадающем списке.</summary>
        public string DisplayText => $"{Ticker} — {Description}";
    }

    /// <summary>
    /// Элемент локального кэша данных об опционе.
    /// </summary>
    public class CachedOption
    {
        public string Key { get; set; } = string.Empty;
        public OptionData Data { get; set; } = new();
        public DateTime CachedAt { get; set; } = DateTime.Now;
        public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);
        public bool IsValid => DateTime.Now - CachedAt < Ttl;
    }
}
