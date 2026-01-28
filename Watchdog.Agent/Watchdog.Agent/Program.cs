using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Protos;
using Watchdog.Agent.Services;
using Watchdog.Agent.WindowsService;
using ConfigurationManager = Watchdog.Agent.Services.ConfigurationManager;
using IConfigurationManager = Watchdog.Agent.Interface.IConfigurationManager;

namespace Watchdog.Agent;

public class Program
{
    public static void Main(string[] args)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args);

        if (OperatingSystem.IsWindows())
        {
            builder = builder.UseWindowsService(config =>
            {
                config.ServiceName = "WatchdogAgent";
            });
        }

        return builder
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
                services.AddSingleton<IGrpcClient, GrpcClient>();
                services.AddSingleton<INetworkManager, NetworkManager>();
                services.AddSingleton<ICommandExecutor, CommandExecutor>();
                services.AddSingleton<IConfigurationManager, ConfigurationManager>();
                
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
                services.AddGrpcClient<AgentService.AgentServiceClient>((serviceProvider, options) =>
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
    }

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