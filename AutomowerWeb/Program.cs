using AutomowerConsole.Core;
using AutomowerWeb;
using AutomowerWeb.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Same services the CLI uses, same singleton-per-run pattern (see
// Program.cs in AutomowerConsole) - cheap, effectively-stateless wrappers
// over AutomowerConnect.Instance/Storage, safe to share across requests.
builder.Services.AddSingleton<MowerService>();
builder.Services.AddSingleton<MowerDetailService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<TrackingService>();

// Registered as plain AddSingleton (not AddHttpClient<T>, which would make
// DI hand out a fresh instance - and fresh, empty cache - on every
// request) - both services hold their own long-lived in-memory cache and
// resolve HttpClient instances on demand via IHttpClientFactory instead.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LocationService>();
builder.Services.AddSingleton<WeatherService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
