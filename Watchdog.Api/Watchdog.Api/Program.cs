using Microsoft.OpenApi.Models;
using Watchdog.Api.BackgroundServices;
using Watchdog.Api.Data;
using Watchdog.Api.gRPC;
using Watchdog.Api.Interface;
using Watchdog.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Watchdog Control Plane API", Version = "v1" });
});

builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Connection string 'Default' is not configured.");

    return new SqlServerConnectionFactory(connectionString);
});

builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();

// Register Services
builder.Services.AddScoped<IApplicationManager, ApplicationManager>();
builder.Services.AddScoped<IAgentManager, AgentManager>();
builder.Services.AddScoped<IScalingEngine, ScalingEngine>();
builder.Services.AddScoped<ICommandService, CommandService>();
builder.Services.AddScoped<INetworkManager, NetworkManager>();
builder.Services.AddSingleton<IAgentGrpcService, AgentGrpcService>();

// gRPC Services
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = true;
    options.MaxReceiveMessageSize = 10 * 1024 * 1024;
    options.MaxSendMessageSize = 10 * 1024 * 1024;
});

// Background Services
// builder.Services.AddHostedService<ScalingBackgroundService>();
// builder.Services.AddHostedService<CommandDispatcherBackgroundService>();
// builder.Services.AddHostedService<GrpcConnectionCleanupBackgroundService>();

builder.Services.AddHttpClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Map gRPC service
app.MapGrpcService<AgentGrpcServiceImpl>();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));


app.Run();

