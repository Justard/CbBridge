using CbBridge.Web.Components;
using CbBridge.Core.Infrastructure;
using CbBridge.Web.Infrastructure.Browser;
using CbBridge.Web.Ci;
using CbBridge.Web.Api;
using CbBridge.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddBridge(builder.Configuration);
builder.Services.AddScoped<BrowserStore>();
builder.Services.AddScoped<CbBridge.Web.Infrastructure.History.RerunInbox>();   // history → console   // web only: per-user settings in the browser

var app = builder.Build();

// Branding check: warn early if a company colour would make white text unreadable.
{
    var brand = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CbBridge.Core.Application.BrandingOptions>>().Value;
    foreach (var (key, value, fallback) in new[]
             {
                 ("Branding:AccentColor", brand.AccentColor, CbBridge.Core.Application.BrandingTheme.DefaultAccent),
                 ("Branding:HeaderColor", brand.HeaderColor, CbBridge.Core.Application.BrandingTheme.DefaultHeader)
             })
    {
        var used = CbBridge.Core.Application.BrandingTheme.SafeColor(value, fallback);
        if (used != value?.Trim())
            app.Logger.LogWarning("{Key} '{Value}' is not a #RRGGBB colour; using {Fallback}", key, value, fallback);
        else if (CbBridge.Core.Application.BrandingTheme.ContrastWithWhite(used) < 4.5)
            app.Logger.LogWarning("{Key} {Value} is too light for white text (contrast {Ratio:0.0}:1, needs 4.5:1). Pick a darker shade.",
                key, used, CbBridge.Core.Application.BrandingTheme.ContrastWithWhite(used));
    }
}

// Licence state into the startup log (and locked state, if so)
app.Services.GetRequiredService<CbBridge.Core.Licensing.LicenceService>();

// API client configuration problems (unknown scope, missing key) → startup warnings
foreach (var problem in app.Services.GetRequiredService<CbBridge.Core.Application.Api.ApiClientRegistry>().Problems())
    app.Logger.LogWarning("Api:Clients: {Problem}", problem);

// Open (create/migrate) the run history database now, so a missing SQLite library shows in the startup log.
app.Services.GetRequiredService<CbBridge.Core.Application.Abstractions.IResultRepository>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// Licence lock for machine access: everything under /api, /mcp and /metrics answers 402 without a valid
// licence (the UI shows the licence page instead). /health and the licence page stay reachable.
app.Use(async (http, next) =>
{
    var path = http.Request.Path;
    if ((path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp") || path.StartsWithSegments("/metrics"))
        && !path.StartsWithSegments("/api/openapi.json")
        && http.RequestServices.GetRequiredService<CbBridge.Core.Licensing.LicenceService>() is { AllowsUse: false } licence)
    {
        http.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        await http.Response.WriteAsJsonAsync(new { status = 402, title = "Licence required", detail = licence.Check.Message });
        return;
    }
    await next();
});

app.MapCiApi();   // Jenkins / CI — off unless Ci:Enabled
app.MapAnalysisCallback();   // AI agent answers — off unless Analysis:Enabled
app.MapHistoryApi();         // /api/v1 for agents, dashboards, scripts — off unless Api:Enabled
app.MapMcp();                // /mcp — the same, as MCP tools for AI agents — off unless Api:Enabled
app.MapGet("/health", () => Results.Json(new { status = "ok" }));   // load balancers / uptime checks
app.MapGet("/metrics", (HttpContext http, CbBridge.Core.Application.BridgeMetrics metrics,
    CbBridge.Core.Application.Execution.BenchRegistry benches,
    Microsoft.Extensions.Options.IOptions<CbBridge.Core.Application.Api.BridgeOptions> bridge,
    CbBridge.Core.Application.Api.ApiClientRegistry clients) =>
{
    // Prometheus text. Public only when Bridge:MetricsPublic; otherwise a key with history:read.
    var client = clients.Find(ApiAuth.PresentedKey(http.Request, allowBearer: true));
    if (!bridge.Value.MetricsPublic && client?.Can(CbBridge.Core.Application.Api.Scopes.HistoryRead) != true)
        return Results.StatusCode(401);
    return Results.Text(metrics.Prometheus(benches.Active.Count), "text/plain; version=0.0.4");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
