using Microsoft.AspNetCore.Mvc;
using Watchdog.Api.Data;
using Watchdog.Api.Interface;
using Watchdog.Api.Services;

namespace Watchdog.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationManager _applicationManager;
    private readonly ILogger<ApplicationsController> _logger;
    
    public ApplicationsController(
        IApplicationManager applicationManager,
        ILogger<ApplicationsController> logger)
    {
        _applicationManager = applicationManager;
        _logger = logger;
    }
    
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Application>>> GetAll()
    {
        try
        {
            var applications = await _applicationManager.GetApplications();
            return Ok(applications);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all applications");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpGet("{id}")]
    public async Task<ActionResult<Application>> GetById(string id)
    {
        try
        {
            var application = await _applicationManager.GetApplication(id);
            
            if (application == null)
                return NotFound(new { error = $"Application '{id}' not found" });
            
            return Ok(application);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost]
    public async Task<ActionResult<Application>> Create([FromBody] CreateApplicationRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            var application = await _applicationManager.CreateApplication(request);
            return CreatedAtAction(nameof(GetById), new { id = application.Id }, application);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating application");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPut("{id}")]
    public async Task<ActionResult<Application>> Update(string id, [FromBody] UpdateApplicationRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            
            var success = await _applicationManager.UpdateApplication(id, request);
            
            if (!success)
                return NotFound(new { error = $"Application '{id}' not found" });
            
            var application = await _applicationManager.GetApplicationAsync(id);
            return Ok(application);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        try
        {
            var success = await _applicationManager.DeleteApplication(id);
            
            if (!success)
                return NotFound(new { error = $"Application '{id}' not found" });
            
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpGet("{id}/instances")]
    public async Task<ActionResult<IEnumerable<ApplicationInstance>>> GetInstances(string id)
    {
        try
        {
            var instances = await _applicationManager.GetApplicationInstances(id);
            return Ok(instances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting instances for application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost("{id}/start")]
    public async Task<ActionResult> Start(string id)
    {
        try
        {
            var success = await _applicationManager.StartApplication(id);
            
            if (!success)
                return BadRequest(new { error = $"Failed to start application '{id}'" });
            
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost("{id}/stop")]
    public async Task<ActionResult> Stop(string id)
    {
        try
        {
            var success = await _applicationManager.StopApplication(id);
            
            if (!success)
                return BadRequest(new { error = $"Failed to stop application '{id}'" });
            
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost("{id}/restart")]
    public async Task<ActionResult> Restart(string id)
    {
        try
        {
            var success = await _applicationManager.RestartApplication(id);
            
            if (!success)
                return BadRequest(new { error = $"Failed to restart application '{id}'" });
            
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting application {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}