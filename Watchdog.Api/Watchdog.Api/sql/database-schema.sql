-- =============================================
-- 1. AGENTS TABLE (Worker Nodes)
-- =============================================
CREATE TABLE agent (
    id VARCHAR(50) NOT NULL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    display_name VARCHAR(200),

    ip_address VARCHAR(45) NOT NULL,
    hostname VARCHAR(255),

    total_memory_mb BIGINT,
    available_memory_mb BIGINT,
    cpu_cores INT,
    cpu_model VARCHAR(100),
    total_disk_gb BIGINT,
    available_disk_gb BIGINT,
    os_version VARCHAR(100),
    dot_net_version VARCHAR(50),

    status VARCHAR(20) DEFAULT 'offline',
    last_heartbeat DATETIME2,

    max_concurrent_processes INT DEFAULT 100,
    current_process_count INT DEFAULT 0,
    total_spawned_processes INT DEFAULT 0,
    total_failed_spawns INT DEFAULT 0,
    tags VARCHAR(MAX) DEFAULT '[]',

    created DATETIME2 DEFAULT GETUTCDATE(),
    updated DATETIME2 NULL,
    created_by BIGINT NULL,
    updated_by BIGINT NULL,

    CONSTRAINT ck_agents_status
        CHECK (status IN ('offline', 'online', 'draining', 'maintenance'))
);


-- =============================================
-- 2. APPLICATIONS TABLE (Desired State)
-- =============================================
CREATE TABLE application (
    id VARCHAR(50) NOT NULL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    display_name VARCHAR(200),
    description VARCHAR(500),
    
    executable_path VARCHAR(500) NOT NULL,
    arguments VARCHAR(1000),
    working_directory VARCHAR(500),
    application_type INT NOT NULL DEFAULT 0,
    
    health_check_interval INT DEFAULT 30,
    startup_timeout INT DEFAULT 180,
    heartbeat_timeout INT DEFAULT 120,
    
    desired_instances INT DEFAULT 1,
    min_instances INT DEFAULT 1,
    max_instances INT DEFAULT 2,
    
    environment_variables VARCHAR(MAX) DEFAULT '{}',
    built_in_port INT NULL,

    auto_start BIT DEFAULT 1,
    max_restart_attempts INT DEFAULT 3,
    restart_delay_seconds INT DEFAULT 10,
    stop_timeout_seconds INT DEFAULT 30,

    created DATETIME2 DEFAULT GETUTCDATE(),
    updated DATETIME2 NULL,
    created_by BIGINT NULL,
    updated_by BIGINT NULL,

    status INT DEFAULT 0,

    CONSTRAINT ck_applications_desired_instances CHECK (desired_instances >= 0),
    CONSTRAINT ck_applications_min_instances CHECK (min_instances >= 0),
    CONSTRAINT ck_applications_max_instances CHECK (max_instances >= min_instances),
    CONSTRAINT ck_applications_health_check_interval CHECK (health_check_interval BETWEEN 5 AND 300)
);

-- =============================================
-- 3. APPLICATION INSTANCES TABLE (Actual State)
-- =============================================
CREATE TABLE application_instance (
    instance_id VARCHAR(100) NOT NULL PRIMARY KEY,
    application_id VARCHAR(50) NOT NULL,
    agent_id VARCHAR(50) NULL,
    
    process_id INT,
    process_name VARCHAR(255),
    command_line VARCHAR(1000),
    
    status VARCHAR(20) DEFAULT 'pending',
    exit_code INT,
    exit_reason VARCHAR(500),
    
    cpu_percent DECIMAL(5,2),
    memory_mb DECIMAL(10,2),
    memory_percent DECIMAL(5,2),
    thread_count INT,
    handle_count INT,
    
    assigned_port INT,
    
    started_at DATETIME2,
    stopped_at DATETIME2,
    last_heartbeat DATETIME2,
    last_health_check DATETIME2,
    
    is_ready BIT DEFAULT 0,
    ready_at DATETIME2,
    
    restart_count INT DEFAULT 0,
    last_restart_attempt DATETIME2,
    auto_restart_enabled BIT DEFAULT 1,
    
    last_error VARCHAR(1000),
    error_count INT DEFAULT 0,
    
    created_at DATETIME2 DEFAULT GETUTCDATE(),
    updated_at DATETIME2 DEFAULT GETUTCDATE(),
    
    CONSTRAINT fk_app_instances_application
       FOREIGN KEY (application_id) REFERENCES application(id),
    CONSTRAINT fk_app_instances_agent
       FOREIGN KEY (agent_id) REFERENCES agent(id),
    
    CONSTRAINT ck_app_instances_status
       CHECK (status IN ('pending','starting','running','stopping','stopped','warning','error'))
);


