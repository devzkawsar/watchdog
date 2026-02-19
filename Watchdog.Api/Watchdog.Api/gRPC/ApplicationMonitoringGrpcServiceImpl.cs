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
            "SELECT COUNT(1) FROM application WHERE id = @Id",
            new { Id = applicationId });

        if (exists > 0)
        {
            return;
        }

        const string insertApplicationSql = @"
            INSERT INTO application
                (id, name, display_name, executable_path, arguments, working_directory,
                 application_type,  health_check_interval, heartbeat_timeout,
                 desired_instances, min_instances, max_instances,
                 environment_variables, auto_start, created, created_by)
            VALUES
                (@Id, @Name, @DisplayName, @ExecutablePath, '', '',
                 0, 30, 120,
                 0, 0, 0,
                 '{}', 0, GETUTCDATE(), NULL)";

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
                "SELECT COUNT(1) FROM application WHERE id = @Id",
                new { Id = request.ApplicationId });

            if (exists <= 0)
            {
                const string insertApplicationSql = @"
                    INSERT INTO application
                        (id, name, display_name, executable_path, arguments, working_directory,
                         application_type, heartbeat_timeout,
                         desired_instances, min_instances, max_instances,
                         environment_variables, auto_start, created, created_by)
                    VALUES
                        (@Id, @Name, @DisplayName, @ExecutablePath, @Arguments, '',
                         @ApplicationType, @HeartbeatTimeout,
                         1,1,2,
                         '{}', 0, GETUTCDATE(), NULL)";

                var heartbeatTimeout = Math.Max(30, request.ExpectedHeartbeatIntervalSeconds * 2);

                await connection.ExecuteAsync(insertApplicationSql, new
                {
                    Id = request.ApplicationId,
                    Name = request.ApplicationName,
                    DisplayName = request.ApplicationName,
                    ExecutablePath = request.ExecutablePath ?? string.Empty,
                    Arguments = request.Arguments ?? string.Empty,
                    ApplicationType = request.ApplicationType,
                    HeartbeatTimeout = heartbeatTimeout
                });
            }
            else
            {
                const string updateApplicationSql = @"
                    UPDATE application
                    SET name = @Name,
                        display_name = @DisplayName,
                        executable_path = @ExecutablePath,
                        arguments = @Arguments,
                        application_type = @ApplicationType,
                        updated = GETUTCDATE(),
                        updated_by = NULL
                    WHERE id = @Id";

                await connection.ExecuteAsync(updateApplicationSql, new
                {
                    Id = request.ApplicationId,
                    Name = request.ApplicationName,
                    DisplayName = request.ApplicationName,
                    ExecutablePath = request.ExecutablePath ?? string.Empty,
                    Arguments = request.Arguments ?? string.Empty,
                    ApplicationType = request.ApplicationType
                });
            }

            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    UPDATE application_instance
                    SET application_id = @ApplicationId,
                        status = 'starting',
                        last_heartbeat = GETUTCDATE(),
                        updated_at = GETUTCDATE()
                    WHERE instance_id = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO application_instance
                        (instance_id, application_id, agent_id, status, is_ready, created_at, updated_at, last_heartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'starting', 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            var instanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? request.ApplicationId
                : request.InstanceId;

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId
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
                IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    UPDATE application_instance
                    SET application_id = @ApplicationId,
                        is_ready = 1,
                        ready_at = @ReadyAt,
                        status = 'running',
                        process_id = @ProcessId,
                        assigned_port = @AssignedPort,
                        last_heartbeat = GETUTCDATE(),
                        updated_at = GETUTCDATE()
                    WHERE instance_id = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO application_instance
                        (instance_id, application_id, agent_id, status, is_ready, ready_at, process_id, assigned_port, created_at, updated_at, last_heartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'running', 1, @ReadyAt, @ProcessId, @AssignedPort, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            var readyAt = ToUtcDateTimeFromUnixSeconds(request.TimestampUnixSeconds);

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId,
                ReadyAt = readyAt,
                ProcessId = request.ProcessId > 0 ? (int?)request.ProcessId : null,
                AssignedPort = request.AssignedPort > 0 ? (int?)request.AssignedPort : null
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
                IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    UPDATE application_instance
                    SET application_id = @ApplicationId,
                        last_heartbeat = GETUTCDATE(),
                        status = CASE WHEN is_ready = 1 THEN 'running' ELSE 'starting' END,
                        updated_at = GETUTCDATE()
                    WHERE instance_id = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO application_instance
                        (instance_id, application_id, agent_id, status, is_ready, created_at, updated_at, last_heartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'starting', 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId
            });

            const string sql = @"
                INSERT INTO heartbeat
                    (agent_id, instance_id, application_id, heartbeat_type, is_healthy, metrics, response_time_ms, status_code, status_message, timestamp, received_at)
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
                IF NOT EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    INSERT INTO application_instance
                        (instance_id, application_id, agent_id, status, is_ready, created_at, updated_at, last_heartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, NULL, 'starting', 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId
            });

            const string sql = @"
                INSERT INTO metrics_history (instance_id, application_id, metric_name, metric_value, details, recorded_at, created_at)
                VALUES (@InstanceId, @ApplicationId, @MetricName, @MetricValue, @Details, @RecordedAt, GETUTCDATE())";

            var recordedAt = ToUtcDateTimeFromUnixSeconds(request.RecordedAtUnixSeconds);
            
            await connection.ExecuteAsync(sql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId,
                MetricName = "PerformanceMetric",
                MetricValue = request.Quantity,
                Details = $"{{\"startTimeUnixSeconds\":{request.StartTimeUnixSeconds},\"endTimeUnixSeconds\":{request.EndTimeUnixSeconds},\"quantity\":{request.Quantity},\"delta\":{request.Delta}}}",
                RecordedAt = recordedAt
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
