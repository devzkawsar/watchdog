
-- Applications table
CREATE TABLE Applications (
      Id NVARCHAR(100) PRIMARY KEY,
      Name NVARCHAR(200) NOT NULL,
      DisplayName NVARCHAR(200) NOT NULL,
      ExecutablePath NVARCHAR(500) NOT NULL,
      WorkingDirectory NVARCHAR(500),
      Arguments NVARCHAR(1000),
      AppType INT NOT NULL DEFAULT 0, -- 0=Console, 1=Service, 2=IIS
      ExpectedAppId NVARCHAR(100),
      HeartbeatTimeoutSeconds INT DEFAULT 120,
      StartupTimeoutSeconds INT DEFAULT 180,
      MaxRestartAttempts INT DEFAULT 3,
      DesiredInstances INT DEFAULT 1,
      AutoStart BIT DEFAULT 1,
      Status INT NOT NULL DEFAULT 0, -- 0=Unknown, 1=Healthy, 2=Warning, 3=Error
      CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
      UpdatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Application scaling configuration
CREATE TABLE AppScalingConfig (
  AppId NVARCHAR(100) PRIMARY KEY REFERENCES Applications(Id),
  DesiredReplica INT NOT NULL DEFAULT 1,
  MinReplica INT NOT NULL DEFAULT 1,
  MaxReplica INT NOT NULL DEFAULT 5,
  ScaleUpThresholdCpu DECIMAL(5,2) DEFAULT 80.0,
  ScaleDownThresholdCpu DECIMAL(5,2) DEFAULT 20.0,
  ScaleUpThresholdMemory DECIMAL(5,2) DEFAULT 85.0,
  ScaleDownThresholdMemory DECIMAL(5,2) DEFAULT 30.0,
  CoolDownSeconds INT DEFAULT 300,
  UpdatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Agents table
CREATE TABLE Agents (
    AgentId NVARCHAR(100) PRIMARY KEY,
    Hostname NVARCHAR(200) NOT NULL,
    IpAddress NVARCHAR(45) NOT NULL,
    Status INT NOT NULL DEFAULT 0, -- 0=Offline, 1=Online, 2=Error
    LastHeartbeat DATETIME2,
    CpuCores INT,
    TotalMemoryMb INT,
    OsVersion NVARCHAR(100),
    Capabilities NVARCHAR(MAX),
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Application instances
CREATE TABLE AppInstances (
  InstanceId NVARCHAR(150) PRIMARY KEY,
  AppId NVARCHAR(100) NOT NULL REFERENCES Applications(Id),
  AgentId NVARCHAR(100) NOT NULL REFERENCES Agents(AgentId),
  ProcessId INT,
  Status INT NOT NULL DEFAULT 0,
  CpuPercent DECIMAL(5,2),
  MemoryMb DECIMAL(10,2),
  StartTime DATETIME2,
  LastHeartbeat DATETIME2,
  CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);

-- Agent Application Assignment
CREATE TABLE AgentApplications (
   AgentId NVARCHAR(100) REFERENCES Agents(AgentId),
   AppId NVARCHAR(100) REFERENCES Applications(Id),
   InstanceCount INT DEFAULT 1,
   CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
   PRIMARY KEY (AgentId, AppId)
);

-- Heartbeat log
CREATE TABLE HeartbeatLog (
  Id BIGINT IDENTITY(1,1) PRIMARY KEY,
  AppId NVARCHAR(100) NOT NULL,
  InstanceId NVARCHAR(150) NOT NULL,
  Timestamp DATETIME2 DEFAULT GETUTCDATE(),
  Status INT NOT NULL,
  CpuPercent DECIMAL(5,2),
  MemoryMb DECIMAL(10,2)
);

CREATE INDEX IX_HeartbeatLog_AppId ON HeartbeatLog(AppId);
CREATE INDEX IX_HeartbeatLog_Timestamp ON HeartbeatLog(Timestamp);

-- Recovery history
CREATE TABLE RecoveryHistory (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    AppId NVARCHAR(100) NOT NULL,
    AttemptNumber INT NOT NULL,
    Success BIT NOT NULL,
    FailureReason NVARCHAR(500),
    StartedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CompletedAt DATETIME2,
    Details NVARCHAR(MAX)
);

CREATE INDEX IX_RecoveryHistory_AppId ON RecoveryHistory(AppId);
GO

-- Stored Procedure for Agent Heartbeat
CREATE PROCEDURE UpdateAgentHeartbeat
    @AgentId NVARCHAR(100),
    @Status INT,
    @CpuCores INT = NULL,
    @TotalMemoryMb INT = NULL
AS
BEGIN
UPDATE Agents
SET LastHeartbeat = GETUTCDATE(),
    Status = @Status,
    CpuCores = ISNULL(@CpuCores, CpuCores),
    TotalMemoryMb = ISNULL(@TotalMemoryMb, TotalMemoryMb),
    UpdatedAt = GETUTCDATE()
WHERE AgentId = @AgentId;

IF @@ROWCOUNT = 0
BEGIN
INSERT INTO Agents (AgentId, Hostname, IpAddress, Status, LastHeartbeat, CpuCores, TotalMemoryMb)
VALUES (@AgentId, 'Unknown', '0.0.0.0', @Status, GETUTCDATE(), @CpuCores, @TotalMemoryMb);
END
END
GO