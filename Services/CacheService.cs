using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FractionalBlackScholes.Models;
using Microsoft.Extensions.Logging;

namespace FractionalBlackScholes.Services
{
    /// <summary>
    /// Интерфейс локального кэша данных об опционах.
    /// </summary>
    public interface ICacheService
    {
        /// <summary>Получить данные из кэша по тикеру.</summary>
        OptionData? Get(string ticker);

        /// <summary>Сохранить данные в кэш.</summary>
        void Set(string ticker, OptionData data, TimeSpan? ttl = null);

        /// <summary>Удалить запись из кэша.</summary>
        void Remove(string ticker);

        /// <summary>Очистить весь кэш.</summary>
        void Clear();

        /// <summary>Количество записей в кэше.</summary>
        int Count { get; }

        /// <summary>Список всех ключей кэша.</summary>
        IReadOnlyList<string> Keys { get; }
    }

    /// <summary>
    /// Потокобезопасный in-memory кэш на основе ConcurrentDictionary.
    /// Срок жизни записи — 24 часа по умолчанию.
    /// </summary>
    public class CacheService : ICacheService
    {
        private readonly ConcurrentDictionary<string, CachedOption> _cache = new();
        private readonly ILogger<CacheService> _logger;

        public CacheService(ILogger<CacheService> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public OptionData? Get(string ticker)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return null;

            string key = ticker.ToUpperInvariant();

            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.IsValid)
                {
                    _logger.LogDebug("Cache HIT for {Ticker}", key);
                    return cached.Data;
                }

                // Запись устарела — удаляем
                _logger.LogDebug("Cache EXPIRED for {Ticker}", key);
                _cache.TryRemove(key, out _);
            }

            return null;
        }

        /// <inheritdoc/>
        public void Set(string ticker, OptionData data, TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return;

            string key = ticker.ToUpperInvariant();
            var entry = new CachedOption
            {
                Key     = key,
                Data    = data,
                CachedAt = DateTime.Now,
                Ttl     = ttl ?? TimeSpan.FromHours(24)
            };

            _cache[key] = entry;
            _logger.LogDebug("Cache SET for {Ticker}, TTL={Ttl}", key, entry.Ttl);
        }

        /// <inheritdoc/>
        public void Remove(string ticker)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return;
            _cache.TryRemove(ticker.ToUpperInvariant(), out _);
        }

        /// <inheritdoc/>
        public void Clear()
        {
            _cache.Clear();
            _logger.LogInformation("Cache cleared");
        }

        /// <inheritdoc/>
        public int Count => _cache.Count(kv => kv.Value.IsValid);

        /// <inheritdoc/>
        public IReadOnlyList<string> Keys =>
            _cache.Where(kv => kv.Value.IsValid)
                  .Select(kv => kv.Key)
                  .ToList()
                  .AsReadOnly();
    }
}
