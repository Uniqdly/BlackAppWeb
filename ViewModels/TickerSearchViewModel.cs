using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BlackScholesApp.Models;
using BlackScholesApp.Services;
using ILogger = Serilog.ILogger;

namespace BlackScholesApp.ViewModels;

/// <summary>
/// ViewModel для автодополнения тикера опциона.
/// Вынесен отдельно чтобы не перегружать MainViewModel.
/// MainWindow создаёт экземпляр и передаёт результат выбора в MainViewModel.
/// </summary>
public class TickerSearchViewModel : INotifyPropertyChanged
{
    private readonly IOptionSearchService _search;
    private readonly ILogger _log;

    private CancellationTokenSource? _searchCts;

    public TickerSearchViewModel(IOptionSearchService search, ILogger log)
    {
        _search = search;
        _log    = log;
    }

    // ---- Suggestions list ----
    private ObservableCollection<OptionSuggestion> _suggestions = new();
    public ObservableCollection<OptionSuggestion> Suggestions
    {
        get => _suggestions;
        set { _suggestions = value; OnPropertyChanged(); }
    }

    private bool _isDropdownOpen;
    public bool IsDropdownOpen
    {
        get => _isDropdownOpen;
        set { _isDropdownOpen = value; OnPropertyChanged(); }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set { _isSearching = value; OnPropertyChanged(); }
    }

    private string _searchHint = "Загрузка списка опционов...";
    public string SearchHint
    {
        get => _searchHint;
        set { _searchHint = value; OnPropertyChanged(); }
    }

    // ---- Public API ----

    /// <summary>Вызывается при изменении текста в поле тикера</summary>
    public async Task OnQueryChangedAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // Дебаунс 250 мс
        try { await Task.Delay(250, ct); }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        await DoSearchAsync(query, ct);
    }

    /// <summary>Загружает начальный список при открытии дропдауна</summary>
    public async Task LoadInitialAsync()
    {
        if (Suggestions.Count > 0) return;
        await DoSearchAsync(string.Empty, CancellationToken.None);
    }

    private async Task DoSearchAsync(string query, CancellationToken ct)
    {
        IsSearching = true;
        SearchHint  = "Поиск...";

        try
        {
            List<OptionSuggestion> items;
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                items = await _search.GetPopularAsync(ct);
                SearchHint = items.Count > 0
                    ? $"Популярные опционы ({items.Count})"
                    : "Список недоступен — введите тикер вручную";
            }
            else
            {
                items = await _search.SearchAsync(query, ct);
                SearchHint = items.Count > 0
                    ? $"Найдено: {items.Count}"
                    : "Ничего не найдено — попробуйте другой запрос";
            }

            if (ct.IsCancellationRequested) return;

            Suggestions.Clear();
            foreach (var item in items)
                Suggestions.Add(item);

            IsDropdownOpen = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warning(ex, "Search error for query '{Query}'", query);
            SearchHint = "Ошибка загрузки — введите тикер вручную";
        }
        finally
        {
            IsSearching = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
