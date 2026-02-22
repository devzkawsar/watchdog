using PEPMonitoring;
using PEPMonitoring.Config;
using PEPMonitoring.Contracts;

namespace TestConsole;

public class TestClass : DefaultPEPMonitor
{
    
    public TestClass()
    {
        var grpcAddress = Environment.GetEnvironmentVariable("PEP_MONITORING_GRPC_ADDRESS") ?? "http://localhost:5144";
        var serviceId = Environment.GetEnvironmentVariable("WATCHDOG_APP_ID");
        var instanceId = Environment.GetEnvironmentVariable("WATCHDOG_INSTANCE_ID");
        var agentId = Environment.GetEnvironmentVariable("WATCHDOG_AGENT_ID");
        var intervalSecondsRaw = Environment.GetEnvironmentVariable("PEP_MONITORING_HEARTBEAT_SECONDS") ?? "10";
        
        _ = int.TryParse(intervalSecondsRaw, out var intervalSeconds);
        if (intervalSeconds <= 0)
        {
            intervalSeconds = 10;
        }

        Console.WriteLine($"Ins:- {instanceId} - Ag:- {agentId}");
        var config = new MonitoringConfig
        {
            GrpcAddress = grpcAddress,
            MUUID = serviceId,
            InstanceId = instanceId,
            ApplicationType = 0
        };

        Configure(config);

    }
    
    protected override void Configure(IPEPMonitorConfig config)
    {
        MonitorConfig = config;
        base.Configure(config);
        
    }
}