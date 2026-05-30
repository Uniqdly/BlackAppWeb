using MyBlazorApp.Components;
using BlackScholesApp.Services;
using BlackScholesApp.ViewModels;
using Serilog;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// 1. Инициализация и регистрация Serilog для DI-контейнера
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File("Logs/BlackScholesApp_.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Services.AddSingleton<Serilog.ILogger>(Log.Logger);

// 2. Регистрация HttpClient для работы MoexApiService и OptionSearchService
builder.Services.AddHttpClient();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 3. Регистрация внутренних сервисов
// Собираем LoggingService вручную, так как ему нужен путь к логам в конструктор
builder.Services.AddSingleton<LoggingService>(provider => 
{
    string logsPath = Path.Combine(AppContext.BaseDirectory, "Logs");
    return new LoggingService(logsPath);
});

builder.Services.AddSingleton<CacheService>();
builder.Services.AddScoped<MoexApiService>();
builder.Services.AddScoped<OptionSearchService>();

// Регистрируем интерфейс поиска для TickerSearchViewModel, если он используется
builder.Services.AddScoped<IOptionSearchService, OptionSearchService>();

// 4. Регистрация ViewModels 
builder.Services.AddScoped<MainViewModel>();
builder.Services.AddScoped<TickerSearchViewModel>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
