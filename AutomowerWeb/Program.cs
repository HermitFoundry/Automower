using AutomowerConsole.Core;
using AutomowerWeb;
using AutomowerWeb.Components;
using Microsoft.AspNetCore.HttpOverrides;

// ContentRootPath anchored to the app's own location (AppContext.
// BaseDirectory), not left to default to the launching shell's current
// directory - UseStaticFiles() resolves "wwwroot" relative to the content
// root, and defaulting to CWD meant it 404'd on every asset unless you
// happened to launch from inside the publish folder itself. Confirmed:
// identical binary, only the launch directory differed, 404 vs working.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Default antiforgery cookie policy doesn't follow Request.IsHttps the way
// a plain CookieBuilder does - confirmed by testing (the cookie came back
// without the Secure attribute even once UseForwardedHeaders correctly
// reported IsHttps=true) - has to be set explicitly. SameAsRequest, not
// Always, so startweb.dev's plain-http local/LAN path still works.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Storage seam (see the 2026-07-29/30 SQLite-migration + hybrid-tracking
// work) - SQLite-backed as of the 2026-07-30 cutover.
builder.Services.AddSingleton<IMowerRepositoryFactory, SqliteMowerRepositoryFactory>();
builder.Services.AddSingleton<IMowerRegistry, SqliteMowerRegistry>();

// Same services the CLI uses, same singleton-per-run pattern (see
// Program.cs in AutomowerConsole) - cheap, effectively-stateless wrappers
// over AutomowerConnect.Instance/Storage, safe to share across requests.
builder.Services.AddSingleton<MowerService>();
builder.Services.AddSingleton<MowerDetailService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<TrackingService>();
builder.Services.AddSingleton<CoverageService>();

// Registered as plain AddSingleton (not AddHttpClient<T>, which would make
// DI hand out a fresh instance - and fresh, empty cache - on every
// request) - both services hold their own long-lived in-memory cache and
// resolve HttpClient instances on demand via IHttpClientFactory instead.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LocationService>();
builder.Services.AddSingleton<WeatherService>();

var app = builder.Build();

// Caddy terminates TLS and reverse-proxies here over plain HTTP on the
// QNAP's own LAN (see Caddyfile / AUTOMOWER_UPSTREAM) - without this,
// Kestrel has no way to know the original request was HTTPS, so every
// scheme-dependent decision downstream (the antiforgery cookie's Secure
// flag, UseHttpsRedirection, any absolute URL the app itself generates)
// silently assumes http. That's what caused the browser to flag the site
// as "not secure" (confirmed via its own "connection isn't secure" +
// "certificate is valid" combination - not an actual cert problem) even
// though the wire-level TLS from Caddy outward was genuinely fine.
// KnownNetworks/KnownProxies cleared deliberately: Caddy reaches Kestrel
// via QNAP's own Docker/LXD-backed bridge networking, whose exact source
// IP isn't pinned down (see qnap_infrastructure_setup.md) - acceptable
// here since Kestrel's own port (5152) is never forwarded past the LAN
// (only Caddy's 8880/8443 are internet-facing), so nothing outside the
// LAN can spoof these headers directly against Kestrel.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

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

// UseStaticFiles(), not MapStaticAssets() - the latter (fingerprinted URLs,
// automatic compression, via App.razor's old @Assets[...] references)
// turned out to return every asset as a 200 with an empty body in this
// SDK's Production mode - not just app.css, but _framework/blazor.web.js
// itself, meaning the app would have been completely non-interactive, not
// just unstyled. Confirmed this wasn't a build-vs-publish issue: still
// broken from a full 'dotnet publish' output with the compressed/manifest
// assets actually present on disk. UseStaticFiles serves straight from
// wwwroot with no manifest/endpoint-resolution step to go wrong - see
// App.razor/ReconnectModal.razor for the corresponding plain-path hrefs
// (a manual "?v=" query string substitutes for the cache-busting that
// MapStaticAssets' fingerprinted filenames used to give for free).
app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
