# Fractional Black–Scholes · ASP.NET Core 8 / Blazor Server

## Структура проекта

```
FractionalBlackScholes/
├── Models/
│   ├── FractionalBlackScholes.cs   ← вычислительный движок (перенесён из WPF)
│   ├── OptionData.cs               ← модели данных
│   └── OptionSuggestion.cs        ← кэш и подсказки
│
├── Services/
│   ├── CacheService.cs             ← in-memory кэш (24 ч)
│   ├── MoexApiService.cs           ← MOEX ISS REST API
│   └── OptionSearchService.cs      ← фасад бизнес-логики
│
├── Pages/
│   ├── _Host.cshtml                ← точка входа Blazor Server
│   └── Index.razor                 ← главная страница
│
├── Components/Shared/
│   └── MainLayout.razor            ← корневой layout
│
├── wwwroot/
│   ├── css/app.css                 ← стили (финансовый dark-theme)
│   └── favicon.svg
│
├── App.razor
├── _Imports.razor
├── Program.cs                      ← DI + middleware
├── appsettings.json
└── FractionalBlackScholes.csproj
```

---

## Запуск

### Требования
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

### Разработка
```bash
cd FractionalBlackScholes
dotnet run
# откройте https://localhost:5001
```

### Продакшн (Linux)
```bash
dotnet publish -c Release -o ./publish
cd publish
./FractionalBlackScholes
```

### IIS
1. Установите `dotnet-hosting-8.0` (ASP.NET Core Hosting Bundle).
2. `dotnet publish -c Release -o C:\inetpub\fbs`
3. Создайте Application Pool (No Managed Code) → укажите папку.
4. В `web.config` уже будет правильный `aspNetCore` handler.

---

## Перенос FractionalBlackScholesEngine из WPF

Класс **FractionalBlackScholesEngine** (`Models/FractionalBlackScholes.cs`) является
**чистой бизнес-логикой** без зависимостей от UI, WPF, или Windows.

### Шаги переноса

1. **Скопируйте** ваш существующий класс в `Models/FractionalBlackScholes.cs`.

2. **Смените namespace** на `FractionalBlackScholes.Models`.

3. **Убедитесь**, что класс не ссылается на:
   - `System.Windows.*` — убрать
   - `INotifyPropertyChanged` — убрать
   - `DispatcherTimer` — убрать
   - Любые WPF/MVVM базовые классы — убрать

4. **Публичный API** должен оставаться прежним:
   ```csharp
   double CalculateCallPrice(double S, double K, double T, double sigma, double r, double alpha);
   double CalculatePutPrice(double S, double K, double T, double sigma, double r, double alpha);
   ```

5. Файл `Services/OptionSearchService.cs` уже использует движок через DI — **никакой
   дополнительной интеграции не требуется**.

---

## Архитектура

```
Index.razor (UI)
    │  inject
    ▼
IOptionSearchService  ←────────────┐
    │                              │
    ├── IMoexApiService            │ OptionSearchService
    │       └── IHttpClientFactory │
    ├── ICacheService              │
    └── FractionalBlackScholesEngine (вычисления)
```

### Принципы SOLID

| Принцип | Реализация |
|---------|-----------|
| SRP | Каждый сервис отвечает за одно: кэш, API, расчёт |
| OCP | Новые модели ценообразования — новый класс, не правка существующих |
| LSP | Все зависимости через интерфейсы (ICacheService, IMoexApiService, …) |
| ISP | Узкие интерфейсы — отдельно поиск, отдельно кэш |
| DIP | Движок и сервисы внедряются через DI, не создаются вручную |

---

## MOEX ISS API

Приложение использует публичный API без авторизации:

| Эндпоинт | Назначение |
|----------|-----------|
| `/iss/securities.json?q={query}&type=options` | Поиск опционов |
| `/iss/engines/futures/markets/options/securities/{ticker}.json` | Данные опциона |
| `/iss/engines/futures/markets/forts/securities/{ticker}.json` | Цена фьючерса |
| `/iss/engines/stock/markets/shares/securities/{ticker}.json` | Цена акции |

Если API недоступен — автоматически возвращаются демо-данные (опцион на RTS).

---

## Дробная модель Блэка–Шоулза

Модифицированная формула с производной Римана–Лиувилля порядка α:

```
При α = 1:  классическая модель Блэка–Шоулза
При α ∈ (0,1):
  T_α  = T^α / Γ(1+α)                    — дробно-скалированное время
  σ_α  = σ · √(T^(2α-1) / Γ(2α))         — скорректированная волатильность
  C    = S·Φ(d1) - K·e^{-r·T_α}·Φ(d2)
  d1   = [ln(S/K) + (r + σ_α²/2)·T_α] / (σ_α·√T_α)
  d2   = d1 - σ_α·√T_α
```

Паритет колл–пут: `P = C - S + K·e^{-r·T_α}`
