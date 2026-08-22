# Forgejo MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server for
[Forgejo](https://forgejo.org/) (Gitea-compatible) — a self-hostable Git
repository host — built with the .NET / C#
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
SDK (2.2.0, .NET 10).

It exposes Forgejo operations as MCP **tools** and **resources**, so any
MCP-capable client (Claude, IDE agents, etc.) can drive a Forgejo instance
through natural-language requests.

## Stack

| Component         | Value                          |
| ----------------- | ------------------------------ |
| Runtime           | .NET 10 (`net10.0`)            |
| MCP SDK           | `ModelContextProtocol` 2.2.0   |
| Host              | `Microsoft.Extensions.Hosting` |
| Transport         | stdio (stdin/stdout, NDJSON)   |
| Output            | Executable (`Exe`)             |

## Repository layout

```
.
├── README.md
├── src/
│   ├── ForgejoClient/        # typed, retrying REST client for the Forgejo v1 API
│   │   ├── ForgejoClient.cs
│   │   ├── ForgejoCredentials.cs
│   │   ├── ForgejoException.cs
│   │   ├── ForgejoJson.cs
│   │   ├── Models.cs
│   │   └── RequestOptions.cs
│   └── ForgejoMcp/           # MCP server (tools + resources over ForgejoClient)
│       ├── Program.cs
│       ├── ForgejoMcpToolSurface.cs
│       └── ForgejoResources.cs
└── tests/
    └── (planned) ForgejoClient.Tests for the REST client
```

## Tools

| Tool                  | Description                                                            |
| --------------------- | ---------------------------------------------------------------------- |
| `list_repos`          | Repositories visible to the authenticated identity (`GET /user/repos`). |
| `get_repo`            | Metadata for `owner/name` (`GET /repos/{owner}/{name}`).               |
| `create_issue`        | Create an issue in `owner/name` (`POST /repos/{owner}/{name}/issues`). |
| `list_issues`         | List issues of `owner/name` (paged, optional `state` filter).          |
| `list_pull_requests`  | List pull requests of `owner/name` (paged, optional `state` filter).   |
| `list_commits`        | List commits of `owner/name` (paged, optional `branch` filter).        |
| `get_file`            | Raw file content at `path` on `branch` (defaults to the default branch).|

## Resources

| URI                                    | Description                                                       |
| -------------------------------------- | ----------------------------------------------------------------- |
| `forgejo://instance`                   | Server self-identification: instance URL + auth mode.             |
| `forgejo://repo/{owner}/{name}`        | Repo metadata — the same document the `get_repo` tool returns.    |

`resources/list` reports the static `instance` resource; the repo resource is
exposed as a template in `resources/templates/list`.

## Configuration

| Variable           | Required | Description                                          |
| ------------------ | -------- | ---------------------------------------------------- |
| `FORGEJO_URL`      | yes      | Instance root, e.g. `https://git.home.internal`. The client appends `/api/v1` automatically (idempotent if already present). |
| `FORGEJO_TOKEN`    | either   | Personal access token (`Authorization: token …`).    |
| `FORGEJO_USERNAME` + `FORGEJO_PASSWORD` | either   | HTTP basic credentials.                              |

With neither credential pair set the client runs anonymous and only public
data is reachable.

## Running

```sh
export FORGEJO_URL=https://git.home.internal
export FORGEJO_TOKEN=***
dotnet run --project src/ForgejoMcp
```

Or point any stdio MCP client at the built binary:

```
src/ForgejoMcp/bin/Debug/net10.0/ForgejoMcp
```

> **Internal TLS CAs:** if the instance uses a private CA, make the CA
> available to the .NET runtime before start (e.g.
> `export SSL_CERT_FILE=/path/to/ca.crt` plus the usual trust setup);
> the client does not disable certificate validation.

## Error contract

Tool calls never throw at the MCP level: failures are returned as
`isError` content of shape

```json
{ "error": { "code": "not_found|unauthorized|forbidden|rate_limited|transport_error|...", "message": "…" } }
```

`code` is the lowercased Forgejo HTTP status (e.g. `not_found` for 404) or a
stable transport error name, so clients can branch on it.

## Notes

- The client retries transient failures (HTTP 429/5xx, transport errors) with
  exponential backoff (4 total attempts by default); `Retry-After` headers are
  honored.
- All JSON on the wire is snake_case, matching the Forgejo API shape.
