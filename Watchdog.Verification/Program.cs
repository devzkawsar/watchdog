
using System.Data.SqlClient;
using Dapper;
using Grpc.Net.Client;
using Watchdog.Api.Protos;

Console.WriteLine("Starting Verification...");

var connectionString = "Server=localhost,1433;Database=WatchdogDB;User Id=sa;Password=YourStrong@Password;TrustServerCertificate=true;";
using var connection = new SqlConnection(connectionString);

// 1. Clean up previous test data
var testAppId = "app-test-orphan";
var testAgentId = "agent-test-verifier";
var oldInstanceId = "old-orphan-guid";
var processId = 99999;

Console.WriteLine("Cleaning up test data...");
await connection.ExecuteAsync("DELETE FROM command_queue WHERE application_id = @AppId", new { AppId = testAppId });
await connection.ExecuteAsync("DELETE FROM agent_application WHERE application_id = @AppId", new { AppId = testAppId });
await connection.ExecuteAsync("DELETE FROM application_instance WHERE application_id = @AppId", new { AppId = testAppId });
await connection.ExecuteAsync("DELETE FROM application WHERE id = @AppId", new { AppId = testAppId });
await connection.ExecuteAsync("DELETE FROM agent WHERE id = @AgentId", new { AgentId = testAgentId });

// 2. Setup Test Data
Console.WriteLine("Setting up test data...");

// Create Test App
await connection.ExecuteAsync(@"
    INSERT INTO application (id, name, executable_path, application_type, created)
    VALUES (@Id, 'Test Orphan App', 'test.exe', 0, GETUTCDATE())", 
    new { Id = testAppId });

// Create Orphan Instance (AgentId is NULL)
await connection.ExecuteAsync(@"
    INSERT INTO application_instance (instance_id, application_id, agent_id, process_id, status, created_at)
    VALUES (@InstanceId, @AppId, NULL, @ProcessId, 'running', GETUTCDATE())",
    new { InstanceId = oldInstanceId, AppId = testAppId, ProcessId = processId });
    
Console.WriteLine($"Inserted orphan instance: {oldInstanceId}");

// 3. Trigger Registration via gRPC on localhost:5144 (or wherever API is running)
// We need to assume API is running.
var channel = GrpcChannel.ForAddress("http://localhost:5144");
var client = new AgentService.AgentServiceClient(channel);

Console.WriteLine("Calling RegisterAgent...");
var request = new AgentRegistrationRequest
{
    AgentId = testAgentId,
    AgentName = "test-agent",
    Hostname = "test-host",
    IpAddress = "127.0.0.1",
    OsVersion = "TestOS",
    TotalMemoryMb = 1000,
    CpuCores = 4
};

// Also we need to assign the app to the agent so the logic picks it up!
// Logic in RegisterAgent checks assignedApplications.
// So we need to insert into agent_application table BEFORE registering?
// RegisterAgent calls await _agentManager.RegisterAgent(agentRegistration); which creates the agent if not exists.
// Then await _agentManager.GetAgentApplications(agent.Id);
// GetAgentApplications logic usually returns apps assigned to this agent in agent_application table.
// So we need to assign the app to the agent manually or trust the logic does it.
// Default logic might not auto-assign. Let's insert assignment manually first to be sure.

// Register agent first to ensure FK exists? No, RegisterAgent creates it.
// But GetAgentApplications is called immediately after.
// Let's create the agent and assignment manually before calling RegisterAgent just to be safe, 
// OR rely on RegisterAgent to create agent, but then we might miss assignment in first call?
// Actually API logic: var agent = await _agentManager.RegisterAgent(agentRegistration);
// If RegisterAgent is idempotent and updates/creates, it returns the agent.
// Then _agentManager.GetAgentApplications(agent.Id) is called.
// So if I don't have assignment, it returns empty list.
// And then orphan logic: var assignedAppIds = assignedApplications.Select(a => a.Id).ToList();
// loops only over assigned apps. 
// SO I MUST CREATE ASSIGNMENT.

// So let's create agent and assignment manually.
await connection.ExecuteAsync(@"
    INSERT INTO agent (id, name, ip_address, status) 
    VALUES (@Id, 'test-agent', '127.0.0.1', 'online')",
    new { Id = testAgentId });

await connection.ExecuteAsync(@"
    INSERT INTO agent_application (agent_id, application_id)
    VALUES (@AgentId, @AppId)",
    new { AgentId = testAgentId, AppId = testAppId });

Console.WriteLine("Pre-assigned app to agent.");

try 
{
    var response = await client.RegisterAgentAsync(request);
    Console.WriteLine($"Register Response: {response.Success} - {response.Message}");
    
    // 4. Verify Response contains the claimed instance
    var claimed = response.ActiveInstances.FirstOrDefault(i => i.ApplicationId == testAppId);
    if (claimed != null)
    {
        Console.WriteLine($"Claimed Instance in Response: {claimed.InstanceId} (Status: {claimed.Status})");
        
        // check format: {Hostname}-{AgentName}-{Guid}
        if (claimed.InstanceId.StartsWith("test-host-test-agent-"))
        {
             Console.WriteLine("SUCCESS: Instance ID format matches requirement.");
        }
        else
        {
             Console.WriteLine($"FAILURE: Instance ID format mismatch. Got {claimed.InstanceId}");
        }
    }
    else
    {
        Console.WriteLine("FAILURE: No instance returned for test app.");
    }
    
    // 5. Verify DB
    var dbInstance = await connection.QueryFirstOrDefaultAsync<dynamic>(
        "SELECT * FROM application_instance WHERE application_id = @AppId", new { AppId = testAppId });
        
    if (dbInstance != null)
    {
        Console.WriteLine($"DB Instance ID: {dbInstance.instance_id}");
        Console.WriteLine($"DB Agent ID: {dbInstance.agent_id}");
        
        if (dbInstance.instance_id != oldInstanceId && dbInstance.agent_id == testAgentId)
        {
            Console.WriteLine("SUCCESS: DB updated correctly (Old ID gone, New ID set, Agent set).");
        }
        else
        {
            Console.WriteLine($"FAILURE: DB state incorrect. ID={dbInstance.instance_id}, Agent={dbInstance.agent_id}");
        }
    }
    else
    {
        Console.WriteLine("FAILURE: Instance gone from DB!");
    }

}
catch (Exception ex)
{
    Console.WriteLine($"RPC Error: {ex.Message}");
}

Console.WriteLine("Done.");
