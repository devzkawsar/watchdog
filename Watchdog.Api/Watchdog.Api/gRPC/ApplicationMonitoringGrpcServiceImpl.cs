using System.Text.Json;
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
    }

    public override async Task<ApplicationRegistrationResponse> RegisterApplication(
        ApplicationRegistrationRequest request,
        ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            var exists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM application WHERE Id = @Id",
                new { Id = request.ApplicationId });

            if (exists <= 0)
            {
                return new ApplicationRegistrationResponse
                {
                    Success = false,
                    Message = "No Application found"
                };
            }

            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    UPDATE application_instance
                    SET application_id = @ApplicationId,
                        status = 'Starting',
                        agent_id=@AgentId,
                        last_heartbeat = GETUTCDATE(),
                        updated_at = GETUTCDATE()
                    WHERE instance_id = @InstanceId
                END
                ELSE
                BEGIN
                    INSERT INTO application_instance
                        (instance_id, application_id, agent_id, status, started_at, is_ready, created_at, updated_at, last_heartbeat)
                    VALUES
                        (@InstanceId, @ApplicationId, @AgentId, 'Starting', GETUTCDATE(), 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                END";

            var instanceId = request.InstanceId;

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
                ApplicationId = request.ApplicationId,
                AgentId = request.AgentId
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

            var instanceId = request.InstanceId;
            
            _logger.LogDebug("Error recording ready for application instance {ApplicationId}", instanceId);

            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    UPDATE application_instance
                    SET
                        is_ready = 1,
                        ready_at = @ReadyAt,
                        status = 'running',
                        process_id = @ProcessId,
                        assigned_port = @AssignedPort,
                        last_heartbeat = GETUTCDATE(),
                        updated_at = GETUTCDATE()
                    WHERE instance_id = @InstanceId
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

            var instanceId =  request.InstanceId;
            const string upsertInstanceSql = @"
                IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                BEGIN
                    UPDATE application_instance
                    SET last_heartbeat = GETUTCDATE(),
                        status = 'running',
                        updated_at = GETUTCDATE()
                    WHERE instance_id = @InstanceId
                END";

            await connection.ExecuteAsync(upsertInstanceSql, new
            {
                InstanceId = instanceId,
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

    public override async Task<RecordMetricsResponse> RecordMetrics(
        RecordMetricsRequest request,
        ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            var instanceId = string.IsNullOrWhiteSpace(request.InstanceId) ? null : request.InstanceId;

            if (instanceId == null)
            {
                _logger.LogError("Error recording metrics for instance {InstanceId}", request.InstanceId);
                return new RecordMetricsResponse
                {
                    Success = false,
                    Message = $"Metrics failed: InstanceId is null"
                };
            }

            var timestamp = request.TimestampUnixSeconds > 0
                ? ToUtcDateTimeFromUnixSeconds(request.TimestampUnixSeconds)
                : DateTime.UtcNow;

            // 1. Insert parent row into metrics_snapshot
            const string insertSnapshotSql = @"
                INSERT INTO metrics (instance_id, metric_type, payload, timestamp, server_name, payload_generate_datetime)
                OUTPUT INSERTED.id
                VALUES (@InstanceId, @MetricType, @Payload, @Timestamp, @ServerName, @PayloadGenerateDatetime)";

            var metricId = await connection.ExecuteScalarAsync<long>(insertSnapshotSql, new
            {
                InstanceId = instanceId,
                MetricType = request.MetricType,
                Payload = request.Payload,
                Timestamp = timestamp,
                ServerName = (string?)null,
                PayloadGenerateDatetime = timestamp
            });

            // 2. Insert into child table based on MetricType
            if (!string.IsNullOrWhiteSpace(request.Payload))
            {
                using var doc = JsonDocument.Parse(request.Payload);
                var root = doc.RootElement;

                decimal GetDecimal(string prop) => root.TryGetProperty(prop, out var p) ? (decimal)p.GetDouble() : 0m;
                double GetDouble(string prop) => root.TryGetProperty(prop, out var p) ? p.GetDouble() : 0d;
                long GetLong(string prop) => root.TryGetProperty(prop, out var p) ? p.GetInt64() : 0L;
                int GetInt(string prop) => root.TryGetProperty(prop, out var p) ? p.GetInt32() : 0;
                string GetString(string prop) => root.TryGetProperty(prop, out var p) ? p.GetString() : null;

                switch (request.MetricType)
                {
                    case 1: // Machine
                        const string machSql = @"
                            INSERT INTO machine_metrics (metric_id, cpu_percent, memory_mb, memory_percent, disk_usage_percent, thread_count, handle_count)
                            VALUES (@MetricID, @CpuPercent, @MemoryMb, @MemoryPercent, @DiskUsagePercent, @ThreadCount, @HandleCount)";
                        await connection.ExecuteAsync(machSql, new
                        {
                            MetricID = metricId,
                            CpuPercent = GetDecimal("CpuPercent"),
                            MemoryMb = GetDecimal("MemoryMb"),
                            MemoryPercent = GetDecimal("MemoryPercent"),
                            DiskUsagePercent = GetDecimal("DiskUsagePercent"),
                            ThreadCount = GetInt("ThreadCount"),
                            HandleCount = GetInt("HandleCount")
                        });
                        break;

                    case 2: // Queue
                        const string queueSql = @"
                            INSERT INTO queue_metrics (metric_id, queue_name, queue_length, queue_ready, queue_unacknowledged, incoming_per_sec, deliver_per_sec, ack_per_sec, consumer_count)
                            VALUES (@MetricID, @QueueName, @QueueLength, @QueueReady, @QueueUnacknowledged, @IncomingPerSec, @DeliverPerSec, @AckPerSec, @ConsumerCount)";
                        await connection.ExecuteAsync(queueSql, new
                        {
                            MetricID = metricId,
                            QueueName = GetString("QueueName"),
                            QueueLength = GetLong("QueueLength"),
                            QueueReady = GetLong("QueueReady"),
                            QueueUnacknowledged = GetLong("QueueUnacknowledged"),
                            IncomingPerSec = GetDouble("IncomingPerSec"),
                            DeliverPerSec = GetDouble("DeliverPerSec"),
                            AckPerSec = GetDouble("AckPerSec"),
                            ConsumerCount = GetInt("ConsumerCount")
                        });
                        break;

                    case 3: // Throughput
                        const string tpSql = @"
                            INSERT INTO throughput_metrics (metric_id, messages_published_per_sec, messages_consumed_per_sec, messages_acked_per_sec, redelivered_per_sec, returned_unroutable_per_sec, global_queue_ready, global_queue_unacknowledged, global_queue_total)
                            VALUES (@MetricID, @MessagesPublishedPerSec, @MessagesConsumedPerSec, @MessagesAckedPerSec, @RedeliveredPerSec, @ReturnedUnroutablePerSec, @GlobalQueueReady, @GlobalQueueUnacknowledged, @GlobalQueueTotal)";
                        await connection.ExecuteAsync(tpSql, new
                        {
                            MetricID = metricId,
                            MessagesPublishedPerSec = GetDouble("MessagesPublishedPerSec"),
                            MessagesConsumedPerSec = GetDouble("MessagesConsumedPerSec"),
                            MessagesAckedPerSec = GetDouble("MessagesAckedPerSec"),
                            RedeliveredPerSec = GetDouble("RedeliveredPerSec"),
                            ReturnedUnroutablePerSec = GetDouble("ReturnedUnroutablePerSec"),
                            GlobalQueueReady = GetInt("GlobalQueueReady"),
                            GlobalQueueUnacknowledged = GetInt("GlobalQueueUnacknowledged"),
                            GlobalQueueTotal = GetInt("GlobalQueueTotal")
                        });
                        break;

                    case 4: // Latency
                        const string latSql = @"
                            INSERT INTO latency_metrics (metric_id, message_latency_ms, ack_latency_ms, end_to_end_latency_ms, p50_latency_ms, p95_latency_ms, p99_latency_ms, max_latency_ms, min_latency_ms)
                            VALUES (@MetricID, @MessageLatencyMs, @AckLatencyMs, @EndToEndLatencyMs, @P50LatencyMs, @P95LatencyMs, @P99LatencyMs, @MaxLatencyMs, @MinLatencyMs)";
                        await connection.ExecuteAsync(latSql, new
                        {
                            MetricID = metricId,
                            MessageLatencyMs = GetDouble("MessageLatencyMs"),
                            AckLatencyMs = GetDouble("AckLatencyMs"),
                            EndToEndLatencyMs = GetDouble("EndToEndLatencyMs"),
                            P50LatencyMs = GetDouble("P50LatencyMs"),
                            P95LatencyMs = GetDouble("P95LatencyMs"),
                            P99LatencyMs = GetDouble("P99LatencyMs"),
                            MaxLatencyMs = GetDouble("MaxLatencyMs"),
                            MinLatencyMs = GetDouble("MinLatencyMs")
                        });
                        break;

                    default:
                        _logger.LogWarning("Unknown MetricType {MetricType} for instance {InstanceId}", request.MetricType, instanceId);
                        break;
                }
            }

            return new RecordMetricsResponse
            {
                Success = true,
                Message = "Metrics recorded"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording metrics for instance {InstanceId}", request.InstanceId);
            return new RecordMetricsResponse
            {
                Success = false,
                Message = $"Metrics failed: {ex.Message}"
            };
        }
    }
}

