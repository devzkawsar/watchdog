// See https://aka.ms/new-console-template for more information

using TestConsole;

using var monitor = new TestClass();
monitor.Ready();

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