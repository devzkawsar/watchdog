using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Protos;
using Watchdog.Agent.Services;
using Watchdog.Agent.WindowsService;

namespace Watchdog.Agent;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService(config =>
            {
                config.ServiceName = "WatchdogAgent";
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                // Add configuration sources
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", 
                    optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables();
                config.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                // Configuration
                services.Configure<AgentSettings>(context.Configuration.GetSection("Agent"));
                services.Configure<ControlPlaneSettings>(context.Configuration.GetSection("ControlPlane"));
                services.Configure<NetworkSettings>(context.Configuration.GetSection("Network"));
                services.Configure<ProcessSettings>(context.Configuration.GetSection("Process"));
                services.Configure<MonitoringSettings>(context.Configuration.GetSection("Monitoring"));
                
                // Services
                services.AddSingleton<IApplicationManager, ApplicationManager>();
                services.AddSingleton<IProcessManager, ProcessManager>();
                services.AddSingleton<IMonitorService, MonitorService>();
                services.AddSingleton<IGrpcClient, GrpcClient>();
                services.AddSingleton<INetworkManager, NetworkManager>();
                services.AddSingleton<IHealthChecker, HealthChecker>();
                services.AddSingleton<ICommandExecutor, CommandExecutor>();
                services.AddSingleton<IMetricsCollector, MetricsCollector>();
                services.AddSingleton<Watchdog.Agent.Services.IConfigurationManagerInternal, Watchdog.Agent.Services.ConfigurationManager>();
                
                // Hosted service
                services.AddHostedService<AgentWorker>();
                
                // HTTP Client for control plane communication
                services.AddHttpClient("ControlPlane", (serviceProvider, client) =>
                {
                    var config = serviceProvider.GetRequiredService<IOptions<ControlPlaneSettings>>().Value;
                    client.BaseAddress = new Uri(config.BaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds);
                    client.DefaultRequestHeaders.Add("User-Agent", "Watchdog-Agent");
                    client.DefaultRequestHeaders.Add("X-Agent-ID", 
                        serviceProvider.GetRequiredService<IOptions<AgentSettings>>().Value.AgentId);
                });
                
                // gRPC Client
                services.AddGrpcClient<ControlPlaneService.ControlPlaneServiceClient>((serviceProvider, options) =>
                {
                    var config = serviceProvider.GetRequiredService<IOptions<ControlPlaneSettings>>().Value;
                    options.Address = new Uri(config.GrpcEndpoint);
                })
                .ConfigurePrimaryHttpMessageHandler(() => 
                {
                    var handler = new HttpClientHandler();
                    // Configure SSL/TLS settings if needed
                    if (Environment.GetEnvironmentVariable("WATCHDOG_IGNORE_SSL") == "true")
                    {
                        handler.ServerCertificateCustomValidationCallback = 
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }
                    return handler;
                });
                
                // Add health checks
                services.AddHealthChecks()
                    .AddCheck<AgentHealthCheck>("agent_health_check");
                
                // Add logging
                services.AddLogging();
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                
                if (OperatingSystem.IsWindows())
                {
                    AddWindowsEventLog(logging);
                }
                
                logging.AddConsole();
                logging.AddDebug();
                
                // Add file logging
                var logPath = context.Configuration["Logging:File:Path"] ?? 
                    Path.Combine(AppContext.BaseDirectory, "logs", "watchdog-agent.log");
                logging.AddFile(logPath, 
                    minimumLevel: LogLevel.Information,
                    fileSizeLimitBytes: 10 * 1024 * 1024, // 10MB
                    retainedFileCountLimit: 5);
            });

    [SupportedOSPlatform("windows")]
    private static void AddWindowsEventLog(ILoggingBuilder logging)
    {
        logging.AddEventLog(config =>
        {
            config.SourceName = "WatchdogAgent";
            config.LogName = "Application";
        });
    }
}