# McpForgejoServer

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server template
for [Forgejo](https://forgejo.org/) — a self-hostable Git repository host — built
with the .NET / C# [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
SDK.

The intent is to expose Forgejo operations (repositories, issues, pull requests,
commits, and more) as MCP **tools**, so any MCP-capable client (Claude, IDE
agents, etc.) can drive a Forgejo instance through natural-language requests.

> **Status: initial template / scaffold.**
> This repository currently contains the project skeleton wired against the
> `ModelContextProtocol` 2.2.0 SDK targeting .NET 10. The concrete Forgejo MCP
> tool implementations (repo/issue/PR handlers) are **pending** and form a
> follow-up work item. Do not assume the tool surface is complete.

## Stack

| Component         | Value                          |
| ----------------- | ------------------------------ |
| Runtime           | .NET 10 (`net10.0`)            |
| MCP SDK           | `ModelContextProtocol` 2.2.0   |
| Host              | `Microsoft.Extensions.Hosting` |
| Output            | Executable (`Exe`)             |

## Repository layout

```
.
├── McpForgejoServer.csproj   # project file (SDK + MCP references)
├── Program.cs                # entry point (template stub)
└── README.md                 # this file
```

## Getting started

Requires the .NET 10 SDK.

```sh
dotnet restore
dotnet build
dotnet run
```

## Configuration (planned)

When the Forgejo tool handlers are implemented, the server is expected to accept
an instance URL and a token via environment variables:

- `FORGEJO_URL`   — base URL of the Forgejo instance, e.g. `http://git.home.internal:3000`
- `FORGEJO_TOKEN` — personal access token with the scopes the tools need

## License

Add a license before publishing the full implementation.
