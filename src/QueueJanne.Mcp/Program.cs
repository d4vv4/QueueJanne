using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QueueJanne.Core.Connections;
using QueueJanne.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so every log line goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(_ => new ConnectionStore());
builder.Services.AddSingleton<NamespaceClientCache>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
