using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlackScholesApp.Models;
using Newtonsoft.Json;
using ILogger = Serilog.ILogger;

namespace BlackScholesApp.Services;

public interface ICacheService
{
    Task<CachedOption?> GetAsync(string ticker);
    Task SetAsync(string ticker, OptionData data);
    Task RemoveAsync(string ticker);
    Task ClearAllAsync();
    bool IsFresh(CachedOption cached);
}

public class CacheService : ICacheService
{
    private readonly ILogger _log;
    private readonly string _cacheFilePath;
    private readonly TimeSpan _maxAge = TimeSpan.FromHours(24);
    private CacheStore _store = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CacheService(ILogger log)
    {
        _log = log;
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
        Directory.CreateDirectory(cacheDir);
        _cacheFilePath = Path.Combine(cacheDir, "options_cache.json");
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                _store = JsonConvert.DeserializeObject<CacheStore>(json) ?? new CacheStore();
                _log.Information("Cache loaded: {Count} entries", _store.Entries.Count);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load cache from disk, starting fresh");
            _store = new CacheStore();
        }
    }

    public async Task<CachedOption?> GetAsync(string ticker)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        await _lock.WaitAsync();
        try
        {
            if (_store.Entries.TryGetValue(ticker, out var cached))
            {
                _log.Information("Cache hit for {Ticker}, cached at {CachedAt}", ticker, cached.CachedAt);
                return cached;
            }
            _log.Information("Cache miss for {Ticker}", ticker);
            return null;
        }
        finally { _lock.Release(); }
    }

    public async Task SetAsync(string ticker, OptionData data)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        await _lock.WaitAsync();
        try
        {
            _store.Entries[ticker] = new CachedOption { Data = data, CachedAt = DateTime.Now };
            await SaveToDiskAsync();
            _log.Information("Cached option data for {Ticker}", ticker);
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveAsync(string ticker)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        await _lock.WaitAsync();
        try
        {
            _store.Entries.Remove(ticker);
            await SaveToDiskAsync();
            _log.Information("Removed cache entry for {Ticker}", ticker);
        }
        finally { _lock.Release(); }
    }

    public async Task ClearAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _store.Entries.Clear();
            await SaveToDiskAsync();
            _log.Information("Cache cleared");
        }
        finally { _lock.Release(); }
    }

    public bool IsFresh(CachedOption cached) => !cached.IsExpired(_maxAge);

    private async Task SaveToDiskAsync()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_store, Formatting.Indented);
            await File.WriteAllTextAsync(_cacheFilePath, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to save cache to disk");
        }
    }
}