-- =============================================
-- 4. AGENT APPLICATIONS TABLE (Assignment)
-- =============================================
CREATE TABLE agent_application (
    agent_id VARCHAR(50) NOT NULL,
    application_id VARCHAR(50) NOT NULL,
    
    priority INT DEFAULT 0,

    assigned_at DATETIME2 DEFAULT GETUTCDATE(),
    assigned_by VARCHAR(100),
    last_assigned_instance DATETIME2,
    
    PRIMARY KEY (agent_id, application_id),
    
    CONSTRAINT fk_agent_apps_agent
        FOREIGN KEY (agent_id) REFERENCES agent(id),
    CONSTRAINT fk_agent_apps_application
        FOREIGN KEY (application_id) REFERENCES application(id)
);
-- =============================================
-- 5. HEARTBEATS TABLE (Health Monitoring)
-- =============================================
CREATE TABLE heartbeat (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,

    agent_id VARCHAR(50),
    instance_id VARCHAR(100),
    application_id VARCHAR(50),

    heartbeat_type VARCHAR(20) NOT NULL,
    is_healthy BIT DEFAULT 1,
    metrics VARCHAR(MAX),

    response_time_ms INT,
    status_code INT,
    status_message VARCHAR(500),

    timestamp DATETIME2 DEFAULT GETUTCDATE(),
    received_at DATETIME2 DEFAULT GETUTCDATE(),

    CONSTRAINT fk_heartbeat_agent FOREIGN KEY (agent_id) REFERENCES agent(id),
    CONSTRAINT fk_heartbeat_instance FOREIGN KEY (instance_id) REFERENCES application_instance(instance_id),
    CONSTRAINT fk_heartbeat_application FOREIGN KEY (application_id) REFERENCES application(id),

    CONSTRAINT ck_heartbeat_type
        CHECK (heartbeat_type IN ('agent','instance','custom'))
);


-- =============================================
-- 6. COMMAND QUEUE
-- =============================================
CREATE TABLE command_queue (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    command_id VARCHAR(50) NOT NULL UNIQUE,
    
    command_type VARCHAR(20) NOT NULL,
    agent_id VARCHAR(50) NOT NULL,
    application_id VARCHAR(50),
    instance_id VARCHAR(100),
    
    parameters VARCHAR(MAX),
    
    status VARCHAR(20) DEFAULT 'pending',
    priority INT DEFAULT 0,
    
    created_at DATETIME2 DEFAULT GETUTCDATE(),
    scheduled_for DATETIME2,
    sent_at DATETIME2,
    started_at DATETIME2,
    completed_at DATETIME2,
    
    result VARCHAR(MAX),
    error_message VARCHAR(1000),
    error_stack_trace VARCHAR(MAX),
    
    retry_count INT DEFAULT 0,
    max_retries INT DEFAULT 3,
    next_retry_at DATETIME2,
    
    CONSTRAINT fk_command_agent FOREIGN KEY (agent_id) REFERENCES agent(id),
    CONSTRAINT fk_command_application FOREIGN KEY (application_id) REFERENCES application(id),
    
    CONSTRAINT ck_command_type
    CHECK (command_type IN ('spawn','kill','restart','stop_all')),
    CONSTRAINT ck_command_status
    CHECK (status IN ('pending','sent','executing','completed','failed','cancelled'))
);

-- =============================================
-- 7. METRICS SNAPSHOT TABLE (Parent)
-- =============================================
CREATE TABLE metrics (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    instance_id VARCHAR(100) NOT NULL,
    metric_type INT NOT NULL,           -- 1=Machine, 2=Queue, 3=Throughput, 4=Latency
    payload NVARCHAR(MAX) NULL,         -- raw JSON payload
    timestamp DATETIME2 DEFAULT GETUTCDATE(),
    server_name VARCHAR(255) NULL,
    payload_generate_datetime DATETIME2 NULL,

    CONSTRAINT fk_metrics_instance
        FOREIGN KEY (instance_id) REFERENCES application_instance(instance_id)
);

-- =============================================
-- 8. MACHINE METRICS SNAPSHOT
-- =============================================
CREATE TABLE machine_metrics (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id BIGINT NOT NULL,
    cpu_percent DECIMAL(5,2) NULL,
    memory_mb DECIMAL(10,2) NULL,
    memory_percent DECIMAL(5,2) NULL,
    disk_usage_percent DECIMAL(5,2) NULL,
    thread_count INT NULL,
    handle_count INT NULL,

    CONSTRAINT fk_machine_metrics
        FOREIGN KEY (metric_id) REFERENCES metrics(id)
);

