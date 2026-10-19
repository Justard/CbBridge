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
        services.Configure<Application.History.DatabaseOptions>(config.GetSection("Database"));
        services.AddSingleton<IResultRepository, Storage.SqliteResultRepository>();
        services.AddSingleton<Application.Results.RunRecorder>();

        // API clients (Jenkins, n8n, MCP, scripts) and events to other systems
        services.Configure<Application.Api.ApiOptions>(config.GetSection("Api"));
        services.Configure<Application.Api.BridgeOptions>(config.GetSection("Bridge"));
        services.Configure<Application.Events.EventOptions>(config.GetSection("Events"));
        services.AddSingleton<Application.Api.ApiClientRegistry>();
        services.AddSingleton<Events.WebhookEventPublisher>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<Events.WebhookEventPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<Events.WebhookEventPublisher>());
        services.AddSingleton<Application.Events.RunEvents>();
        services.AddHttpClient("events");
        services.Configure<AnalysisOptions>(config.GetSection("Analysis"));
        services.AddSingleton<Application.Analysis.AnalysisCallbacks>();
        services.AddScoped<IAnalysisAgent, Analysis.WebhookAnalysisAgent>();
        services.AddScoped<Application.Analysis.FailureAnalyzer>();
        services.AddHttpClient("analysis", c => c.Timeout = Timeout.InfiniteTimeSpan);   // per call: Analysis:RequestTimeoutSec

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
        services.AddSingleton<BenchRegistry>();
        services.AddSingleton<BridgeMetrics>();
        services.Configure<AccessOptions>(config.GetSection("Access"));
        services.Configure<Licensing.LicensingOptions>(config.GetSection("Licensing"));
        services.AddSingleton<Licensing.LicenceService>();
        services.AddScoped<Application.Selection.TestSelection>();

        // CI (Jenkins): jobs live for the whole app; each job creates its own scope with its own session
        services.AddSingleton<Application.Ci.CiJobStore>();
        services.AddSingleton<Application.Ci.CiRunner>();
        return services;
    }
}
