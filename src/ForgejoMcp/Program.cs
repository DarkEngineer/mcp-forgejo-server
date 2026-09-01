// forgejo-mcp-server entry point.
//
// Wires the hosting builder for a stdio-transport MCP server that exposes the
// ForgejoClient as MCP tools (list_repos, get_repo, create_issue, list_issues,
// list_pull_requests, list_commits, get_file) and as MCP resources
// (forgejo://repo/{owner}/{name}, forgejo://instance).
//
// Configuration precedence (highest wins):
//   1. Environment variables      — FORGEJO_URL / FORGEJO_TOKEN /
//      FORGEJO_USERNAME / FORGEJO_PASSWORD (explicitly mapped onto the
//      "Forgejo" section, so they override the file)
//   2. appsettings.{Environment}.json — per-environment overrides (next to the binary)
//   3. appsettings.json           — repository-shipped defaults (next to the binary)
//
// So a fresh clone runs with nothing but:
//   export FORGEJO_URL=https://git.home.internal
//   export FORGEJO_TOKEN=***
//   dotnet run --project src/ForgejoMcp
//
// Auth precedence: FORGEJO_TOKEN > FORGEJO_USERNAME+FORGEJO_PASSWORD > anonymous.
//
// Stdio note: stdout is the MCP protocol channel. All logging is therefore
// routed to stderr (see ConsoleLoggerOptions.LogToStandardErrorThreshold).
using Forgejo.Client;
using Forgejo.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

const string ServerName = "forgejo-mcp-server";
const string ServerVersion = "3.0.1"; // matches release tag v3.0.1

// ----- CLI flags (handled before anything is constructed) ---------------
// The stdio MCP host takes no CLI arguments in normal operation (MCP clients
// pass none), but a standalone user typing `mcp-server --help` must get a
// usage page instead of a configuration error — so `--help/-h` and
// `--version` are answered before the client is validated.
if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
{
    Console.WriteLine(
        $"{ServerName} v{ServerVersion}\n" +
        "MCP server exposing Forgejo/Gitea operations over stdio (NDJSON).\n" +
        "MCP clients launch this process themselves; no flags are needed.\n\n" +
        "Usage: forgejo-mcp-server [--help | --version]\n\n" +
        "Flags:\n" +
        "  -h, --help    Show this help and exit.\n" +
        "  --version     Show the version and exit.\n\n" +
        "Configuration (highest priority first):\n" +
        "  1. Environment variables  FORGEJO_URL (required), FORGEJO_TOKEN, " +
        "FORGEJO_USERNAME, FORGEJO_PASSWORD,\n" +
        "                            FORGEJO_MAX_RETRIES, FORGEJO_RETRY_BASE_DELAY_SECONDS\n" +
        "  2. appsettings.json / appsettings.{Environment}.json next to the binary\n\n" +
        "Authentication (token XOR basic, never both):\n" +
        "  FORGEJO_TOKEN                     = personal access token (recommended)\n" +
        "  FORGEJO_USERNAME + FORGEJO_PASSWORD = HTTP basic auth (both required)\n" +
        "  (neither set → anonymous, public repositories only)\n\n" +
        "Example:\n" +
        "  FORGEJO_URL=https://git.home.internal FORGEJO_TOKEN=*** forgejo-mcp-server\n\n" +
        "MCP client registration (Hermes Agent):\n" +
        "  hermes mcp add forgejo --command <path-to>/forgejo-mcp-server \\\n" +
        "    --env FORGEJO_URL=https://git.home.internal FORGEJO_TOKEN=***\n\n" +
        "Or any MCP host (JSON config):\n" +
        "  { \"mcpServers\": { \"forgejo\": { \"command\": \"<path-to>/forgejo-mcp-server\", " +
        "\"env\": { \"FORGEJO_URL\": \"https://git.home.internal\", \"FORGEJO_TOKEN\": \"***\" } } }");
    Environment.Exit(0);
}
if (args.Length > 0 && args[0] == "--version")
{
    Console.WriteLine($"{ServerName} {ServerVersion}");
    Environment.Exit(0);
}

