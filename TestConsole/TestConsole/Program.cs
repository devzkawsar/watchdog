using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestConsole;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<TestConsoleWorker>();
builder.Services.AddWindowsService();

var host = builder.Build();
await host.RunAsync();

public class TestConsoleWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var monitor = new TestClass();
        monitor.Ready();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                monitor.Heartbeat();
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            monitor.Unregister();
        }
    }
}