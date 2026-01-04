using Microsoft.OpenApi.Models;
using Watchdog.Api.BackgroundServices;
using Watchdog.Api.Data;
using Watchdog.Api.gRPC;
using Watchdog.Api.Services;

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
    var connectionString = config.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Connection string 'Default' is not configured.");

    return new SqlServerConnectionFactory(connectionString);
});

// Register Repositories
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
    options.MaxReceiveMessageSize = 10 * 1024 * 1024; // 10MB
    options.MaxSendMessageSize = 10 * 1024 * 1024; // 10MB
});

// Background Services
builder.Services.AddHostedService<ScalingBackgroundService>();
builder.Services.AddHostedService<CommandDispatcherBackgroundService>();
builder.Services.AddHostedService<GrpcConnectionCleanupBackgroundService>();

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
app.MapGrpcService<AgentGrpcServiceImpl>();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));


app.Run();

