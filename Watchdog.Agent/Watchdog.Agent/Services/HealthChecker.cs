using System.Net.Http;
using Watchdog.Agent.Interface;

namespace Watchdog.Agent.Services;

internal interface IHealthCheckerInternal : IHealthChecker
{
}

public class HealthChecker : IHealthCheckerInternal
{
    private static readonly HttpClient HttpClient = new();

    public async Task<bool> PerformHttpHealthCheck(string url, int timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await HttpClient.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
