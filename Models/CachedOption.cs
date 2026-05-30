using System;
using System.Collections.Generic;

namespace BlackScholesApp.Models;

public class CachedOption
{
    public OptionData Data { get; set; } = new();
    public DateTime CachedAt { get; set; } = DateTime.Now;
    public bool IsExpired(TimeSpan maxAge) => DateTime.Now - CachedAt > maxAge;
}

public class CacheStore
{
    public Dictionary<string, CachedOption> Entries { get; set; } = new();
}
