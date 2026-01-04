using Microsoft.EntityFrameworkCore;
using Watchdog.WebApi.Data;
using Watchdog.WebApi.Services;
using Watchdog.WebApi.BackgroundServices;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Watchdog Control Plane API", Version = "v1" });
});

// Database Configuration (Dapper)
builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new SqlServerConnectionFactory(config.GetConnectionString("Default"));
});

// Register Repositories
builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();
builder.Services.AddScoped<IInstanceRepository, InstanceRepository>();

// Register Services
builder.Services.AddScoped<IApplicationManager, ApplicationManager>();
builder.Services.AddScoped<IAgentManager, AgentManager>();
builder.Services.AddScoped<IScalingEngine, ScalingEngine>();
builder.Services.AddScoped<ICommandService, CommandService>();

// gRPC Services
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = true;
    options.MaxReceiveMessageSize = 10 * 1024 * 1024; // 10MB
    options.MaxSendMessageSize = 10 * 1024 * 1024; // 10MB
});

// Background Services
builder.Services.AddHostedService<HealthCheckBackgroundService>();
builder.Services.AddHostedService<ScalingBackgroundService>();
builder.Services.AddHostedService<CommandDispatcherBackgroundService>();

// HTTP Client for agent communication
builder.Services.AddHttpClient();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Map gRPC service
app.MapGrpcService<AgentGrpcService>();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));


app.Run();

