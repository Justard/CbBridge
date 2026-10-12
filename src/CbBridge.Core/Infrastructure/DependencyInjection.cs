using CbBridge.Core.Application;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Editing;
using CbBridge.Core.Application.Execution;
using CbBridge.Core.Infrastructure.Codebeamer;
using CbBridge.Core.Infrastructure.Framework;

namespace CbBridge.Core.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBridge(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CodebeamerOptions>(config.GetSection("Codebeamer"));
        services.Configure<FrameworkOptions>(config.GetSection("Framework"));
        services.Configure<PostBackOptions>(config.GetSection("PostBack"));
        services.Configure<BrandingOptions>(config.GetSection("Branding"));
        services.Configure<RunMultipleOptions>(config.GetSection("RunMultiple"));
        services.Configure<Application.Ci.CiOptions>(config.GetSection("Ci"));
        services.Configure<Application.Results.ResultStoreOptions>(config.GetSection("Results"));
        services.AddSingleton<Application.Results.ResultStore>();

        services.AddHttpClient("codebeamer", c => c.Timeout = TimeSpan.FromSeconds(100));
        // No HttpClient timeout here: each call gets its own (timeLimitSec + grace).
        services.AddHttpClient("framework", c => c.Timeout = Timeout.InfiniteTimeSpan);

        // Scoped = one per browser tab (Blazor circuit): each user has their own login.
        services.AddScoped<CodebeamerSession>();
        services.AddScoped<ICodebeamerReader, LegacyRestReader>();
        services.AddScoped<ICodebeamerResultWriter, SwaggerResultWriter>();
        services.AddScoped<FrameworkTarget>();
        services.AddScoped<IFrameworkClient, FlaskFrameworkClient>();
        services.AddScoped<PayloadBuilder>();
        services.AddScoped<RunOrchestrator>();
        services.AddScoped<Application.Selection.TestSelection>();

        // CI (Jenkins): jobs live for the whole app; each job creates its own scope with its own session
        services.AddSingleton<Application.Ci.CiJobStore>();
        services.AddSingleton<Application.Ci.CiRunner>();
        return services;
    }
}