var builder = Host.CreateApplicationBuilder(args);

// ----- Configuration ----------------------------------------------------
// The generic Host resolves "appsettings.json" relative to the process
// working directory, but MCP hosts spawn this server from an arbitrary CWD
// (Claude Desktop, Claude Code, CI, …), so the bundled defaults must come
// from the application base directory — where appsettings.json sits next to
// ForgejoMcp.dll (CopyToOutputDirectory in the csproj).
//
// Effective precedence, highest wins:
//   1. FORGEJO_URL / FORGEJO_TOKEN / FORGEJO_USERNAME / FORGEJO_PASSWORD
//      environment variables (mapped explicitly onto the "Forgejo" section —
//      a plain AddEnvironmentVariables() would expose them as FORGEJO_* keys,
//      which do NOT bind to the Forgejo:Url property)
//   2. appsettings.json / appsettings.{Environment}.json next to the binary
//   3. the Host's CWD-sourced sources (kept for in-repo development)
var baseDir = AppContext.BaseDirectory;
builder.Configuration.Sources.Insert(
    0, new JsonConfigurationSource
    {
        Path = "appsettings.json",
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(baseDir),
        ReloadOnChange = true,
        Optional = true,
    });
var devSettings = $"appsettings.{builder.Environment.EnvironmentName}.json";
if (devSettings != "appsettings.json")
{
    builder.Configuration.Sources.Insert(
        0, new JsonConfigurationSource
        {
            Path = devSettings,
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(baseDir),
            ReloadOnChange = true,
            Optional = true,
        });
}

// ForgejoServerOptions.FromConfiguration binds the "Forgejo" section (Url,
// Token, Username, Password, MaxRetries, RetryBaseDelaySeconds) from the
// sources above. ApplyEnvironmentOverrides then layers the FORGEJO_*
// environment variables on top (highest priority), so a fresh clone runs with:
//     FORGEJO_URL=https://git.home.internal
//     FORGEJO_TOKEN=***
// BuildClient validates eagerly (required Url, token-XOR-basic auth) so a
// misconfiguration fails fast at startup, not on the first tool call.
var options = ForgejoServerOptions.ApplyEnvironmentOverrides(
    ForgejoServerOptions.FromConfiguration(builder.Configuration));
var client = options.BuildClient();

// Stdio servers must keep stdout free for the protocol: route all console
// logging to stderr and drop Information-level noise by default.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation
    {
        Name = ServerName,
        Title = "Forgejo MCP Server",
        Version = ServerVersion,
        Description = "MCP tools and resources for a Forgejo/Gitea instance: " +
                      "repositories, issues, pull requests, commits, and file contents.",
    };
    options.ServerInstructions =
        "Tools and resources operate against the configured Forgejo instance. " +
        "Use list_repos to discover repositories, get_repo (or the " +
        "forgejo://repo/{owner}/{name} resource) for metadata, and the issue, " +
        "pull-request, commit, and file tools for details. create_issue is the " +
        "only mutating operation and requires a token with write permission.";
})
.WithStdioServerTransport()
// Tools: discover [McpServerToolType] classes in this assembly
// (ForgejoMcpToolSurface). Constructor dependency (ForgejoClient) resolves
// from the DI container below.
.WithToolsFromAssembly()
// Resources: concrete instances (they need the client; attribute-based
// discovery is not needed because the resource shape depends on it).
.WithResources(new McpServerResource[]
{
    new ForgejoRepoResource(client),
    new ForgejoInstanceResource(client),
});

// Tool constructor dependency (and any future DI-resolved tool services).
builder.Services.AddSingleton<ForgejoClient>(client);
builder.Services.AddSingleton<ForgejoMcpToolSurface>(_ => new ForgejoMcpToolSurface(client));

var app = builder.Build();
await app.RunAsync();
