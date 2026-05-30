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

public interface IMoexApiService
{
    Task<OptionData?> LoadOptionDataAsync(string ticker, CancellationToken ct = default);
}

/// <summary>
/// Сервис для получения данных об опционах с Московской биржи через ISS MOEX REST API
/// </summary>
public class MoexApiService : IMoexApiService
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private const string BaseUrl = "https://iss.moex.com/iss";

    public MoexApiService(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    public async Task<OptionData?> LoadOptionDataAsync(string ticker, CancellationToken ct = default)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        _log.Information("Loading option data for ticker: {Ticker}", ticker);

        var result = new OptionData { Ticker = ticker };

        bool loaded = await TryLoadFromOptionsMarketAsync(result, ticker, ct);
        if (!loaded)
            loaded = await TryLoadFromSecuritiesSearchAsync(result, ticker, ct);

        if (!loaded)
        {
            _log.Warning("Could not find option {Ticker} in MOEX API", ticker);
            return null;
        }

        if (string.IsNullOrEmpty(result.OptionType))
            result.OptionType = InferOptionType(ticker);

        if (!string.IsNullOrEmpty(result.UnderlyingTicker))
            await LoadUnderlyingPriceAsync(result, ct);

        if (result.ImpliedVolatility <= 0 && result.UnderlyingPrice > 0)
        {
            _log.Information("IV not available, calculating historical volatility for {Asset}", result.UnderlyingTicker);
            result.HistoricalVolatility = await CalculateHistoricalVolatilityAsync(result.UnderlyingTicker, ct);
            result.IsVolatilityImplied = false;
        }
        else
        {
            result.IsVolatilityImplied = result.ImpliedVolatility > 0;
        }

        _log.Information("Option data loaded: Strike={Strike}, Expiry={Expiry}, S={S}, Vol={Vol:P2}, Type={Type}",
            result.Strike, result.MaturityDate, result.UnderlyingPrice,
            result.EffectiveVolatility, result.OptionType);

        return result;
    }

    private async Task<bool> TryLoadFromOptionsMarketAsync(OptionData result, string ticker, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/engines/futures/markets/options/securities/{Uri.EscapeDataString(ticker)}.json" +
                      "?iss.meta=off&iss.only=securities,marketdata";
            _log.Debug("GET {Url}", url);
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            return ParseOptionsSecurities(doc, result);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error loading from futures/options market for {Ticker}", ticker);
            return false;
        }
    }

    private bool ParseOptionsSecurities(JsonDocument doc, OptionData result)
    {
        try
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("securities", out var securitiesBlock))
            {
                var columns = GetColumns(securitiesBlock);
                var data = GetDataRows(securitiesBlock);
                if (data.Count > 0)
                {
                    var row = data[0];
                    result.Strike = GetDouble(row, columns, "STRIKE", "STRIKEPRICE");
                    result.UnderlyingTicker = GetString(row, columns, "ASSETCODE", "UNDERLYING") ?? string.Empty;

                    var matStr = GetString(row, columns, "LASTTRADEDATE", "MATDATE", "EXPIRATION");
                    if (DateTime.TryParse(matStr, out var mat))
                        result.MaturityDate = mat;

                    var optType = GetString(row, columns, "OPTIONTYPE", "OPTIONSTYLE");
                    if (!string.IsNullOrEmpty(optType))
                        result.OptionType = optType.ToUpperInvariant().StartsWith("C") ? "CALL" : "PUT";

                    var iv = GetDouble(row, columns, "IMPLICEDVOLATILITY", "IMPLIEDVOLATILITY", "IV");
                    if (iv > 0) result.ImpliedVolatility = iv / 100.0;
                }
            }

            if (root.TryGetProperty("marketdata", out var marketBlock))
            {
                var columns = GetColumns(marketBlock);
                var data = GetDataRows(marketBlock);
                if (data.Count > 0)
                {
                    var row = data[0];
                    double last = GetDouble(row, columns, "LAST");
                    double bid  = GetDouble(row, columns, "BID");
                    double ask  = GetDouble(row, columns, "OFFER", "ASK");
                    result.MarketPrice = last > 0 ? last : (bid + ask) / 2.0;
                }
            }

            return result.Strike > 0 || result.MaturityDate.HasValue;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error parsing options securities response");
            return false;
        }
    }

    private async Task<bool> TryLoadFromSecuritiesSearchAsync(OptionData result, string ticker, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/securities/{Uri.EscapeDataString(ticker)}.json?iss.meta=off";
            _log.Debug("GET {Url}", url);
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("description", out var desc))
                return false;

            var columns = GetColumns(desc);
            var data = GetDataRows(desc);

            foreach (var row in data)
            {
                var name  = GetString(row, columns, "name");
                var value = GetString(row, columns, "value");

                switch (name?.ToUpperInvariant())
                {
                    case "STRIKE":
                        result.Strike = ParseDouble(value); break;
                    case "LASTTRADEDATE":
                    case "MATDATE":
                        if (DateTime.TryParse(value, out var dt)) result.MaturityDate = dt;
                        break;
                    case "ASSETCODE":
                    case "UNDERLYING":
                        result.UnderlyingTicker = value ?? string.Empty;
                        break;
                    case "OPTIONTYPE":
                        result.OptionType = (value ?? "").ToUpperInvariant().StartsWith("C") ? "CALL" : "PUT";
                        break;
                }
            }

            return result.Strike > 0 || !string.IsNullOrEmpty(result.UnderlyingTicker);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error loading from securities search for {Ticker}", ticker);
            return false;
        }
    }

    private async Task LoadUnderlyingPriceAsync(OptionData result, CancellationToken ct)
    {
        var asset = result.UnderlyingTicker;
        if (string.IsNullOrEmpty(asset)) return;

        var attempts = new[]
        {
            $"{BaseUrl}/engines/stock/markets/shares/securities/{Uri.EscapeDataString(asset)}.json?iss.meta=off&iss.only=marketdata",
            $"{BaseUrl}/engines/futures/markets/forts/securities/{Uri.EscapeDataString(asset)}.json?iss.meta=off&iss.only=marketdata",
            $"{BaseUrl}/engines/currency/markets/selt/securities/{Uri.EscapeDataString(asset)}.json?iss.meta=off&iss.only=marketdata",
        };

        foreach (var url in attempts)
        {
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var price = ExtractPrice(doc);
                if (price > 0)
                {
                    result.UnderlyingPrice = price;
                    _log.Information("Underlying {Asset} price: {Price}", asset, price);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Underlying price attempt failed: {Url}", url);
            }
        }

        _log.Warning("Could not load underlying price for {Asset}", asset);
    }

    private double ExtractPrice(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("marketdata", out var md)) return 0;
        var columns = GetColumns(md);
        var data = GetDataRows(md);
        if (data.Count == 0) return 0;

        foreach (var row in data)
        {
            double last = GetDouble(row, columns, "LAST");
            if (last > 0) return last;
            double bid = GetDouble(row, columns, "BID");
            double ask = GetDouble(row, columns, "OFFER", "ASK");
            if (bid > 0 && ask > 0) return (bid + ask) / 2;
        }
        return 0;
    }

    private async Task<double> CalculateHistoricalVolatilityAsync(string asset, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(asset)) return 0.30;

        var from = DateTime.Today.AddDays(-90).ToString("yyyy-MM-dd");
        var till = DateTime.Today.ToString("yyyy-MM-dd");

        var endpoints = new[]
        {
            $"{BaseUrl}/engines/stock/markets/shares/securities/{Uri.EscapeDataString(asset)}/candles.json?from={from}&till={till}&interval=24&iss.meta=off",
            $"{BaseUrl}/engines/futures/markets/forts/securities/{Uri.EscapeDataString(asset)}/candles.json?from={from}&till={till}&interval=24&iss.meta=off",
            $"{BaseUrl}/engines/currency/markets/selt/securities/{Uri.EscapeDataString(asset)}/candles.json?from={from}&till={till}&interval=24&iss.meta=off",
        };

        foreach (var url in endpoints)
        {
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var vol = ComputeVolatilityFromCandles(doc);
                if (vol > 0)
                {
                    _log.Information("Historical volatility for {Asset}: {Vol:P2}", asset, vol);
                    return vol;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Candles attempt failed");
            }
        }

        _log.Warning("Could not compute HV for {Asset}, using fallback 30%", asset);
        return 0.30;
    }

    private double ComputeVolatilityFromCandles(JsonDocument doc)
    {
        try
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("candles", out var candles)) return 0;

            var columns = GetColumns(candles);
            var data = GetDataRows(candles);
            if (data.Count < 5) return 0;

            int closeIdx = columns.IndexOf("close");
            if (closeIdx < 0) return 0;

            var closes = new List<double>();
            foreach (var row in data)
            {
                if (row.ValueKind == JsonValueKind.Array)
                {
                    var arr = row.EnumerateArray().ToList();
                    if (closeIdx < arr.Count && arr[closeIdx].ValueKind == JsonValueKind.Number)
                    {
                        double c = arr[closeIdx].GetDouble();
                        if (c > 0) closes.Add(c);
                    }
                }
            }

            if (closes.Count < 5) return 0;

            var logReturns = new List<double>();
            for (int i = 1; i < closes.Count; i++)
                logReturns.Add(Math.Log(closes[i] / closes[i - 1]));

            double mean = logReturns.Average();
            double variance = logReturns.Sum(r => (r - mean) * (r - mean)) / (logReturns.Count - 1);
            return Math.Sqrt(variance) * Math.Sqrt(252);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error computing volatility from candles");
            return 0;
        }
    }

    // Вспомогательные методы парсинга ISS JSON
    private static List<string> GetColumns(JsonElement block)
    {
        var result = new List<string>();
        if (block.TryGetProperty("columns", out var cols))
            foreach (var c in cols.EnumerateArray())
                result.Add(c.GetString() ?? string.Empty);
        return result;
    }

    private static List<JsonElement> GetDataRows(JsonElement block)
    {
        var result = new List<JsonElement>();
        if (block.TryGetProperty("data", out var data))
            foreach (var row in data.EnumerateArray())
                result.Add(row);
        return result;
    }

    private static string? GetString(JsonElement row, List<string> columns, params string[] fieldNames)
    {
        foreach (var field in fieldNames)
        {
            int idx = columns.FindIndex(c => string.Equals(c, field, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && row.ValueKind == JsonValueKind.Array)
            {
                var arr = row.EnumerateArray().ToList();
                if (idx < arr.Count && arr[idx].ValueKind != JsonValueKind.Null)
                    return arr[idx].GetString();
            }
        }
        return null;
    }

    private static double GetDouble(JsonElement row, List<string> columns, params string[] fieldNames)
    {
        foreach (var field in fieldNames)
        {
            int idx = columns.FindIndex(c => string.Equals(c, field, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && row.ValueKind == JsonValueKind.Array)
            {
                var arr = row.EnumerateArray().ToList();
                if (idx < arr.Count)
                {
                    if (arr[idx].ValueKind == JsonValueKind.Number) return arr[idx].GetDouble();
                    if (arr[idx].ValueKind == JsonValueKind.String &&
                        double.TryParse(arr[idx].GetString(),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double v))
                        return v;
                }
            }
        }
        return 0;
    }

    private static double ParseDouble(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double v);
        return v;
    }

    private static string InferOptionType(string ticker)
    {
        if (string.IsNullOrEmpty(ticker)) return "CALL";
        char last = ticker[^1];
        return last == 'P' || last == 'p' ? "PUT" : "CALL";
    }
}
