namespace Watchdog.Agent.Configuration;

public class ControlPlaneSettings
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string GrpcEndpoint { get; set; } = "http://localhost:5001";
    public int HttpTimeoutSeconds { get; set; } = 30;
    public int GrpcTimeoutSeconds { get; set; } = 30;
    public int ReconnectIntervalSeconds { get; set; } = 10;
    public int MaxReconnectAttempts { get; set; } = 50;
    public string ApiKey { get; set; } = string.Empty;
    public string AuthenticationToken { get; set; } = string.Empty;
}