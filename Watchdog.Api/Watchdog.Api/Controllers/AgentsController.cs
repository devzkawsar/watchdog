using Microsoft.AspNetCore.Mvc;
using Watchdog.Api.Data;
using Watchdog.Api.Interface;
using Watchdog.Api.Services;

namespace Watchdog.WebApi.Controllers;

[ApiController]
[Route("api/")]
public class AgentsController : ControllerBase
{
    private readonly IAgentManager _agentManager;
    private readonly ILogger<AgentsController> _logger;
    
    public AgentsController(
        IAgentManager agentManager,
        ILogger<AgentsController> logger)
    {
        _agentManager = agentManager;
        _logger = logger;
    }
    
    [HttpGet("agent")]
    public async Task<ActionResult<IEnumerable<Agent>>> GetAll()
    {
        try
        {
            var agents = await _agentManager.GetAgents();
            return Ok(agents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all agents");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpGet("agent/{id}")]
    public async Task<ActionResult<Agent>> GetById(string id)
    {
        try
        {
            var agent = await _agentManager.GetAgent(id);
            
            if (agent == null)
                return NotFound(new { error = $"Agent '{id}' not found" });
            
            return Ok(agent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agent {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost("agent/{id}/heartbeat")]
    public async Task<ActionResult> Heartbeat(string id)
    {
        try
        {
            var success = await _agentManager.UpdateAgentHeartbeat(id);
            
            if (!success)
                return NotFound(new { error = $"Agent '{id}' not found" });
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating heartbeat for agent {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost("agent/{agentId}/application/{applicationId}/assign")]
    public async Task<ActionResult> AssignApplication(string agentId, string applicationId)
    {
        try
        {
            var success = await _agentManager.AssignApplicationToAgent(agentId, applicationId);
            
            if (!success)
                return BadRequest(new { error = "Failed to assign application to agent" });
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning application {ApplicationId} to agent {AgentId}", 
                applicationId, agentId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpGet("agent/{id}/application")]
    public async Task<ActionResult<IEnumerable<Application>>> GetAgentApplications(string id)
    {
        try
        {
            var applications = await _agentManager.GetAgentApplications(id);
            return Ok(applications);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting applications for agent {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}