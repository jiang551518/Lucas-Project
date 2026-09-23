using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 8);
builder.Services.AddDataProtection().SetApplicationName("LucasAgent");
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "tauri://localhost", "http://tauri.localhost")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddScoped<AgentBackend.Services.IAgentService, AgentBackend.Services.AgentService>();
builder.Services.AddSingleton<AgentBackend.Services.ToolApprovalBroker>();
builder.Services.AddSingleton<AgentBackend.Services.WorkspaceToolExecutor>();
builder.Services.AddSingleton<AgentBackend.Services.AgentRunRegistry>();
builder.Services.AddSingleton<AgentBackend.Services.GitChangeTracker>();
builder.Services.AddSingleton<AgentBackend.Repositories.IAgentRepository, AgentBackend.Repositories.SqliteAgentRepository>();
builder.Services.AddHttpClient("ModelProvider", client => client.Timeout = TimeSpan.FromMinutes(5));

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseAuthorization();

app.MapControllers();
app.MapHub<AgentBackend.Hubs.AgentHub>("/hubs/agent");

app.Run();
