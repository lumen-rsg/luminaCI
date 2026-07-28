using Lumina.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs exclusively to the MCP stdio protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(LuminaOptions.FromEnvironment());
builder.Services.AddSingleton<ILuminaApiClient>(services =>
    LuminaApiClient.Create(services.GetRequiredService<LuminaOptions>()));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
