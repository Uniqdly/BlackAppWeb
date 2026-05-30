using System;

namespace BlackScholesApp.Models;

/// <summary>
/// Элемент выпадающего списка — краткая информация об опционе
/// </summary>
public class OptionSuggestion
{
    public string Ticker      { get; set; } = string.Empty;
    public string OptionType  { get; set; } = string.Empty; // CALL / PUT
    public string Underlying  { get; set; } = string.Empty; // Si, RTS, GAZP...
    public double Strike      { get; set; }
    public DateTime? Expiry   { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Отображается в заголовке строки списка</summary>
    public string DisplayName => Ticker;

    /// <summary>Подробная строка под тикером</summary>
    public string SubText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(OptionType))
                parts.Add(OptionType == "CALL" ? "📈 CALL" : "📉 PUT");
            if (!string.IsNullOrEmpty(Underlying))
                parts.Add($"Базовый: {Underlying}");
            if (Strike > 0)
                parts.Add($"Страйк: {Strike:N0}");
            if (Expiry.HasValue)
                parts.Add($"Экспирация: {Expiry:dd.MM.yyyy}");
            return parts.Count > 0 ? string.Join("  •  ", parts) : Description;
        }
    }
}
