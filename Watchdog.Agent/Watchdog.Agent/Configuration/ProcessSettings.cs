namespace Watchdog.Agent.Configuration;

public class ProcessSettings
{
    public int MaxConcurrentProcesses { get; set; } = 100;
    public int ProcessStartTimeoutSeconds { get; set; } = 30;
    public int ProcessStopTimeoutSeconds { get; set; } = 30;
    public bool CaptureOutput { get; set; } = true;
    public int MaxOutputBufferSize { get; set; } = 1024 * 1024; // 1MB
    public bool RedirectOutput { get; set; } = true;
    public bool CreateNoWindow { get; set; } = true;
    public bool UseShellExecute { get; set; } = false;
}