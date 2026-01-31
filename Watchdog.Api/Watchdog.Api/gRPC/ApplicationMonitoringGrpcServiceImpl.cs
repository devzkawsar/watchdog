using Dapper;
using Grpc.Core;
using System.Data;
using Watchdog.Api.Data;
using Watchdog.Api.Protos;

namespace Watchdog.Api.gRPC;

public class ApplicationMonitoringGrpcServiceImpl : ApplicationMonitoringService.ApplicationMonitoringServiceBase
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ApplicationMonitoringGrpcServiceImpl> _logger;

    public ApplicationMonitoringGrpcServiceImpl(
        IDbConnectionFactory connectionFactory,
        ILogger<ApplicationMonitoringGrpcServiceImpl> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private static DateTime ToUtcDateTimeFromUnixSeconds(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return DateTime.UtcNow;
        }

        return DateTime.UnixEpoch.AddSeconds(unixSeconds);
    }

    private static async Task EnsureApplicationExistsAsync(IDbConnection connection, string applicationId)
    {
        var exists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM Applications WHERE Id = @Id",
            new { Id = applicationId });

        if (exists > 0)
        {
            return;
        }

        const string insertApplicationSql = @"
            INSERT INTO Applications
                (Id, Name, DisplayName, ExecutablePath, Arguments, WorkingDirectory,
                 ApplicationType, HealthCheckUrl, HealthCheckInterval, HeartbeatTimeout,
                 DesiredInstances, MinInstances, MaxInstances,
                 PortRequirements, EnvironmentVariables, AutoStart)
            VALUES
                (@Id, @Name, @DisplayName, @ExecutablePath, '', '',
                 0, '', 30, 120,
                 0, 0, 0,
                 '[]', '{}', 0)";

        await connection.ExecuteAsync(insertApplicationSql, new
        {
            Id = applicationId,
            Name = applicationId,
            DisplayName = applicationId,
            ExecutablePath = ""
        });
    }

    public override async Task<ApplicationRegistrationResponse> RegisterApplication(
        ApplicationRegistrationRequest request,
        ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            var exists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM Applications WHERE Id = @Id",
                new { Id = request.ApplicationId });

            if (exists <= 0)
            {
                const string insertApplicationSql = @"
                    INSERT INTO Applications
                        (Id, Name, DisplayName, ExecutablePath, Arguments, WorkingDirectory,
                         ApplicationType, HealthCheckUrl, HealthCheckInterval, HeartbeatTimeout,
                         DesiredInstances, MinInstances, MaxInstances,
                         PortRequirements, EnvironmentVariables, AutoStart)
                    VALUES
                        (@Id, @Name, @DisplayName, @ExecutablePath, '', '',
                         0, '', @HealthCheckInterval, @HeartbeatTimeout,
                         0, 0, 0,
                         '[]', '{}', 0)";

                var heartbeatTimeout = Math.Max(30, request.ExpectedHeartbeatIntervalSeconds * 2);
                var healthCheckInterval = Math.Max(5, request.ExpectedHeartbeatIntervalSeconds);

                await connection.ExecuteAsync(insertApplicationSql, new
                {
                    Id = request.ApplicationId,
                    Name = request.ApplicationName,
                    DisplayName = request.ApplicationName,
                    ExecutablePath = "",
                    HealthCheckInterval = healthCheckInterval,
                    HeartbeatTimeout = heartbeatTimeout
                });
            }
            else
            {
                const string updateApplicationSql = @"
                    UPDATE Applications
                    SET Name = @Name,
                        DisplayName = @DisplayName,
                        UpdatedAt = GETUTCDATE()
                    WHERE Id = @Id";

                await connection.ExecuteAsync(updateApplicationSql, new
                {
                    Id = request.ApplicationId,
                    Name = request.ApplicationName,
                    DisplayName = request.ApplicationName
                });
            }

            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM ApplicationInstances WHERE InstanceId = @InstanceId)
                BEGIN
                    UPDATE ApplicationInstances
                    SET ApplicationId = @ApplicationId,
                        Status = 'Starting',
                        LastHeartbeat = GETUTCDATE(),
                        UpdatedAt = GETUTCDATE()
                    WHERE InstanceId = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO ApplicationInstances
                        (InstanceId, ApplicationId, AgentId, Status, IsReady, CreatedAt, UpdatedAt, LastHeartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'Starting', 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            var instanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? request.ApplicationId
                : request.InstanceId;

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId
            });

            const string sql = @"
                INSERT INTO EventsLog (EventType, EventLevel, EventSource, ApplicationId, InstanceId, Message, Details, Timestamp)
                VALUES (@EventType, @EventLevel, @EventSource, @ApplicationId, @InstanceId, @Message, @Details, GETUTCDATE())";

            await connection.ExecuteAsync(sql, new
            {
                EventType = "ApplicationRegistered",
                EventLevel = "Information",
                EventSource = "ApplicationMonitoringService",
                ApplicationId = request.ApplicationId,
                Message = $"Application registered: {request.ApplicationName}",
                InstanceId = instanceId,
                Details = $"{{\"applicationId\":\"{request.ApplicationId}\",\"instanceId\":\"{instanceId}\",\"applicationName\":\"{request.ApplicationName}\",\"expectedHeartbeatIntervalSeconds\":{request.ExpectedHeartbeatIntervalSeconds},\"registeredAtUnixSeconds\":{request.RegisteredAtUnixSeconds}}}"
            });

            return new ApplicationRegistrationResponse
            {
                Success = true,
                Message = "Registration recorded"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering application {ApplicationId}", request.ApplicationId);
            return new ApplicationRegistrationResponse
            {
                Success = false,
                Message = $"Registration failed: {ex.Message}"
            };
        }
    }

    public override async Task<ApplicationReadyResponse> Ready(
        ApplicationReadyRequest request,
        ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            await EnsureApplicationExistsAsync(connection, request.ApplicationId);

            var instanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? request.ApplicationId
                : request.InstanceId;

            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM ApplicationInstances WHERE InstanceId = @InstanceId)
                BEGIN
                    UPDATE ApplicationInstances
                    SET ApplicationId = @ApplicationId,
                        IsReady = 1,
                        ReadyAt = @ReadyAt,
                        Status = 'Running',
                        LastHeartbeat = GETUTCDATE(),
                        UpdatedAt = GETUTCDATE()
                    WHERE InstanceId = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO ApplicationInstances
                        (InstanceId, ApplicationId, AgentId, Status, IsReady, ReadyAt, CreatedAt, UpdatedAt, LastHeartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'Running', 1, @ReadyAt, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            var readyAt = ToUtcDateTimeFromUnixSeconds(request.TimestampUnixSeconds);

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId,
                ReadyAt = readyAt
            });

            const string sql = @"
                INSERT INTO EventsLog (EventType, EventLevel, EventSource, ApplicationId, InstanceId, Message, Details, Timestamp)
                VALUES (@EventType, @EventLevel, @EventSource, @ApplicationId, @InstanceId, @Message, @Details, GETUTCDATE())";

            await connection.ExecuteAsync(sql, new
            {
                EventType = "ApplicationReady",
                EventLevel = "Information",
                EventSource = "ApplicationMonitoringService",
                ApplicationId = request.ApplicationId,
                InstanceId = instanceId,
                Message = string.IsNullOrWhiteSpace(request.Message) ? "Ready" : request.Message,
                Details = $"{{\"applicationId\":\"{request.ApplicationId}\",\"instanceId\":\"{instanceId}\",\"timestampUnixSeconds\":{request.TimestampUnixSeconds}}}"
            });

            return new ApplicationReadyResponse
            {
                Success = true,
                Message = "Ready recorded"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording ready for application {ApplicationId}", request.ApplicationId);
            return new ApplicationReadyResponse
            {
                Success = false,
                Message = $"Ready failed: {ex.Message}"
            };
        }
    }

    public override async Task<ApplicationHeartbeatResponse> RecordHeartbeat(
        ApplicationHeartbeatRequest request,
        ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            await EnsureApplicationExistsAsync(connection, request.ApplicationId);

            var instanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? request.ApplicationId
                : request.InstanceId;

            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM ApplicationInstances WHERE InstanceId = @InstanceId)
                BEGIN
                    UPDATE ApplicationInstances
                    SET ApplicationId = @ApplicationId,
                        LastHeartbeat = GETUTCDATE(),
                        Status = CASE WHEN IsReady = 1 THEN 'Running' ELSE 'Starting' END,
                        UpdatedAt = GETUTCDATE()
                    WHERE InstanceId = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO ApplicationInstances
                        (InstanceId, ApplicationId, AgentId, Status, IsReady, CreatedAt, UpdatedAt, LastHeartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'Starting', 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId
            });

            const string sql = @"
                INSERT INTO Heartbeats
                    (AgentId, InstanceId, ApplicationId, HeartbeatType, IsHealthy, Metrics, ResponseTimeMs, StatusCode, StatusMessage, Timestamp, ReceivedAt)
                VALUES
                    (NULL, @InstanceId, @ApplicationId, 'Instance', @IsHealthy, @Metrics, NULL, NULL, @StatusMessage, @Timestamp, GETUTCDATE())";

            var statusMessage = string.IsNullOrWhiteSpace(request.Message) ? request.Status : request.Message;
            var timestamp = ToUtcDateTimeFromUnixSeconds(request.TimestampUnixSeconds);

            await connection.ExecuteAsync(sql, new
            {
                ApplicationId = request.ApplicationId,
                InstanceId = instanceId,
                IsHealthy = request.IsHealthy,
                Metrics = request.MetricsJson,
                StatusMessage = statusMessage,
                Timestamp = timestamp
            });

            return new ApplicationHeartbeatResponse
            {
                Success = true,
                Message = "Heartbeat recorded"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording heartbeat for application {ApplicationId}", request.ApplicationId);
            return new ApplicationHeartbeatResponse
            {
                Success = false,
                Message = $"Heartbeat failed: {ex.Message}"
            };
        }
    }

    public override async Task<PerformanceMetricResponse> RecordPerformanceMetric(
        PerformanceMetricRequest request,
        ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            await EnsureApplicationExistsAsync(connection, request.ApplicationId);

            var instanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? request.ApplicationId
                : request.InstanceId;

            const string upsertInstanceSql = @"
                IF NOT EXISTS (SELECT 1 FROM ApplicationInstances WHERE InstanceId = @InstanceId)
                BEGIN
                    INSERT INTO ApplicationInstances
                        (InstanceId, ApplicationId, AgentId, Status, IsReady, CreatedAt, UpdatedAt, LastHeartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'Starting', 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId
            });

            const string sql = @"
                INSERT INTO EventsLog (EventType, EventLevel, EventSource, ApplicationId, InstanceId, Message, Details, Timestamp)
                VALUES (@EventType, @EventLevel, @EventSource, @ApplicationId, @InstanceId, @Message, @Details, GETUTCDATE())";

            await connection.ExecuteAsync(sql, new
            {
                EventType = "PerformanceMetric",
                EventLevel = "Information",
                EventSource = "ApplicationMonitoringService",
                ApplicationId = request.ApplicationId,
                InstanceId = instanceId,
                Message = "Performance metric recorded",
                Details = $"{{\"applicationId\":\"{request.ApplicationId}\",\"instanceId\":\"{instanceId}\",\"startTimeUnixSeconds\":{request.StartTimeUnixSeconds},\"endTimeUnixSeconds\":{request.EndTimeUnixSeconds},\"quantity\":{request.Quantity},\"delta\":{request.Delta},\"recordedAtUnixSeconds\":{request.RecordedAtUnixSeconds}}}"
            });

            return new PerformanceMetricResponse
            {
                Success = true,
                Message = "Metric recorded"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording metric for application {ApplicationId}", request.ApplicationId);
            return new PerformanceMetricResponse
            {
                Success = false,
                Message = $"Metric failed: {ex.Message}"
            };
        }
    }
}
