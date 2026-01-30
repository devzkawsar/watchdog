
-- =============================================
-- 1. APPLICATIONS TABLE (Desired State)
-- =============================================
CREATE TABLE [Applications] (
    [Id] VARCHAR(50) NOT NULL PRIMARY KEY,
    [Name] VARCHAR(100) NOT NULL,
    [DisplayName] VARCHAR(200),
    [Description] VARCHAR(500),

    -- Application Configuration
    [ExecutablePath] VARCHAR(500) NOT NULL,
    [Arguments] VARCHAR(1000),
    [WorkingDirectory] VARCHAR(500),
    [ApplicationType] INT NOT NULL DEFAULT 0, -- 0=Console, 1=Windows Service, 2=IIS

-- Health Check Configuration
    [HealthCheckUrl] VARCHAR(500),
    [HealthCheckInterval] INT DEFAULT 30, -- Seconds
    [StartupTimeout] INT DEFAULT 180, -- Seconds
    [HeartbeatTimeout] INT DEFAULT 120, -- Seconds

-- Scaling Configuration
    [DesiredInstances] INT DEFAULT 1,
    [MinInstances] INT DEFAULT 1,
    [MaxInstances] INT DEFAULT 5,

    -- Port Requirements (JSON)
    [PortRequirements] VARCHAR(MAX) DEFAULT '[]',

    -- Environment Variables (JSON)
    [EnvironmentVariables] VARCHAR(MAX) DEFAULT '{}',

    -- Behavior Configuration
    [AutoStart] BIT DEFAULT 1,
    [MaxRestartAttempts] INT DEFAULT 3,
    [RestartDelaySeconds] INT DEFAULT 10,
    [StopTimeoutSeconds] INT DEFAULT 30,

    -- Metadata
    [CreatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [CreatedBy] VARCHAR(100),
    [UpdatedBy] VARCHAR(100),

    -- Status
    [Status] INT DEFAULT 0, -- 0=Inactive, 1=Active, 2=Paused, 3=Error

-- Constraints
    CONSTRAINT [CK_Applications_DesiredInstances]
    CHECK ([DesiredInstances] >= 0),
    CONSTRAINT [CK_Applications_MinInstances]
    CHECK ([MinInstances] >= 0),
    CONSTRAINT [CK_Applications_MaxInstances]
    CHECK ([MaxInstances] >= [MinInstances]),
    CONSTRAINT [CK_Applications_HealthCheckInterval]
    CHECK ([HealthCheckInterval] BETWEEN 5 AND 300)
    );


-- =============================================
-- 2. AGENTS TABLE (Worker Nodes)
-- =============================================
CREATE TABLE [Agents] (
    [Id] VARCHAR(50) NOT NULL PRIMARY KEY,
    [Name] VARCHAR(100) NOT NULL,
    [DisplayName] VARCHAR(200),

    -- Network Information
    [IpAddress] VARCHAR(45) NOT NULL,
    [Hostname] VARCHAR(255),
    [FQDN] VARCHAR(255),

    -- System Resources
    [TotalMemoryMB] BIGINT,
    [AvailableMemoryMB] BIGINT,
    [CpuCores] INT,
    [CpuModel] VARCHAR(100),
    [TotalDiskGB] BIGINT,
    [AvailableDiskGB] BIGINT,
    [OsVersion] VARCHAR(100),
    [DotNetVersion] VARCHAR(50),

    -- Port Management (JSON array of available ports)
    [AvailablePorts] VARCHAR(MAX) DEFAULT '[]',
    [PortRangeStart] INT DEFAULT 30000,
    [PortRangeEnd] INT DEFAULT 40000,

    -- Status
    [Status] VARCHAR(20) DEFAULT 'Offline', -- Offline, Online, Draining, Maintenance
    [LastHeartbeat] DATETIME2,
    [UptimeSeconds] BIGINT DEFAULT 0,

    -- Capacity Tracking
    [MaxConcurrentProcesses] INT DEFAULT 100,
    [CurrentProcessCount] INT DEFAULT 0,
    [TotalSpawnedProcesses] INT DEFAULT 0,
    [TotalFailedSpawns] INT DEFAULT 0,

    -- Metadata
    [RegisteredAt] DATETIME2 DEFAULT GETUTCDATE(),
    [LastSeenAt] DATETIME2,
    [Tags] VARCHAR(MAX) DEFAULT '[]', -- JSON array of tags

-- Constraints
    CONSTRAINT [CK_Agents_Status]
    CHECK ([Status] IN ('Offline', 'Online', 'Draining', 'Maintenance'))
    );


-- =============================================
-- 3. APPLICATION INSTANCES TABLE (Actual State)
-- =============================================
CREATE TABLE [ApplicationInstances] (
    [InstanceId] VARCHAR(100) NOT NULL PRIMARY KEY,
    [ApplicationId] VARCHAR(50) NOT NULL,
    [AgentId] VARCHAR(50) NULL,

    -- Process Information
    [ProcessId] INT,
    [ProcessName] VARCHAR(255),
    [CommandLine] VARCHAR(1000),

    -- Status
    [Status] VARCHAR(20) DEFAULT 'Pending', -- Pending, Starting, Running, Stopping, Stopped, Error
    [ExitCode] INT,
    [ExitReason] VARCHAR(500),

    -- Resource Usage
    [CpuPercent] DECIMAL(5,2),
    [MemoryMB] DECIMAL(10,2),
    [MemoryPercent] DECIMAL(5,2),
    [ThreadCount] INT,
    [HandleCount] INT,

    -- Port Assignments (JSON)
    [AssignedPorts] VARCHAR(MAX),

    -- Timing
    [StartedAt] DATETIME2,
    [StoppedAt] DATETIME2,
    [LastHeartbeat] DATETIME2,
    [LastHealthCheck] DATETIME2,

    -- Lifecycle
    [IsReady] BIT DEFAULT 0,
    [ReadyAt] DATETIME2,

    -- Restart Tracking
    [RestartCount] INT DEFAULT 0,
    [LastRestartAttempt] DATETIME2,
    [AutoRestartEnabled] BIT DEFAULT 1,

    -- Error Tracking
    [LastError] VARCHAR(1000),
    [ErrorCount] INT DEFAULT 0,

    -- Metadata
    [CreatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2 DEFAULT GETUTCDATE(),

    -- Foreign Keys
    FOREIGN KEY ([ApplicationId]) REFERENCES [Applications]([Id]),
    FOREIGN KEY ([AgentId]) REFERENCES [Agents]([Id]),

    -- Constraints
    CONSTRAINT [CK_ApplicationInstances_Status]
    CHECK ([Status] IN ('Pending', 'Starting', 'Running', 'Stopping', 'Stopped', 'Error'))
    );


-- =============================================
-- 4. AGENT APPLICATIONS TABLE (Assignment)
-- =============================================
CREATE TABLE [AgentApplications] (
    [AgentId] VARCHAR(50) NOT NULL,
    [ApplicationId] VARCHAR(50) NOT NULL,

    -- Assignment Configuration
    [MaxInstancesOnAgent] INT DEFAULT 10,
    [CurrentInstancesOnAgent] INT DEFAULT 0,
    [Priority] INT DEFAULT 0, -- Lower number = higher priority

-- Scheduling Preferences
    [PreferThisAgent] BIT DEFAULT 0,
    [AvoidThisAgent] BIT DEFAULT 0,

    -- Metadata
    [AssignedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [AssignedBy] VARCHAR(100),
    [LastAssignedInstance] DATETIME2,

    -- Composite Primary Key
    PRIMARY KEY ([AgentId], [ApplicationId]),

    -- Foreign Keys
    FOREIGN KEY ([AgentId]) REFERENCES [Agents]([Id]),
    FOREIGN KEY ([ApplicationId]) REFERENCES [Applications]([Id])
    );

-- =============================================
-- 5. COMMAND QUEUE TABLE (Orchestration Commands)
-- =============================================
CREATE TABLE [CommandQueue] (
    [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,
    [CommandId] VARCHAR(50) NOT NULL UNIQUE,

    -- Command Details
    [CommandType] VARCHAR(20) NOT NULL, -- SPAWN, KILL, RESTART, UPDATE, STOP_ALL
    [AgentId] VARCHAR(50) NOT NULL,
    [ApplicationId] VARCHAR(50),
    [InstanceId] VARCHAR(100),

    -- Command Parameters (JSON)
    [Parameters] VARCHAR(MAX),

    -- Status Tracking
    [Status] VARCHAR(20) DEFAULT 'Pending', -- Pending, Sent, Executing, Completed, Failed, Cancelled
    [Priority] INT DEFAULT 0, -- 0=Normal, 1=High, 2=Critical

-- Timing
    [CreatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [ScheduledFor] DATETIME2,
    [SentAt] DATETIME2,
    [StartedAt] DATETIME2,
    [CompletedAt] DATETIME2,

    -- Execution Results
    [Result] VARCHAR(MAX), -- JSON result
    [ErrorMessage] VARCHAR(1000),
    [ErrorStackTrace] VARCHAR(MAX),

    -- Retry Tracking
    [RetryCount] INT DEFAULT 0,
    [MaxRetries] INT DEFAULT 3,
    [NextRetryAt] DATETIME2,

    -- Foreign Keys
    FOREIGN KEY ([AgentId]) REFERENCES [Agents]([Id]),
    FOREIGN KEY ([ApplicationId]) REFERENCES [Applications]([Id]),

    -- Constraints
    CONSTRAINT [CK_CommandQueue_CommandType]
    CHECK ([CommandType] IN ('SPAWN', 'KILL', 'RESTART', 'UPDATE', 'STOP_ALL')),
    CONSTRAINT [CK_CommandQueue_Status]
    CHECK ([Status] IN ('Pending', 'Sent', 'Executing', 'Completed', 'Failed', 'Cancelled'))
    );


-- =============================================
-- 6. HEARTBEATS TABLE (Health Monitoring)
-- =============================================
CREATE TABLE [Heartbeats] (
    [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,

    -- Source Identification
    [AgentId] VARCHAR(50),
    [InstanceId] VARCHAR(100),
    [ApplicationId] VARCHAR(50),

    -- Heartbeat Data
    [HeartbeatType] VARCHAR(20) NOT NULL, -- Agent, Instance, Custom
    [IsHealthy] BIT DEFAULT 1,
    [Metrics] VARCHAR(MAX), -- JSON metrics

-- Response Data
    [ResponseTimeMs] INT,
    [StatusCode] INT,
    [StatusMessage] VARCHAR(500),

    -- Timing
    [Timestamp] DATETIME2 DEFAULT GETUTCDATE(),
    [ReceivedAt] DATETIME2 DEFAULT GETUTCDATE(),

    -- Foreign Keys
    FOREIGN KEY ([AgentId]) REFERENCES [Agents]([Id]),
    FOREIGN KEY ([InstanceId]) REFERENCES [ApplicationInstances]([InstanceId]),
    FOREIGN KEY ([ApplicationId]) REFERENCES [Applications]([Id]),

    -- Constraints
    CONSTRAINT [CK_Heartbeats_HeartbeatType]
    CHECK ([HeartbeatType] IN ('Agent', 'Instance', 'Custom'))
    );

-- =============================================
-- 7. METRICS HISTORY TABLE (Performance Data)
-- =============================================
CREATE TABLE [MetricsHistory] (
    [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,

    -- Source Identification
    [AgentId] VARCHAR(50),
    [InstanceId] VARCHAR(100),

    -- System Metrics
    [CpuPercent] DECIMAL(5,2),
    [MemoryMB] DECIMAL(10,2),
    [MemoryPercent] DECIMAL(5,2),
    [DiskUsagePercent] DECIMAL(5,2),
    [NetworkBytesSent] BIGINT,
    [NetworkBytesReceived] BIGINT,

    -- Application Metrics
    [ThreadCount] INT,
    [HandleCount] INT,
    [IoReadBytes] BIGINT,
    [IoWriteBytes] BIGINT,
    [PrivateBytes] BIGINT,
    [WorkingSet] BIGINT,

    -- Process Metrics
    [UpTimeSeconds] INT,
    [UserProcessorTime] INT,
    [PrivilegedProcessorTime] INT,

    -- Collection Metadata
    [CollectionInterval] INT DEFAULT 10, -- Seconds
    [Timestamp] DATETIME2 DEFAULT GETUTCDATE(),

    -- Foreign Keys
    FOREIGN KEY ([AgentId]) REFERENCES [Agents]([Id]),
    FOREIGN KEY ([InstanceId]) REFERENCES [ApplicationInstances]([InstanceId])
    );


-- =============================================
-- 8. EVENTS LOG TABLE (Audit Trail)
-- =============================================
CREATE TABLE [EventsLog] (
    [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,

    -- Event Details
    [EventType] VARCHAR(50) NOT NULL,
    [EventLevel] VARCHAR(20) DEFAULT 'Information', -- Debug, Information, Warning, Error, Critical
    [EventSource] VARCHAR(100),

    -- Entity References
    [AgentId] VARCHAR(50),
    [ApplicationId] VARCHAR(50),
    [InstanceId] VARCHAR(100),
    [CommandId] VARCHAR(50),

    -- Event Data
    [Message] VARCHAR(1000) NOT NULL,
    [Details] VARCHAR(MAX), -- JSON details
    [CorrelationId] VARCHAR(50),

    -- User Context
    [UserId] VARCHAR(100),
    [UserName] VARCHAR(100),
    [UserIp] VARCHAR(45),

    -- Timing
    [Timestamp] DATETIME2 DEFAULT GETUTCDATE(),

    -- Foreign Keys
    FOREIGN KEY ([AgentId]) REFERENCES [Agents]([Id]),
    FOREIGN KEY ([ApplicationId]) REFERENCES [Applications]([Id]),
    FOREIGN KEY ([InstanceId]) REFERENCES [ApplicationInstances]([InstanceId]),

    -- Constraints
    CONSTRAINT [CK_EventsLog_EventLevel]
    CHECK ([EventLevel] IN ('Debug', 'Information', 'Warning', 'Error', 'Critical'))
    );


-- =============================================
-- 9. CONFIGURATION TABLE (System Settings)
-- =============================================
CREATE TABLE [Configuration] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,

    -- Configuration Details
    [Category] VARCHAR(50) NOT NULL, -- System, Agent, Application, Scaling, Network
    [Key] VARCHAR(100) NOT NULL,
    [Value] VARCHAR(MAX),
    [DataType] VARCHAR(20) DEFAULT 'String', -- String, Int, Bool, Decimal, JSON

-- Versioning
    [Version] INT DEFAULT 1,
    [IsActive] BIT DEFAULT 1,

    -- Metadata
    [Description] VARCHAR(500),
    [CreatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    [CreatedBy] VARCHAR(100),
    [UpdatedBy] VARCHAR(100),

    -- Constraints
    CONSTRAINT [UQ_Configuration_Category_Key] UNIQUE ([Category], [Key]),
    CONSTRAINT [CK_Configuration_DataType]
    CHECK ([DataType] IN ('String', 'Int', 'Bool', 'Decimal', 'JSON'))
    );


-- =============================================
-- 10. SCALING HISTORY TABLE (Auto-scaling Events)
-- =============================================
CREATE TABLE [ScalingHistory] (
    [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,

    -- Scaling Details
    [ApplicationId] VARCHAR(50) NOT NULL,
    [ScalingType] VARCHAR(20) NOT NULL, -- ScaleUp, ScaleDown, Maintain
    [Reason] VARCHAR(500),

    -- Instance Counts
    [PreviousInstances] INT NOT NULL,
    [TargetInstances] INT NOT NULL,
    [ActualInstances] INT,

    -- Trigger Information
    [TriggerSource] VARCHAR(50), -- Manual, CPU, Memory, Schedule, Health
    [TriggerValue] VARCHAR(100),

    -- Metrics at Time of Scaling
    [AvgCpuPercent] DECIMAL(5,2),
    [AvgMemoryPercent] DECIMAL(5,2),
    [HealthyInstances] INT,
    [UnhealthyInstances] INT,

    -- Timing
    [TriggeredAt] DATETIME2 DEFAULT GETUTCDATE(),
    [CompletedAt] DATETIME2,

    -- Result
    [Success] BIT,
    [ErrorMessage] VARCHAR(1000),

    -- Foreign Key
    FOREIGN KEY ([ApplicationId]) REFERENCES [Applications]([Id]),

    -- Constraints
    CONSTRAINT [CK_ScalingHistory_ScalingType]
    CHECK ([ScalingType] IN ('ScaleUp', 'ScaleDown', 'Maintain'))
    );

-- =============================================
-- INDEXES FOR PERFORMANCE
-- =============================================

-- Applications indexes
CREATE INDEX [IX_Applications_Status] ON [Applications] ([Status]);
CREATE INDEX [IX_Applications_AutoStart] ON [Applications] ([AutoStart]);
CREATE INDEX [IX_Applications_UpdatedAt] ON [Applications] ([UpdatedAt]);


-- Agents indexes
CREATE INDEX [IX_Agents_Status_LastHeartbeat] ON [Agents] ([Status], [LastHeartbeat]);
CREATE INDEX [IX_Agents_IpAddress] ON [Agents] ([IpAddress]);
CREATE INDEX [IX_Agents_RegisteredAt] ON [Agents] ([RegisteredAt]);


-- ApplicationInstances indexes
CREATE INDEX [IX_ApplicationInstances_ApplicationId] ON [ApplicationInstances] ([ApplicationId]);
CREATE INDEX [IX_ApplicationInstances_AgentId] ON [ApplicationInstances] ([AgentId]);
CREATE INDEX [IX_ApplicationInstances_Status] ON [ApplicationInstances] ([Status]);
CREATE INDEX [IX_ApplicationInstances_LastHeartbeat] ON [ApplicationInstances] ([LastHeartbeat]);
CREATE INDEX [IX_ApplicationInstances_StartedAt] ON [ApplicationInstances] ([StartedAt]);
CREATE INDEX [IX_ApplicationInstances_AgentId_Status] ON [ApplicationInstances] ([AgentId], [Status]);


-- CommandQueue indexes
CREATE INDEX [IX_CommandQueue_AgentId_Status] ON [CommandQueue] ([AgentId], [Status]);
CREATE INDEX [IX_CommandQueue_Status_CreatedAt] ON [CommandQueue] ([Status], [CreatedAt]);
CREATE INDEX [IX_CommandQueue_ScheduledFor] ON [CommandQueue] ([ScheduledFor]);
CREATE INDEX [IX_CommandQueue_CommandId] ON [CommandQueue] ([CommandId]);
CREATE INDEX [IX_CommandQueue_InstanceId] ON [CommandQueue] ([InstanceId]);


-- Heartbeats indexes
CREATE INDEX [IX_Heartbeats_AgentId_Timestamp] ON [Heartbeats] ([AgentId], [Timestamp]);
CREATE INDEX [IX_Heartbeats_InstanceId_Timestamp] ON [Heartbeats] ([InstanceId], [Timestamp]);
CREATE INDEX [IX_Heartbeats_Timestamp] ON [Heartbeats] ([Timestamp]);

-- MetricsHistory indexes
CREATE INDEX [IX_MetricsHistory_InstanceId_Timestamp] ON [MetricsHistory] ([InstanceId], [Timestamp]);
CREATE INDEX [IX_MetricsHistory_AgentId_Timestamp] ON [MetricsHistory] ([AgentId], [Timestamp]);
CREATE INDEX [IX_MetricsHistory_Timestamp] ON [MetricsHistory] ([Timestamp]);


-- EventsLog indexes
CREATE INDEX [IX_EventsLog_Timestamp] ON [EventsLog] ([Timestamp]);
CREATE INDEX [IX_EventsLog_EventType] ON [EventsLog] ([EventType]);
CREATE INDEX [IX_EventsLog_EventLevel] ON [EventsLog] ([EventLevel]);
CREATE INDEX [IX_EventsLog_AgentId] ON [EventsLog] ([AgentId]);
CREATE INDEX [IX_EventsLog_CorrelationId] ON [EventsLog] ([CorrelationId]);


-- ScalingHistory indexes
CREATE INDEX [IX_ScalingHistory_ApplicationId] ON [ScalingHistory] ([ApplicationId]);
CREATE INDEX [IX_ScalingHistory_TriggeredAt] ON [ScalingHistory] ([TriggeredAt]);


-- =============================================
-- INITIAL DATA (Configuration)
-- =============================================

-- Insert default configuration
INSERT INTO [Configuration] ([Category], [Key], [Value], [DataType], [Description])
VALUES
-- System Configuration
    ('System', 'Watchdog.Version', '1.0.0', 'String', 'Watchdog system version'),
    ('System', 'Cleanup.RetentionDays', '30', 'Int', 'Days to keep historical data'),
    ('System', 'Cleanup.IntervalHours', '24', 'Int', 'Hours between cleanup runs'),

-- Agent Configuration
    ('Agent', 'Heartbeat.IntervalSeconds', '30', 'Int', 'Seconds between agent heartbeats'),
    ('Agent', 'Heartbeat.TimeoutMinutes', '5', 'Int', 'Minutes before marking agent offline'),
    ('Agent', 'Registration.AutoApprove', 'true', 'Bool', 'Auto-approve new agent registrations'),
    ('Agent', 'Port.RangeStart', '30000', 'Int', 'Start of dynamic port range'),
    ('Agent', 'Port.RangeEnd', '40000', 'Int', 'End of dynamic port range'),

-- Scaling Configuration
    ('Scaling', 'Check.IntervalSeconds', '60', 'Int', 'Seconds between scaling checks'),
    ('Scaling', 'Cpu.ThresholdPercent', '80', 'Int', 'CPU threshold for scaling'),
    ('Scaling', 'Memory.ThresholdPercent', '80', 'Int', 'Memory threshold for scaling'),
    ('Scaling', 'HealthCheck.FailureThreshold', '3', 'Int', 'Failed health checks before scaling'),
    ('Scaling', 'Cooldown.PeriodSeconds', '300', 'Int', 'Seconds to wait between scaling actions')

