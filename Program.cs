using FractionalBlackScholes.Models;
using FractionalBlackScholes.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Razor / Blazor ───────────────────────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();


// ─── HttpClient для MOEX API ──────────────────────────────────────────────────
builder.Services.AddHttpClient("moex", client =>
{
    client.BaseAddress = new Uri("https://iss.moex.com/");
    client.Timeout     = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "FractionalBS/1.0");
});

// ─── Сервисы бизнес-логики ────────────────────────────────────────────────────
builder.Services.AddSingleton<ICacheService,    CacheService>();
builder.Services.AddScoped<IMoexApiService,     MoexApiService>();
builder.Services.AddScoped<IOptionSearchService, OptionSearchService>();

// Вычислительный движок — не зависит от UI и DI-контейнера
// Регистрируем как Transient чтобы движок был stateless
builder.Services.AddTransient<FractionalBlackScholesEngine>();

// ─── Логирование ──────────────────────────────────────────────────────────────
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

// ─── Сборка приложения ────────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
