# Watchdog System — Component Specification

> **Version:** 1.0  
> **Date:** 2026-02-23  
> **Target Framework:** .NET 9.0  
> **Database:** SQL Server (MSSQL)

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Architecture Diagram](#2-architecture-diagram)
3. [Watchdog.Api — Control Plane](#3-watchdogapi--control-plane)
4. [Watchdog.Agent — Worker Node](#4-watchdogagent--worker-node)
5. [PEPMonitoring — Client SDK](#5-pepmonitoring--client-sdk)
6. [TestConsole — Reference Client](#6-testconsole--reference-client)
7. [Watchdog.Verification — Integration Test Harness](#7-watchdogverification--integration-test-harness)
8. [Database Schema](#8-database-schema)
9. [Communication Protocols](#9-communication-protocols)
10. [Data Flow Scenarios](#10-data-flow-scenarios)

---

## 1. System Overview

The **Watchdog** system is a distributed application orchestration platform that manages the lifecycle of applications across multiple machines (agents). It provides:

- **Centralized Control Plane** (`Watchdog.Api`) — manages desired state, issues commands, monitors health.
- **Distributed Agents** (`Watchdog.Agent`) — run on each worker machine, spawn/kill processes, report status.
- **Client SDK** (`PEPMonitoring`) — a NuGet library that applications embed to register themselves, send heartbeats, and report metrics directly to the Control Plane.
- **Testing Utilities** (`TestConsole`, `Watchdog.Verification`) — for development, integration testing, and validating end-to-end flows.

### Key Design Principles

| Principle | Description |
|---|---|
| **Desired-State Reconciliation** | The system continuously compares desired instance counts against actual running instances and scales up/down automatically. |
| **Dual Communication** | Agents communicate via **gRPC** (real-time streaming + RPC) and **REST/HTTP** (management API). Applications communicate via gRPC through the PEPMonitoring SDK. |
| **Orphan Recovery** | Instances that lose their agent assignment are automatically detected and re-claimed or stopped. |
| **Heartbeat-Based Health** | Both agents and application instances send periodic heartbeats. Missed heartbeats trigger escalating status changes (`running` → `warning` → `error`). |

---

## 2. Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                        Watchdog.Api                              │
│                     (Control Plane)                              │
│                                                                  │
│  ┌─────────────┐  ┌──────────────────┐  ┌─────────────────────┐ │
│  │  REST API   │  │  gRPC Services   │  │ Background Services │ │
│  │  :5143      │  │  :5144           │  │                     │ │
│  │             │  │                  │  │ • ScalingBgSvc      │ │
│  │ • Agents    │  │ • AgentService   │  │ • CommandDispatcher │ │
│  │ • Apps      │  │ • AppMonitoring  │  │ • HeartbeatMonitor  │ │
│  │             │  │                  │  │ • gRPC Cleanup      │ │
│  └──────┬──────┘  └────────┬─────────┘  └─────────────────────┘ │
│         │                  │                                     │
│         └────────┬─────────┘                                     │
│                  │                                               │
│         ┌────────▼────────┐                                      │
│         │  SQL Server DB  │                                      │
│         │  (WatchdogDB)   │                                      │
│         └─────────────────┘                                      │
└──────────────────────────────────────────────────────────────────┘
         ▲ REST/HTTP           ▲ gRPC                ▲ gRPC
         │                     │                     │
   ┌─────┴─────┐        ┌─────┴──────┐       ┌──────┴───────┐
   │ Dashboard  │        │  Watchdog  │       │ Application  │
   │ / Admin    │        │  Agent     │       │ (with        │
   │ Tools      │        │  (Worker)  │       │ PEPMonitoring│
   └────────────┘        └────────────┘       │ SDK)         │
                               │              └──────────────┘
                         ┌─────▼─────┐
                         │ Managed   │
                         │ Processes │
                         └───────────┘
```

---

## 3. Watchdog.Api — Control Plane

**Project:** `Watchdog.Api/Watchdog.Api`  
**Type:** ASP.NET Core Web API + gRPC Server  
**Ports:** `5143` (HTTP/1 REST), `5144` (HTTP/2 gRPC)

The Control Plane is the central brain of the system. It persists desired state in SQL Server, receives status reports from agents and applications, and issues commands to achieve the desired state.

### 3.1 REST API Controllers

#### `AgentsController` — `api/agents`

Manages agent (worker node) registrations and assignments.

| Endpoint | Method | Description |
|---|---|---|
| `api/agents` | GET | List all registered agents |
| `api/agents/{id}` | GET | Get a specific agent by ID |
| `api/agents/{id}/heartbeat` | POST | Record an agent heartbeat |
| `api/agents/{agentId}/applications/{applicationId}/assign` | POST | Assign an application to an agent |
| `api/agents/{id}/applications` | GET | List applications assigned to an agent |

#### `ApplicationsController` — `api/applications`

Full CRUD for application definitions and lifecycle operations.

| Endpoint | Method | Description |
|---|---|---|
| `api/applications` | GET | List all applications |
| `api/applications/{id}` | GET | Get a specific application |
| `api/applications` | POST | Create a new application definition |
| `api/applications/{id}` | PUT | Update an application definition |
| `api/applications/{id}` | DELETE | Delete an application (stops all instances first) |
| `api/applications/{id}/instances` | GET | Get all instances (running/stopped/error) |
| `api/applications/{id}/start` | POST | Start the application |
| `api/applications/{id}/stop` | POST | Stop all running instances |
| `api/applications/{id}/restart` | POST | Restart the application (stop → wait → start) |

#### Health Check

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Returns `{ status: "healthy", timestamp: ... }` |

### 3.2 gRPC Services

#### `AgentService` (proto: `monitoring.proto`)

Handles agent-to-control-plane communication.

| RPC | Type | Description |
|---|---|---|
| `RegisterAgent` | Unary | Agent registers with the control plane. Returns assigned applications and active instances. Also claims orphan instances. |
| `ReportStatus` | Unary | Agent reports application statuses and system metrics. Returns pending commands. |
| `CommandStream` | Bidirectional Streaming | Real-time communication channel. Agent sends heartbeats, spawn/stop confirmations, errors. Control plane pushes spawn/kill/restart/update commands. |
| `ExecuteCommand` | Unary | Direct command execution (not currently used in main flow). |

**Key behaviors in `RegisterAgent`:**
- Creates or updates the agent record via MERGE (idempotent).
- Retrieves assigned applications from `agent_application` table.
- Detects **orphan instances** (instances with `agent_id IS NULL`) for the assigned applications and claims them by generating a new instance ID in the format `{hostname}-{agentName}-{guid}`.
- Returns active instances for the agent.

**Key behaviors in `ReportStatus`:**
- Updates agent heartbeat.
- Processes each `ApplicationStatus` entry, updating instance records in the DB.
- Handles **adopted instances** by cleaning up duplicate orphan records.
- Returns pending commands from the `command_queue` table, marking them as `sent`.

**Key behaviors in `CommandStream`:**
- Identifies the agent from the first heartbeat message.
- Registers/unregisters agent gRPC stream connections for real-time command delivery.
- Processes incoming messages: `Heartbeat`, `ApplicationSpawned`, `ApplicationStopped`, `ErrorReport`.
- Records instance lifecycle events in the database.

#### `ApplicationMonitoringService` (proto: `application_monitoring.proto`)

Handles application-to-control-plane communication (used by the PEPMonitoring SDK).

| RPC | Type | Description |
|---|---|---|
| `RegisterApplication` | Unary | Application self-registers. Creates/updates the `application` and `application_instance` records. |
| `Ready` | Unary | Application signals it is ready to serve. Updates `is_ready = 1`, status to `running`, records PID and port. |
| `RecordHeartbeat` | Unary | Application sends a periodic heartbeat. Updates last heartbeat timestamp, inserts into `heartbeat` table. |
| `RecordPerformanceMetric` | Unary | Application reports a performance metric. Stored in `metrics_history` table. |

### 3.3 Core Services

#### `AgentManager`

Manages agent lifecycle in the database.

| Method | Description |
|---|---|
| `GetAgents()` | Returns all agents ordered by status and name. |
| `GetAgent(id)` | Returns a single agent. |
| `RegisterAgent(registration)` | MERGE (upsert) — creates or updates agent record. Sets status to `online`. |
| `UpdateAgentHeartbeat(agentId)` | Updates `last_heartbeat` and status to `online`. Triggers `MarkOfflineAgents` to set agents with heartbeats older than 5 minutes to `offline`. |
| `AssignApplicationToAgent(agentId, applicationId)` | Creates an `agent_application` assignment (idempotent). |
| `GetOnlineAgents()` | Returns agents that are `online` and have heartbeats within the last 5 minutes, ordered by available memory (descending). |
| `GetAgentApplications(agentId)` | Returns applications assigned to a specific agent via the `agent_application` join table. |

#### `ApplicationManager`

Business logic for application lifecycle.

| Method | Description |
|---|---|
| `CreateApplication(request)` | Validates, persists, and optionally auto-starts the application. |
| `UpdateApplication(id, request)` | Updates application configuration. |
| `DeleteApplication(id)` | Stops all instances, then deletes. |
| `StartApplication(id)` | Logging placeholder (actual spawning is handled by the ScalingEngine). |
| `StopApplication(id)` | Queues KILL commands for all running instances. Orphan instances (no agent) are directly marked `stopped`. |
| `RestartApplication(id)` | Stop → 5-second delay → Start. |
| `GetApplicationInstances(id)` | Returns all instances for an application. |
| `UpdateInstanceStatus(instanceId, ...)` | Updates instance status, CPU, memory, and PID. |

**Validation rules:**
- `ExecutablePath` cannot be empty or the placeholder value `"string"`.
- `Name` cannot be empty or `"string"`.

#### `ScalingEngine`

Continuously reconciles desired state with actual state.

| Method | Description |
|---|---|
| `CheckAndScaleApplications()` | Iterates all applications. For each valid application, compares running instances vs. `min_instances`, `max_instances`, and `desired_instances`. Triggers scale up/down as needed. |
| `ScaleApplication(applicationId, desiredInstances)` | Sets desired instances in DB, then immediately scales up or down. |
| `ScaleUpApplication(application, count)` | Finds a suitable online agent (assigned to the app, ordered by available memory). Queues SPAWN commands. Instance ID format: `{appId}-{agentId}-{guid}`. |
| `ScaleDownApplication(instances, count)` | Selects oldest running instances. For orphans (no agent), directly marks as `stopped`. For normal instances, queues KILL commands. |

**Scaling validation:** Applications with invalid `ExecutablePath` or `Name` are skipped.

**Agent selection:** Only agents assigned to the application (`agent_application` table) that are `online` with heartbeats within the last 5 minutes are considered. Selected by highest available memory.

#### `CommandService`

Manages the command queue — the outbox pattern for delivering commands to agents.

| Method | Description |
|---|---|
| `QueueSpawnCommand(agentId, application, instanceId, instanceIndex)` | Creates a `spawn` command with serialized parameters (exe path, args, env vars, port). Attempts immediate delivery via gRPC. |
| `QueueKillCommand(agentId, applicationId, instanceId)` | Creates a `kill` command (force=true, timeout=30s). |
| `QueueRestartCommand(agentId, applicationId, instanceId)` | Creates a `restart` command (timeout=30s). |
| `GetPendingCommands(agentId)` | Returns all `pending` commands for an agent, ordered by creation time. |
| `MarkCommandAsSent(commandId)` | Updates status to `sent` with timestamp. |
| `MarkCommandAsExecuted(commandId, success, result, error)` | Updates status to `executed`/`failed`. On successful spawn, updates the instance record with PID and port. |
| `CleanupOldCommands()` | Deletes `executed`/`failed` commands older than 7 days. |

**Command delivery:** Commands are first persisted to the `command_queue` table, then the service attempts immediate delivery via the `AgentGrpcService`. If the agent is not connected, the command remains `pending` and is delivered on the next status report.

#### `AgentGrpcService`

In-memory registry of active agent gRPC streaming connections.

| Method | Description |
|---|---|
| `RegisterAgentConnection(agentId, stream, connectionId)` | Stores the server-side stream writer for an agent. |
| `UnregisterAgentConnection(agentId)` | Removes the connection. |
| `SendCommandToAgentAsync(agentId, request)` | Sends a `ControlPlaneMessage` to the agent's stream. Returns `false` if not connected. |
| `CleanupStaleConnectionsAsync()` | Removes connections with `LastSeen` older than 10 minutes. |
| `ProcessAgentMessageAsync(agentId, message)` | Updates `LastSeen` timestamp on incoming messages. |

**Message conversion:** Converts `CommandRequest` to `ControlPlaneMessage` by deserializing the JSON `Parameters` field into the appropriate proto message (`SpawnCommand`, `KillCommand`, `RestartCommand`).

### 3.4 Background Services

| Service | Interval | Description |
|---|---|---|
| `ScalingBackgroundService` | 60 seconds | Calls `ScalingEngine.CheckAndScaleApplications()` to reconcile desired vs. actual instance counts. |
| `CommandDispatcherBackgroundService` | — | Currently in **streaming-only mode** (awaits indefinitely). Contains fallback logic for polling-based command dispatch. |
| `HeartbeatMonitorBackgroundService` | 10 seconds | Checks for stale instances (where `last_heartbeat < now - heartbeat_timeout × 2`). Marks as `warning` if overdue, or `error` if overdue by 3× the timeout. |
| `GrpcConnectionCleanupBackgroundService` | 5 minutes | Removes stale gRPC agent connections older than 10 minutes. |

### 3.5 Data Access Layer

#### `ApplicationRepository`

All database access uses **Dapper** with raw SQL against SQL Server. Key operations:

| Method | Description |
|---|---|
| `GetAll()` | Returns all applications with deserialized JSON environment variables. |
| `GetById(id)` | Returns a single application. |
| `Create(application)` | Inserts with `GETUTCDATE()` timestamps. |
| `Update(application)` | Updates all mutable fields. |
| `Delete(id)` | Hard delete from `application` table. |
| `GetApplicationInstances(applicationId)` | Returns instances sorted by `created_at DESC`. |
| `GetActiveInstancesForAgent(agentId)` | Returns `running` instances for an agent. |
| `GetStaleInstancesByHeartbeat()` | Returns instances in `running`/`starting`/`warning` with heartbeats older than `2 × heartbeat_timeout`. |
| `UpdateInstanceStatus(instanceId, ...)` | Updates status with optional CPU, memory, PID, agent ID. Uses `COALESCE` so nulls don't overwrite existing values. |
| `GetOrphanInstances(applicationIds)` | Returns instances where `agent_id IS NULL` for given applications. |
| `ClaimOrphanInstance(oldId, newId, agentId)` | Transactionally updates instance ID and agent ID. Cleans up related `heartbeat` and `metrics_history` rows. |

#### `IDbConnectionFactory`

Singleton that creates `SqlConnection` instances from the configured connection string.

---

## 4. Watchdog.Agent — Worker Node

**Project:** `Watchdog.Agent/Watchdog.Agent`  
**Type:** .NET Generic Host (Worker Service)  
**Can run as:** Console app or Windows Service (`UseWindowsService()`)

The Agent runs on each worker machine and is responsible for spawning, monitoring, and killing application processes as instructed by the Control Plane.

### 4.1 Configuration

Configuration is loaded from `appsettings.json` and bound to typed settings classes:

| Settings Class | Section | Key Properties |
|---|---|---|
| `AgentSettings` | `Agent` | `AgentId`, agent identity, tags |
| `ControlPlaneSettings` | `ControlPlane` | `BaseUrl` (REST), `GrpcEndpoint`, `HttpTimeoutSeconds` |
| `NetworkSettings` | `Network` | Port ranges, network configuration |
| `ProcessSettings` | `Process` | Process spawning defaults |
| `MonitoringSettings` | `Monitoring` | Monitoring intervals |

### 4.2 Core Services

#### `AgentWorker` (Hosted Service)

The main worker loop that orchestrates the agent lifecycle. Runs as a `BackgroundService`.

#### `GrpcClient`

Manages the gRPC connection to the Control Plane.

| Method | Description |
|---|---|
| `Connect()` | Establishes connection and calls `RegisterAgent`. Receives application assignments and active instances. |
| `Disconnect()` | Gracefully disconnects. |
| `Register()` | Sends `AgentRegistrationRequest` with system info (hostname, IP, memory, CPU, OS). |
| `ReportStatus()` | Sends `StatusReportRequest` with all application statuses and system metrics. Receives pending commands. |
| `SendHeartbeat()` | Sends heartbeat via the command stream. |
| `SendApplicationSpawned(...)` | Reports a newly spawned process (instance ID, app ID, PID, port, start time). |
| `SendApplicationStopped(...)` | Reports a stopped process (instance ID, exit code, reason). |
| `SendError(...)` | Reports an error (error type, message, stack trace). |
| `StartCommandStreaming()` | Opens a bidirectional stream. Starts concurrent tasks: `ReceiveCommands` (processes incoming `ControlPlaneMessage`) and `SendHeartbeats` (periodic heartbeat loop). |
| `ProcessSpawnCommand(cmd)` | Handles incoming spawn commands — calls `ProcessManager.SpawnProcess()`. |
| `ProcessKillCommand(cmd)` | Handles kill commands — calls `ProcessManager.KillProcess()`. |
| `ProcessRestartCommand(cmd)` | Handles restart commands — kills then re-spawns. |
| `AttemptReconnect()` | Reconnection logic with retry. |

#### `ProcessManager`

The core process lifecycle manager. Handles spawning, killing, monitoring, and reattaching processes.

| Method | Description |
|---|---|
| `SpawnProcess(...)` | Spawns a new process. Supports **Console apps** (direct `Process.Start`) and **Windows Services** (`sc.exe` create/start). Configures environment variables including `WATCHDOG_APP_ID`, `WATCHDOG_INSTANCE_ID`, `WATCHDOG_AGENT_ID`, `PORT`. Starts a monitoring task. |
| `KillProcess(instanceId, force, timeout)` | Gracefully or forcefully kills a process. For Windows Services, stops and optionally deletes the service. |
| `RestartProcess(instanceId, timeout)` | Kill → re-spawn with the same configuration. |
| `GetProcessInfo(instanceId)` | Returns process info (PID, CPU, memory, status). |
| `GetAllProcesses()` | Returns all managed processes. |
| `IsProcessRunning(instanceId)` | Checks if a managed process is alive. |
| `GetProcessMetrics(instanceId)` | Collects detailed metrics: CPU %, memory MB, thread count, handle count, I/O bytes. |
| `ReattachProcesses(instances)` | Re-discovers and re-attaches to existing processes after agent restart. Matches by executable path and PID. |
| `MonitorProcess(...)` | Background monitoring task per process. Detects exits and reports them. |

#### `ApplicationManager` (Agent-side)

Manages application configurations and instance state on the agent.

| Method | Description |
|---|---|
| `Initialize(assignments, instances)` | Loads application assignments from the Control Plane response. Syncs instance state. |
| `UpdateApplicationsFromControlPlane(assignments)` | Updates local application configs when new assignments arrive. |
| `DiscoverAndAdoptInstances(...)` | Discovers running processes that match known applications and "adopts" them. |
| `GetInstanceStatuses()` | Returns `ApplicationStatus` proto messages for all known instances. |
| `ShouldRestartInstance(...)` | Determines if an instance should be auto-restarted based on restart count and policy. |
| `NotifyInstanceStopped(...)` | Handles instance stop events — can trigger auto-restart. |
| `LoadSavedState()` / `SaveState()` | Persists/loads instance state to local JSON file for agent restart recovery. |

#### `MonitorService`

Periodic monitoring and health checking.

| Method | Description |
|---|---|
| `Start()` | Starts collection, health check, and failure detection loops. |
| `CollectMetrics()` | Collects process-level metrics (CPU, memory, threads) for each instance. |
| `CheckAllHealth()` | Performs health checks on instances (process alive, port responsive). |
| `DetectAndHandleFailures()` | Detects failed instances and triggers remediation (restart or status update). |
| `GetSystemMetrics()` | Collects system-wide metrics (CPU, memory, disk). Uses platform-specific APIs (Windows `GlobalMemoryStatusEx`, Linux `/proc`). |

#### `NetworkManager`

Port allocation and network management.

| Method | Description |
|---|---|
| Allocates available ports from configured ranges. |
| Tracks port assignments per instance. |
| Releases ports when instances stop. |

#### `CommandExecutor`

Executes shell commands and scripts on the worker machine.

#### `AgentHealthCheck`

ASP.NET health check implementation for monitoring agent health.

### 4.3 Communication Flow (Agent → Control Plane)

1. **Startup:** Agent calls `RegisterAgent` RPC → receives application assignments and active instances.
2. **Command Stream:** Opens bidirectional `CommandStream`. Sends heartbeats every N seconds. Receives spawn/kill/restart commands.
3. **Status Reports:** Periodically calls `ReportStatus` with all instance statuses and system metrics. Receives any pending commands not yet delivered via stream.
4. **Event Reporting:** On process spawn/stop/error, sends the corresponding message via the command stream.

---

## 5. PEPMonitoring — Client SDK

**Project:** `PEPMonitoring/PEPMonitoring` (referenced as a project reference)  
**Type:** .NET Class Library (NuGet-distributable)

The PEPMonitoring SDK is a lightweight library that applications embed to integrate with the Watchdog system. It communicates **directly with the Control Plane** (not via the Agent) using the `ApplicationMonitoringService` gRPC service on port `5144`.

### 5.1 Key Classes

#### `DefaultPEPMonitor` (Base Class)

Applications inherit from `DefaultPEPMonitor` and override `Configure()` to set up monitoring configuration.

| Method | Description |
|---|---|
| `Configure(IPEPMonitorConfig config)` | Sets the monitoring configuration (gRPC address, app ID, instance ID, etc.). |
| `ReadyAsync(processId, assignedPort)` | Signals to the Control Plane that the application is ready to serve. |
| `HeartbeatAsync()` | Sends a heartbeat to the Control Plane. Returns `true` on success. |
| `UnregisterAsync()` | Unregisters the application instance on shutdown. |

#### `MonitoringConfig` (implements `IPEPMonitorConfig`)

| Property | Description |
|---|---|
| `GrpcAddress` | Control Plane gRPC endpoint (default: `http://localhost:5144`). |
| `MUUID` | Service/Application ID (from `WATCHDOG_APP_ID` env var). |
| `InstanceId` | Instance ID (from `WATCHDOG_INSTANCE_ID` env var). |
| `ApplicationType` | Application type identifier (0 = Console). |

### 5.2 Communication Protocol

The SDK uses the `ApplicationMonitoringService` gRPC service:

1. **Registration → `RegisterApplication`** — called during initialization.
2. **Ready → `Ready`** — called after the application finishes startup, reporting PID and port.
3. **Heartbeat → `RecordHeartbeat`** — called periodically (configurable, default every 10 seconds).
4. **Metrics → `RecordPerformanceMetric`** — called to record performance data.

### 5.3 Environment Variables

Applications using PEPMonitoring read the following environment variables (set by the Agent during process spawn):

| Variable | Description |
|---|---|
| `PEP_MONITORING_GRPC_ADDRESS` | gRPC endpoint of the Control Plane (default: `http://localhost:5144`) |
| `WATCHDOG_APP_ID` | Application ID |
| `WATCHDOG_INSTANCE_ID` | Instance ID |
| `WATCHDOG_AGENT_ID` | Agent ID that spawned this instance |
| `PEP_MONITORING_HEARTBEAT_SECONDS` | Heartbeat interval in seconds (default: `10`) |
| `PORT` | Assigned port for the application |

---

## 6. TestConsole — Reference Client

**Project:** `TestConsole/TestConsole`  
**Type:** .NET Console Application  
**Purpose:** Reference implementation demonstrating how to use the PEPMonitoring SDK.

### 6.1 How It Works

1. **Startup:** Creates a `TestClass` instance (inherits from `DefaultPEPMonitor`).
2. **Configuration:** Reads environment variables (`PEP_MONITORING_GRPC_ADDRESS`, `WATCHDOG_APP_ID`, `WATCHDOG_INSTANCE_ID`, `WATCHDOG_AGENT_ID`) to configure the monitor.
3. **Ready Signal:** Calls `monitor.ReadyAsync(processId, assignedPort)` to signal readiness.
4. **Heartbeat Loop:** Enters a loop calling `monitor.HeartbeatAsync()` every 10 seconds.
5. **Graceful Shutdown:** On `Ctrl+C`, cancels the loop and calls `monitor.UnregisterAsync()`.

### 6.2 `TestClass`

```csharp
public class TestClass : DefaultPEPMonitor
{
    public TestClass()
    {
        // Reads config from environment variables
        var config = new MonitoringConfig
        {
            GrpcAddress = grpcAddress,   // from PEP_MONITORING_GRPC_ADDRESS
            MUUID = serviceId,           // from WATCHDOG_APP_ID
            InstanceId = instanceId,     // from WATCHDOG_INSTANCE_ID
            ApplicationType = 0
        };
        Configure(config);
    }
}
```

The TestConsole is the simplest possible monitored application, useful for:
- Validating the PEPMonitoring SDK.
- Testing the Control Plane's `ApplicationMonitoringService`.
- End-to-end deployment testing.

---

## 7. Watchdog.Verification — Integration Test Harness

**Project:** `Watchdog.Verification`  
**Type:** .NET Console Application  
**Purpose:** Automated integration tests that validate orphan instance handling and agent registration flows.

### 7.1 Test Scenario

The verification program executes against a running instance of `Watchdog.Api` and a SQL Server database:

1. **Cleanup** — Deletes any previous test data (`app-test-orphan`, `agent-test-verifier`).
2. **Setup** — Creates a test application, an orphan instance (with `agent_id = NULL`), a test agent, and an `agent_application` assignment.
3. **Execute** — Calls `RegisterAgent` via gRPC to trigger orphan claiming.
4. **Verify Response** — Checks that the response contains a claimed instance with the correct ID format (`{hostname}-{agentName}-{guid}`).
5. **Verify Database** — Queries the database to confirm the old orphan instance ID has been replaced and the new agent ID is assigned.

### 7.2 What It Validates

- The `RegisterAgent` RPC correctly detects orphan instances.
- Orphan instances are re-assigned to the registering agent.
- The instance ID is regenerated in the expected format.
- The database is updated correctly (old instance removed, new instance created with agent assignment).

---

## 8. Database Schema

**Database:** `WatchdogDB` (SQL Server)

### 8.1 Tables Overview

| Table | Description | Primary Key |
|---|---|---|
| `agent` | Worker node registrations | `id` (VARCHAR 50) |
| `application` | Application definitions (desired state) | `id` (VARCHAR 50) |
| `application_instance` | Running/stopped instances (actual state) | `instance_id` (VARCHAR 100) |
| `agent_application` | Agent ↔ Application assignment mapping | (`agent_id`, `application_id`) |
| `heartbeat` | Heartbeat history from agents and instances | `id` (BIGINT IDENTITY) |
| `command_queue` | Command outbox for agent communication | `id` (BIGINT IDENTITY), `command_id` (UNIQUE) |
| `metrics_history` | Performance metrics history | `id` (BIGINT IDENTITY) |

### 8.2 Key Table Details

#### `agent`

| Column | Type | Description |
|---|---|---|
| `id` | VARCHAR(50) | Agent identifier |
| `name` | VARCHAR(100) | Display name |
| `ip_address` | VARCHAR(45) | Agent IP address |
| `status` | VARCHAR(20) | `offline`, `online`, `draining`, `maintenance` |
| `total_memory_mb` | BIGINT | Total system memory |
| `available_memory_mb` | BIGINT | Current available memory |
| `cpu_cores` | INT | Number of CPU cores |
| `last_heartbeat` | DATETIME2 | Last heartbeat timestamp |
| `created` / `updated` | DATETIME2 | Audit timestamps |

#### `application`

| Column | Type | Description |
|---|---|---|
| `id` | VARCHAR(50) | Application identifier |
| `name` | VARCHAR(100) | Application name |
| `executable_path` | VARCHAR(500) | Path to the application executable |
| `arguments` | VARCHAR(1000) | Command-line arguments |
| `application_type` | INT | `0` = Console, `1` = Service, `2` = IIS |
| `desired_instances` | INT | Target number of running instances |
| `min_instances` | INT | Minimum instances (for auto-scaling) |
| `max_instances` | INT | Maximum instances |
| `heartbeat_timeout` | INT | Seconds before instance is considered stale |
| `environment_variables` | VARCHAR(MAX) | JSON dict of env vars |
| `built_in_port` | INT | Fixed port (if app uses one) |
| `auto_start` | BIT | Whether to auto-start on creation |

#### `application_instance`

| Column | Type | Description |
|---|---|---|
| `instance_id` | VARCHAR(100) | Unique instance identifier |
| `application_id` | VARCHAR(50) | FK → `application.id` |
| `agent_id` | VARCHAR(50) | FK → `agent.id` (NULL = orphan) |
| `process_id` | INT | OS process ID |
| `status` | VARCHAR(20) | `pending`, `starting`, `running`, `stopping`, `stopped`, `warning`, `error` |
| `cpu_percent` | DECIMAL(5,2) | Current CPU usage |
| `memory_mb` | DECIMAL(10,2) | Current memory usage |
| `assigned_port` | INT | Port assigned to this instance |
| `is_ready` | BIT | Whether the app has signaled ready |
| `last_heartbeat` | DATETIME2 | Last heartbeat from the instance |

#### `command_queue`

| Column | Type | Description |
|---|---|---|
| `command_id` | VARCHAR(50) | Unique command identifier |
| `command_type` | VARCHAR(20) | `spawn`, `kill`, `restart`, `stop_all` |
| `agent_id` | VARCHAR(50) | Target agent |
| `application_id` | VARCHAR(50) | Target application |
| `instance_id` | VARCHAR(100) | Target instance |
| `parameters` | VARCHAR(MAX) | JSON — serialized command parameters |
| `status` | VARCHAR(20) | `pending`, `sent`, `executing`, `completed`, `failed`, `cancelled` |

### 8.3 Relationships

```
agent ──1:N──> agent_application <──N:1── application
  │                                           │
  │                                           │
  └───1:N──> application_instance <──N:1──────┘
                    │
                    ├──1:N──> heartbeat
                    └──1:N──> metrics_history
                    
agent ──1:N──> command_queue
```

---

## 9. Communication Protocols

### 9.1 gRPC Protocol Buffers

#### `monitoring.proto` — Agent ↔ Control Plane

**Messages sent by Agent:**

| Message | Fields | When Sent |
|---|---|---|
| `AgentRegistrationRequest` | agentId, agentName, ipAddress, hostname, totalMemoryMb, cpuCores, osVersion | On startup / reconnect |
| `StatusReportRequest` | agentId, applicationStatuses[], systemMetrics, timestamp | Periodically |
| `Heartbeat` | agentId, timestamp | Every heartbeat interval via stream |
| `ApplicationSpawned` | instanceId, applicationId, processId, assignedPort, startTime | After process spawn |
| `ApplicationStopped` | instanceId, applicationId, exitCode, reason, stopTime | After process exit |
| `ErrorReport` | agentId, errorType, errorMessage, stackTrace, instanceId, applicationId | On errors |

**Messages sent by Control Plane:**

| Message | Fields | When Sent |
|---|---|---|
| `SpawnCommand` | applicationId, instanceId, executablePath, arguments, workingDirectory, envVars, port, instanceIndex | When scaling up |
| `KillCommand` | instanceId, force, timeoutSeconds | When scaling down or stopping |
| `RestartCommand` | instanceId, timeoutSeconds | On restart |
| `UpdateCommand` | instanceId, environmentVariables | On config update |

#### `application_monitoring.proto` — Application ↔ Control Plane

| Message | Fields | When Sent |
|---|---|---|
| `ApplicationRegistrationRequest` | applicationId, instanceId, applicationName, heartbeatInterval, executablePath, applicationType | App startup |
| `ApplicationReadyRequest` | applicationId, instanceId, timestamp, processId, assignedPort | App ready to serve |
| `ApplicationHeartbeatRequest` | applicationId, instanceId, timestamp, isHealthy, status, message, metricsJson | Every heartbeat |
| `PerformanceMetricRequest` | applicationId, instanceId, startTime, endTime, quantity, delta, recordedAt | On metric collection |

### 9.2 REST API (HTTP/1.1)

Used for management operations (CRUD on agents/applications, lifecycle commands). Accessible via Swagger UI in development mode at `http://localhost:5143/swagger`.

---

## 10. Data Flow Scenarios

### 10.1 Application Deployment (Scale Up)

```
1. Admin creates application via REST API (POST /api/applications)
2. Admin assigns application to agent (POST /api/agents/{id}/applications/{appId}/assign)
3. ScalingBackgroundService detects desired_instances > running_instances
4. ScalingEngine.ScaleUpApplication() finds an online assigned agent
5. CommandService.QueueSpawnCommand() creates a command in command_queue
6. If agent is connected via gRPC stream, command is sent immediately
7. Agent.GrpcClient receives SpawnCommand
8. Agent.ProcessManager.SpawnProcess() starts the process
9. Application (with PEPMonitoring) calls RegisterApplication → Ready → Heartbeat loop
10. Agent reports ApplicationSpawned via stream → DB updated
```

### 10.2 Heartbeat-Based Health Monitoring

```
1. Application sends RecordHeartbeat via PEPMonitoring SDK every 10 seconds
2. ApplicationMonitoringGrpcServiceImpl updates last_heartbeat in DB
3. HeartbeatMonitorBackgroundService checks every 10 seconds:
   - If last_heartbeat > 2× heartbeat_timeout → instance is stale
   - If overdue < 3× timeout → status = "warning"
   - If overdue ≥ 3× timeout → status = "error"
```

### 10.3 Agent Reconnection with Orphan Recovery

```
1. Agent goes offline (network issue / restart)
2. AgentManager.MarkOfflineAgents() sets agent status to "offline"
3. Running instances become orphans (agent_id still set but agent offline)
4. Agent reconnects → calls RegisterAgent
5. AgentGrpcServiceImpl.RegisterAgent:
   a. Upserts agent record (status = online)
   b. Gets assigned applications
   c. Finds orphan instances (agent_id IS NULL)
   d. Claims orphans: updates instance_id and agent_id
6. Agent receives active instances in response
7. Agent.ProcessManager.ReattachProcesses() re-discovers running processes
```

### 10.4 Scale Down

```
1. Admin updates desired_instances via REST API
2. ScalingBackgroundService detects running > desired
3. ScalingEngine.ScaleDownApplication() selects oldest instances
4. For orphan instances (no agent): directly marks as "stopped"
5. For normal instances: queues KILL command
6. Agent receives KillCommand → ProcessManager.KillProcess()
7. Agent reports ApplicationStopped → DB updated
```
