using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QueueJanne.Core.Connections;
using QueueJanne.Mcp;

// The app's updater replaces this exe while Claude sessions may still run it. .NET reads parts of a single-file program
// only when they're first used, which fails once the file is replaced, so everything is loaded up front.
LoadAllAssemblies();

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

static void LoadAllAssemblies()
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pending = new Stack<System.Reflection.Assembly>([System.Reflection.Assembly.GetEntryAssembly()!]);
    while (pending.TryPop(out var assembly))
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (seen.Add(reference.FullName))
            {
                try
                {
                    pending.Push(System.Reflection.Assembly.Load(reference));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                }
            }
        }
    }
}
