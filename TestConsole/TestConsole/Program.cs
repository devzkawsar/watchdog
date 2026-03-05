// See https://aka.ms/new-console-template for more information

using PEPMonitoring.Abstract;
using PEPMonitoring.Request;
using TestConsole;

using var monitor = new TestClass();

await monitor.Register();
await monitor.Ready();

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
        monitor.Heartbeat();
        monitor.RecordMetrics(new QueueMetricsSnapshot
        {
            Payload = new QueuePayloadRequest
            {
                QueueLength = 10,
                QueueName = "Job"
            }
        });
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
    }
}
catch (OperationCanceledException)
{
}
finally
{
    monitor.Unregister();
}