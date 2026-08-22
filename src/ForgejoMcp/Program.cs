// forgejo-mcp-server entry point.
//
// Wires the hosting builder for a stdio-transport MCP server that exposes the
// ForgejoClient (previous task) as MCP tools (list_repos, get_repo,
// create_issue, list_issues, list_pull_requests, get_file, list_commits) and
// as MCP resources (forgejo://repo/{owner}/{name}, forgejo://instance).
//
// Configuration is read from environment variables so the server can point at
// any Forgejo instance without code changes:
//
//   FORGEJO_URL       (required) base URL, e.g. https://git.home.internal
//   FORGEJO_TOKEN     (token auth; default mode) personal access token
//   FORGEJO_USERNAME  (basic auth; used with FORGEJO_PASSWORD)
//   FORGEJO_PASSWORD  (basic auth; used with FORGEJO_USERNAME)
//
// Auth precedence: FORGEJO_TOKEN > FORGEJO_USERNAME+FORGEJO_PASSWORD > anonymous.
//
// Stdio note: stdout is the MCP protocol channel. All logging is therefore
// routed to stderr (see ConsoleLoggerOptions.LogToStandardErrorThreshold).
using Forgejo.Client;
using Forgejo.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

const string ServerName = "forgejo-mcp-server";
const string ServerVersion = "1.0.0";

// ----- Configuration ----------------------------------------------------

var urlEnv = Environment.GetEnvironmentVariable("FORGEJO_URL") ?? throw
    new InvalidOperationException(
        "FORGEJO_URL is not set. Point the server at a Forgejo instance, e.g. " +
        "FORGEJO_URL=https://git.home.internal");

Uri baseUrl;
try
{
    baseUrl = new Uri(urlEnv, UriKind.Absolute);
}
catch (UriFormatException ex)
{
    throw new InvalidOperationException($"FORGEJO_URL '{urlEnv}' is not a valid absolute URL.", ex);
}

var token = Environment.GetEnvironmentVariable("FORGEJO_TOKEN");
var username = Environment.GetEnvironmentVariable("FORGEJO_USERNAME");
var password = Environment.GetEnvironmentVariable("FORGEJO_PASSWORD");

ForgejoCredentials credentials;
if (!string.IsNullOrWhiteSpace(token))
{
    credentials = ForgejoCredentials.ForToken(token!);
}
else if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
{
    credentials = ForgejoCredentials.ForBasic(username!, password!);
}
else
{
    credentials = ForgejoCredentials.Anonymous();
}

// Single shared client instance for the whole process. Tools and resources
// both receive it (the SDK resolves tool constructor dependencies from the
// DI container, so it is registered there too).
var client = new ForgejoClient(baseUrl, credentials);

// ----- Hosting ----------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

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
