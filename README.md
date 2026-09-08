# Forgejo MCP Server

MCP (Model Context Protocol) server for **Forgejo** (Gitea-compatible) — a
self-hosted git hosting. Built in .NET / C# on the [`ModelContextProtocol`
2.2.0](https://www.nuget.org/packages/ModelContextProtocol) SDK (.NET 10).

The server exposes Forgejo operations as MCP **tools** and **resources**, so
any MCP client (e.g. IDE agents) can drive a Forgejo instance via simple
text prompts.

The repository also serves as a **template** for building your own MCP servers
— see the [Use-As-Template](#use-the-repo-as-a-template-for-your-own-mcp-server)
section below.

## Stack

| Layer         | Value                              |
|---------------|------------------------------------|
| Runtime       | .NET 10 (`net10.0`)               |
| SDK MCP       | `ModelContextProtocol` 2.2.0      |
| Hosting       | `Microsoft.Extensions.Hosting`    |
| Transport     | stdio (stdin/stdout, NDJSON)      |
| Output        | native executable (`Exe`)         |

## Repository layout

```
.
├── README.md
├── LICENSE                 # MIT
├── .gitignore
├── ForgejoMcp.sln
├── .github/workflows/ci.yml    # CI (GitHub Actions)
├── .forgejo/workflows/ci.yml   # CI (Forgejo Actions) — same file
├── scripts/
│   └── mcp_smoke.py            # MCP stdio smoke-test driver
├── src/
│   ├── ForgejoClient/        # typed REST client for Forgejo v1 API (retry + auth)
│   │   ├── ForgejoClient.cs
│   │   ├── ForgejoCredentials.cs
│   │   ├── ForgejoException.cs
│   │   ├── ForgejoJson.cs
│   │   ├── ListResult.cs
│   │   ├── Models.cs
│   │   └── RequestOptions.cs
│   └── ForgejoMcp/           # MCP server (tools + resources over the client)
│       ├── Program.cs
│       ├── ForgejoServerOptions.cs
│       ├── ForgejoMcpToolSurface.cs
│       ├── ForgejoResources.cs
│       └── appsettings.json
└── tests/
    └── ForgejoMcp.Tests/     # xUnit: REST client, config, MCP surface
```

## Requirements

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`) — to build and run.
- An **instance of Forgejo / Gitea** with a generated *access token*
  (Settings → Authorization → Access Tokens → +).
  Set `FORGEJO_TOKEN` env var (PAT); or pass a `.netrc` with the git password
  instead.

## Running

```bash
# 1. Build
dotnet build

# 2. Run directly
dotnet run --project src/ForgejoMcp

# 3. Publish a standalone self-contained binary (linux-x64)
dotnet publish src/ForgejoMcp/ForgejoMcp.csproj -c Release -r linux-x64 \
  --self-contained true --output publish/
```

## MCP protocol (brief)

The server speaks MCP v2024-11-05 over **stdio** (lines-delimited, JSON-RPC
2.0). A sample client exchange for `create_issue`:

```
>>> { jsonrpc: "2.0", id: 1, method: "initialize",
      params: { protocolVersion: "2024-11-05",
                capabilities: {},
                clientInfo: { name: "my-agent", version: "1.0" } }}

{ jsonrpc: "2.0", id: 1, result: { protocolVersion: "2024-11-05",
                                      serverInfo: { name: "...", version: "3.0.1" },
                                      capabilities: { ... } }}

>>> { jsonrpc: "2.0", id: 2, method: "tools/list", params: {} }

{ jsonrpc: "2.0", id: 2, result: { tools: [ { name: "list_issues",
                                                 description: "...",
                                                 inputSchema: { type: "object",
                                                               properties:
                                                               { ... } } },
                                               ... ] } }

>>> { jsonrpc: "2.0", id: 3, method: "tools/call",
      params: { name: "create_issue",
                arguments: { owner: "novafall", name: "novafall",
                             title: "t", body: "x", labels: ["p1-core"] } } }

{ jsonrpc: "2.0", id: 3, result: {
  isError: false,
  content: [ { type: "text", text: json_encode( { id: 42 } ) } ]
            } }
```

### Tool surface

| Tool                       | Purpose                              |
|----------------------------|--------------------------------------|
| `list_repos`               | List Forgejo repos (searchable, paginated) |
| `get_repo`                | Get full repo metadata             |
| `list_issues`             | List/issues of a repo              |
| `get_issue`               | Get a single issue                 |
| `get_pull_request`        | Get a single PR                    |
| `list_pull_requests`      | List pull requests                 |
| `get_pull_request_files`  | Get a PR's file diff               |
| `create_issue`            | Create an issue (with **name-or-id** `labels`) |
| `update_issue`            | Edit an existing issue             |
| `add_issue_comment`       | Add a comment to an issue/PR       |
| `add_pr_comment`          | Add a comment to a PR (delegates to the issue-comments plumbing) |
| `create_pull_request`     | Open a PR (from a branch)          |
| `get_branch`              | Read branch metadata               |
| `list_branches`           | List/compare branches              |
| `list_commits`            | List commits newest-first          |
| `get_commit`              | Read a commit by SHA (full or short; `stats` + `files`) |
| `create_label`            | Create a label                     |
| `list_milestones`         | List milestones of a repo          |
| `get_milestone`           | Get a single milestone (by id)     |
| `create_milestone`        | Create a milestone                 |
| `list_releases`           | List releases of a repo; also `get_release` |
| `list_file_tree`          | List/recurse files of a repo       |
| *(more)*                   |                                     |

**Milestone identifier (cross-tool convention).** All five surface tools that touch
milestones — `create_issue.milestone`, `update_issue.milestone_id`,
`list_milestones`, `get_milestone`, `create_milestone` — use the **global milestone
`id`**. The wire has no repository-local `number`; `list_milestones` is the only
source of valid `id` values. `create_issue.milestone` *looks* like a number (it's an
`int`) but is the same global id as `update_issue.milestone_id`.

| Tool | Field | Takes |
|------|-------|-------|
| `create_issue` | `milestone` | global milestone `id` (int) |
| `update_issue` | `milestone_id` | global milestone `id` (long) |
| `list_milestones` | returns | `id` per entry |
| `get_milestone` | `id` | global milestone `id` (long) |
| `create_milestone` | — | returns `id` |

### Resources

| Resource                              | Purpose                                          |
|---------------------------------------|--------------------------------------------------|
| `forgejo://instance`                 | Static resource reporting the server identity.   |
| `resources/templates/list`           | Repo metadata for `get_repo` — surfaced as a template. |

### Error contract

Errors are reported as JSON-RPC `{error:{code,...,message}}` payloads.
`code` is a lowered HTTP status (e.g. `not_found` for 404) or a stable
transport error name — clients can react deterministically.

## Notes

- **Retry**: transient failures (429/5xx, transport) auto-retry with exponential
  backoff (default 4 attempts); `Retry-After` headers are honored.
- **JSON on the wire** is always `snake_case` (Forgejo API contract), even
  though .NET prefers camelCase by default.
- **Logs go to stderr only** — stdout is reserved for the MCP protocol.

## Use the repo as a template for your own MCP server

The repository is designed to be a **starting point** for any new MCP server
in .NET 10:

1. Fork this repo → `Settings` → enable **Template Repo** (Is template field)
   so other users can clone it from a single click.
2. Rename the project in `src/ForgejoMcp/` on disk (change the loop name in
   `ForgejoMcp` to your target, e.g. `Mymcp` + update `Program.cs`'s
   `ServerName`, `ServerInfo.Title`, `ServerInstructions`).
3. Swap implementations in `ForgejoMcpToolSurface` / `ForgejoResources` (keep
   DI + `McpServerTool` attribute + REST query wiring — it stays identical).
4. Configuration: change the `Forgejo` section in `appsettings.json` +
   `ForgejoServerOptions` to your env (e.g. `MY_SERVICE_URL` / `TOKEN`) —
   `BuildClient()` is the only validation point.
5. Tests: `tests/ForgejoMcp.Tests` is the skeleton — `TestHttpHandler` stands
   in for the live REST endpoint, `ForgejoServerOptionsTests` shows how
   configuration is tested.
6. CI: `.github/workflows/ci.yml` + `.forgejo/workflows/ci.yml` are identical.
   Drop one if you only target GitHub *or* Forgejo.

You end up with a working, tested, CI-covered MCP server in .NET 10 — no
boilerplate.

## License

MIT — see [LICENSE](LICENSE).
