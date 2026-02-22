// See https://aka.ms/new-console-template for more information

using TestConsole;

using var monitor = new TestClass();

var processId = Environment.ProcessId;
var portStr = Environment.GetEnvironmentVariable("PORT");
int.TryParse(portStr, out var assignedPort);

var ready = await monitor.ReadyAsync(processId: processId, assignedPort: assignedPort);
if (!ready)
{
    Console.WriteLine("Ready failed. Check console error output and Watchdog.Api logs for details.");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var heartbeatOk = await monitor.HeartbeatAsync();
        Console.WriteLine($"Heartbeat result: {heartbeatOk} at {DateTime.UtcNow:O}");
        if (!heartbeatOk)
        {
            Console.WriteLine("Heartbeat failed. Check console error output and Watchdog.Api logs for details.");
        }
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
    }
}
catch (OperationCanceledException)
{
}
finally
{
    var unregistered = await monitor.UnregisterAsync();
    Console.WriteLine($"Unregister result: {unregistered}");
}