-- =============================================
-- 9. QUEUE METRICS SNAPSHOT
-- =============================================
CREATE TABLE queue_metrics (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id BIGINT NOT NULL,
    queue_name VARCHAR(255) NULL,
    queue_length BIGINT NULL,
    queue_ready BIGINT NULL,
    queue_unacknowledged BIGINT NULL,
    incoming_per_sec FLOAT NULL,
    deliver_per_sec FLOAT NULL,
    ack_per_sec FLOAT NULL,
    consumer_count INT NULL,

    CONSTRAINT fk_queue_metrics
        FOREIGN KEY (metric_id) REFERENCES metrics(id)
);

-- =============================================
-- 10. LATENCY METRICS SNAPSHOT
-- =============================================
CREATE TABLE latency_metrics (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id BIGINT NOT NULL,
    message_latency_ms FLOAT NULL,
    ack_latency_ms FLOAT NULL,
    end_to_end_latency_ms FLOAT NULL,
    p50_latency_ms FLOAT NULL,
    p95_latency_ms FLOAT NULL,
    p99_latency_ms FLOAT NULL,
    max_latency_ms FLOAT NULL,
    min_latency_ms FLOAT NULL,

    CONSTRAINT fk_latency_metrics
        FOREIGN KEY (metric_id) REFERENCES metrics(id)
);

-- =============================================
-- 11. THROUGHPUT METRICS SNAPSHOT
-- =============================================
CREATE TABLE throughput_metrics (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id BIGINT NOT NULL,
    messages_published_per_sec FLOAT NULL,
    messages_consumed_per_sec FLOAT NULL,
    messages_acked_per_sec FLOAT NULL,
    redelivered_per_sec FLOAT NULL,
    returned_unroutable_per_sec FLOAT NULL,
    global_queue_ready INT NULL,
    global_queue_unacknowledged INT NULL,
    global_queue_total INT NULL,

    CONSTRAINT fk_throughput_metrics
        FOREIGN KEY (metric_id) REFERENCES metrics(id)
);


-- =============================================
-- INDEXES FOR PERFORMANCE
-- =============================================

-- Applications indexes
CREATE INDEX ix_applications_status ON application (status);
CREATE INDEX ix_applications_auto_start ON application (auto_start);
CREATE INDEX ix_applications_updated ON application (updated);

-- Agents indexes
CREATE INDEX ix_agents_status_last_heartbeat ON agent (status, last_heartbeat);
CREATE INDEX ix_agents_ip_address ON agent (ip_address);
CREATE INDEX ix_agents_created ON agent (created);

-- ApplicationInstances indexes
CREATE INDEX ix_application_instances_application_id ON application_instance (application_id);
CREATE INDEX ix_application_instances_agent_id ON application_instance (agent_id);
CREATE INDEX ix_application_instances_status ON application_instance (status);
CREATE INDEX ix_application_instances_last_heartbeat ON application_instance (last_heartbeat);
CREATE INDEX ix_application_instances_agent_status ON application_instance (agent_id, status);

-- Heartbeats indexes
CREATE INDEX ix_heartbeats_agent_timestamp ON heartbeat (agent_id, timestamp);
CREATE INDEX ix_heartbeats_instance_timestamp ON heartbeat (instance_id, timestamp);
CREATE INDEX ix_heartbeats_timestamp ON heartbeat (timestamp);

-- Command Queue indexes
CREATE INDEX ix_command_queue_agent_status ON command_queue (agent_id, status);
CREATE INDEX ix_command_queue_status_created_at ON command_queue (status, created_at);
CREATE INDEX ix_command_queue_scheduled_for ON command_queue (scheduled_for);
CREATE INDEX ix_command_queue_command_id ON command_queue (command_id);
CREATE INDEX ix_command_queue_instance_id ON command_queue (instance_id);

-- Metrics Snapshot indexes
CREATE INDEX ix_metrics_snapshot_instance_timestamp ON metrics(instance_id, timestamp);
CREATE INDEX ix_metrics_snapshot_metric_type ON metrics (metric_type);
CREATE INDEX ix_metrics_snapshot_timestamp ON metrics (timestamp);

-- Child metrics indexes
CREATE INDEX ix_machine_metrics_metric_id ON machine_metrics (metric_id);
CREATE INDEX ix_queue_metrics_metric_id ON queue_metrics (metric_id);
CREATE INDEX ix_queue_metrics_queue_name ON queue_metrics (queue_name) WHERE queue_name IS NOT NULL;
CREATE INDEX ix_latency_metrics_metric_id ON latency_metrics (metric_id);
CREATE INDEX ix_throughput_metrics_metric_id ON throughput_metrics (metric_id);
