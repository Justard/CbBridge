using CbBridge.Web.Application;
using CbBridge.Web.Application.Abstractions;
using CbBridge.Web.Application.Editing;
using CbBridge.Web.Application.Execution;
using CbBridge.Web.Infrastructure.Codebeamer;
using CbBridge.Web.Infrastructure.Framework;

namespace CbBridge.Web.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBridge(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CodebeamerOptions>(config.GetSection("Codebeamer"));
        services.Configure<FrameworkOptions>(config.GetSection("Framework"));
        services.Configure<PostBackOptions>(config.GetSection("PostBack"));

        services.AddHttpClient("codebeamer", c => c.Timeout = TimeSpan.FromSeconds(100));
        // No HttpClient timeout here: each call gets its own (timeLimitSec + grace).
        services.AddHttpClient("framework", c => c.Timeout = Timeout.InfiniteTimeSpan);

        // Scoped = one per browser tab (Blazor circuit): each user has their own login.
        services.AddScoped<CodebeamerSession>();
        services.AddScoped<ICodebeamerReader, LegacyRestReader>();
        services.AddScoped<ICodebeamerResultWriter, SwaggerResultWriter>();
        services.AddScoped<FrameworkTarget>();
        services.AddScoped<IFrameworkClient, FlaskFrameworkClient>();
        services.AddScoped<Browser.BrowserStore>();
        services.AddScoped<PayloadBuilder>();
        services.AddScoped<RunOrchestrator>();
        return services;
    }
}
