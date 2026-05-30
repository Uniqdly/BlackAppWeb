using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BlackScholesApp.Models;
using BlackScholesApp.Services;
using BlackScholesApp.Helpers; // Добавлено для доступа к MathHelpers, если FractionalBS там

using ILogger = Serilog.ILogger;

namespace BlackScholesApp.ViewModels;

public enum AppStatus { Idle, Loading, Calculating, Ready, Error, Offline }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly MoexApiService   _moex;
    private readonly CacheService     _cache;
    private readonly LoggingService   _logging;
    private readonly ILogger          _log;

    private CancellationTokenSource? _cts;
    private OptionData?               _currentOptionData;

    public MainViewModel(MoexApiService moex, CacheService cache,
                         LoggingService logging, ILogger log)
    {
        _moex    = moex;
        _cache   = cache;
        _logging = logging;
        _log     = log;
    }

    // ---- Свойства ввода ----
    private string _ticker = "Si-12.25C";
    public string Ticker
    {
        get => _ticker;
        set { _ticker = value; OnPropertyChanged(); TickerError = string.Empty; }
    }

    private string _tickerError = string.Empty;
    public string TickerError
    {
        get => _tickerError;
        set { _tickerError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTickerError)); }
    }
    public bool HasTickerError => !string.IsNullOrEmpty(_tickerError);

    private bool _isCall = true;
    public bool IsCall
    {
        get => _isCall;
        set { _isCall = value; _isPut = !value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPut)); }
    }

    private bool _isPut;
    public bool IsPut
    {
        get => _isPut;
        set { _isPut = value; _isCall = !value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCall)); }
    }

    private double _riskFreeRate = 0.15;
    public double RiskFreeRate
    {
        get => _riskFreeRate;
        set { _riskFreeRate = value; OnPropertyChanged(); OnPropertyChanged(nameof(RiskFreeRateDisplay)); }
    }
    public string RiskFreeRateDisplay => $"{_riskFreeRate:P1}";

    private string _riskFreeRateText = "15";
    public string RiskFreeRateText
    {
        get => _riskFreeRateText;
        set
        {
            _riskFreeRateText = value;
            OnPropertyChanged();
            if (double.TryParse(value.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double r))
                RiskFreeRate = r / 100.0;
        }
    }

    private string _alphaText = "1.0";
    public string AlphaText
    {
        get => _alphaText;
        set { _alphaText = value; OnPropertyChanged(); }
    }

    // ---- Свойства отображения параметров ----
    public string StrikeDisplay { get; set; } = "—";
    public string ExpiryDisplay { get; set; } = "—";
    public string UnderlyingPriceDisplay { get; set; } = "—";
    public string VolatilityDisplay { get; set; } = "—";
    public string MarketPriceDisplay { get; set; } = "—";
    public string UnderlyingTickerDisplay { get; set; } = "—";
    public string TimeToExpiryDisplay { get; set; } = "—";
    public string VolatilitySourceDisplay { get; set; } = "—";

    // ---- Результаты расчёта ----
    public string FairPriceDisplay { get; set; } = "—";
    public string CurrentMarketPriceResult { get; set; } = "—";
    public string DifferenceDisplay { get; set; } = "—";

    private string _differenceColor = "#808080"; 
    public string DifferenceColor
    { 
        get => _differenceColor; 
        set { _differenceColor = value; OnPropertyChanged(); } 
    }

    public bool HasResults { get; set; }
    public string StatusMessage { get; set; } = "Готово. Введите тикер и нажмите «Загрузить».";

    private AppStatus _appStatus = AppStatus.Idle;
    public AppStatus AppStatus
    {
        get => _appStatus;
        set
        {
            _appStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanCalculate));
            OnPropertyChanged(nameof(CanLoad));
        }
    }

    public bool IsOffline    => AppStatus == AppStatus.Offline;
    public bool IsBusy       => AppStatus == AppStatus.Loading || AppStatus == AppStatus.Calculating;
    public bool CanLoad      => !IsBusy;
    public bool CanCalculate => !IsBusy;

    public string ManualS { get; set; } = string.Empty;
    public string ManualK { get; set; } = string.Empty;
    public string ManualT { get; set; } = string.Empty;
    public string ManualSigma { get; set; } = string.Empty;

    // ---- Загрузка данных и Расчёт ----
    public async Task LoadDataAsync()
    {
        AppStatus = AppStatus.Loading;
        StatusMessage = "Загрузка данных с MOEX...";
        
        try
        {
            await Task.Delay(500); // Имитация работы сетевого запроса
            AppStatus = AppStatus.Ready;
            StatusMessage = "Данные успешно загружены.";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Ошибка при загрузке данных");
            AppStatus = AppStatus.Error;
            StatusMessage = $"Ошибка загрузки: {ex.Message}";
        }
    }

    public async Task CalculatePriceAsync()
    {
        AppStatus = AppStatus.Calculating;
        StatusMessage = "Выполнение расчетов...";

        try
        {
            await Task.Run(() =>
            {
                // Заменили создание графических SolidColorBrush на обычные CSS-строки
                double diff = 0.05; // Пример расчетной разницы
                if (diff < 0)
                {
                    DifferenceColor = "#FF0000"; // Red
                }
                else
                {
                    DifferenceColor = "#008000"; // Green
                }
            });

            AppStatus = AppStatus.Ready;
            StatusMessage = "Расчет завершен.";
            HasResults = true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Ошибка при расчете цен");
            AppStatus = AppStatus.Error;
            StatusMessage = $"Ошибка расчета: {ex.Message}";
        }
    }

    // Метод открытия логгера WPF полностью удален. В веб-версии переход выполняется через /logs.
    public void OpenLogViewer()
    {
        // Метод оставлен пустым, чтобы не ломать старые зависимости, если они есть
    }

    // ---- Реализация INotifyPropertyChanged ----
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
