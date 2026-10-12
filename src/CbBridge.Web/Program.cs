using CbBridge.Web.Components;
using CbBridge.Core.Infrastructure;
using CbBridge.Web.Infrastructure.Browser;
using CbBridge.Web.Ci;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddBridge(builder.Configuration);
builder.Services.AddScoped<BrowserStore>();   // web only: per-user settings in the browser

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapCiApi();   // Jenkins / CI — off unless Ci:Enabled

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
