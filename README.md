# Forgejo MCP Server

MCP (Model Context Protocol) server for **Forgejo** (Gitea-compatible) — a
self-hosted git host. Built in .NET / C# on the [`ModelContextProtocol`
2.2.0](https://www.nuget.org/packages/ModelContextProtocol) SDK (.NET 10).

The server exposes Forgejo operations as MCP **tools** and **resources**, so
any MCP client (e.g. IDE agents, Hermes) can drive a Forgejo instance via
simple text prompts.

Current release: **v3.1.1** — 31 named tools, 198 tests, self-contained
`linux-x64` release packages.

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
├── .github/workflows/ci.yml     # CI (GitHub Actions)
├── .github/workflows/build.yml  # CI: build + release packaging
├── .forgejo/workflows/ci.yml    # CI (Forgejo Actions) — same file
├── .forgejo/workflows/build.yml # CI: build + release packaging
├── scripts/
│   ├── mcp_smoke.py             # MCP stdio smoke-test driver
│   ├── package.sh               # build self-contained release package + sha256
│   └── publish_release.sh       # create + publish the Forgejo release from a package
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

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`) — to build and run from source.
- A **Forgejo / Gitea instance** plus one of two auth options:
  - a generated *access token* (PAT): Settings → Authorization → Access
    Tokens → `+`, or
  - username + password (HTTP basic) — both work; token is preferred.

## Running from source

```bash
# 1. Build
dotnet build

# 2. Run directly (stdio on stdin/stdout)
dotnet run --project src/ForgejoMcp

# 3. Publish a standalone self-contained binary (linux-x64)
dotnet publish src/ForgejoMcp/ForgejoMcp.csproj -c Release -r linux-x64 \
  --self-contained true --output publish/

# 4. Run the test suite (177 tests)
dotnet test
```

## Installing the release build

Releases are cut as self-contained `linux-x64` packages (binary + .NET
runtime DLLs) with a `.sha256` sidecar, e.g.

```
forgejo-mcp-server-20260916-linux-x64.tar.gz
forgejo-mcp-server-20260916-linux-x64.tar.gz.sha256
```

Download from the [Releases](../../releases) page, verify the checksum,
extract, and point your MCP client at the `forgejo-mcp-server` binary (stdout
is the MCP protocol — wire it as a stdio server). Configuration comes from the
environment or `appsettings.json` alongside the binary.

### Environment variables

| Variable          | Purpose                                            |
|-------------------|----------------------------------------------------|
| `FORGEJO_URL`     | API base, e.g. `https://git.example.com/api/v1`    |
| `FORGEJO_TOKEN`   | Personal access token (preferred)                  |
| `FORGEJO_USERNAME`/`FORGEJO_PASSWORD` | Basic auth (alternative to token) |
| `SSL_CERT_FILE`   | Custom CA bundle for private/TLS instances         |

## MCP protocol (brief)

The server speaks MCP v2024-11-05 over **stdio** (line-delimited, JSON-RPC
2.0). A sample client exchange for `create_issue`:

```
>>> { jsonrpc: "2.0", id: 1, method: "initialize",
      params: { protocolVersion: "2024-11-05",
                capabilities: {},
                clientInfo: { name: "my-agent", version: "1.0" } }}

{ jsonrpc: "2.0", id: 1, result: { protocolVersion: "2024-11-05",
                                      serverInfo: { name: "forgejo-mcp-server",
                                                    version: "3.1.0" },
                                      capabilities: { ... } }}

>>> { jsonrpc: "2.0", id: 2, method: "tools/list", params: {} }

{ jsonrpc: "2.0", id: 2, result: { tools: [ { name: "list_issues",
                                                 description: "...",
                                                 inputSchema: { ... } },
                                               ... ] } }

>>> { jsonrpc: "2.0", id: 3, method: "tools/call",
      params: { name: "create_issue",
                arguments: { owner: "dark-eternity", name: "novafall",
                             title: "t", body: "x", labels: ["p1-core"] } } }

{ jsonrpc: "2.0", id: 3, result: {
  isError: false,
  content: [ { type: "text", text: "{\"number\": 42}" } ]
}}
```

### Tool surface (v3.1.0)

| Tool                       | Purpose                                  |
|----------------------------|------------------------------------------|
| `list_repos`               | List repos visible to the authenticated user |
| `get_repo`                 | Full repo metadata                       |
| `create_issue`             | Create an issue (with **name-or-id** `labels`) |
| `list_issues`              | List issues of a repo (filterable, paginated) |
| `get_issue`                | Get a single issue (by number)           |
| `update_issue`             | Edit an existing issue                   |
| `add_issue_comment`        | Add a comment to an issue                |
| `add_pr_comment`           | Add a comment to a PR (issue-comments plumbing) |
| `list_pull_requests`       | List PRs (state-filterable)              |
| `get_pull_request`         | Get one PR (by index)                    |
| `get_pull_request_files`   | Get a PR's changed files / diff          |
| `create_pull_request`      | Open a PR (branch → base)                |
| `close_pull_request`       | Close an open PR                         |
| `merge_pull_request`       | Merge a PR (`accept_type`; editor check) |
| `list_commits`             | List commits newest-first (`since`/`until`) |
| `get_commit`               | Commit by SHA (metadata, optional patch) |
| `list_branches`            | List branches                            |
| `get_branch`               | Branch metadata (HEAD, protection)       |
| `list_labels`              | List labels of a repo                    |
| `create_label`             | Create a label                           |
| `list_milestones`          | List milestones of a repo                |
| `get_milestone`            | Get one milestone (by global id)         |
| `create_milestone`         | Create a milestone                       |
| `list_releases`            | List releases; per-release metadata + assets |
| `search_repos`             | Search repos instance-wide (cross-org; `list_repos` is user-scoped) — v3.2 |
| `search_code`              | Search code (`search_disabled` on instances without the code index) — v3.2 |
| `get_file`                 | Raw file content at a ref                |
| `get_file_contents`        | Structured contents response (json/base64, + sha) |
| `list_file_tree`           | List / recurse the file tree             |
| `edit_file`                | Create or update a file (`git_sha` preconditions) |
| `delete_file`              | Delete a file (with message, optional sha) |

**Label contract (`create_issue.labels`).** Accepts **names or ids**:
numeric strings are passed through as label ids; anything else is resolved
via `list_labels`. Unknown names → typed `label_not_found` error.

**Milestone identifier (cross-tool convention).** All milestone-touching
tools — `create_issue.milestone`, `update_issue.milestone_id`,
`list_milestones`, `get_milestone`, `create_milestone` — use the **global
milestone `id`** (there is no repository-local `number` on the wire).
`list_milestones` is the source of valid ids.

**File-write contract (`edit_file` / `delete_file`).** Pass the `git_sha`
returned by `get_file_contents` as a precondition; a stale sha → typed
`precondition_failed` error (no blind overwrites).

### Resources

| Resource                              | Purpose                                    |
|---------------------------------------|--------------------------------------------|
| `forgejo://instance`                  | Static resource reporting server identity. |
| `forgejo://repo/{owner}/{name}`       | Repo metadata template (matches `get_repo`). |

### Error contract

Errors are reported as JSON-RPC `{error:{code,...,message}}` payloads.
`code` is a lowered HTTP status (e.g. `not_found` for 404) or a stable
domain error name (`label_not_found`, `precondition_failed`, `no_fields`,
`branch_exists`, `search_disabled`…) — clients can react deterministically.
`search_disabled` is the capability-level signal for the search tools:
the instance does not serve the route (403/404), so the caller should not
retry and should fall back to the `list_*` tools.

## Notes

- **Retry:** transient failures (429/5xx, transport) auto-retry with
  exponential backoff (default 4 attempts); `Retry-After` is honored.
- **JSON on the wire** is always `snake_case` (Forgejo API contract), even
  though .NET prefers camelCase by default.
- **Logs go to stderr only** — stdout is reserved for the MCP protocol.
- **Versioning:** the version is defined in two places and must be kept in
  sync — `src/ForgejoMcp/ForgejoMcp.csproj` (`<Version>`) and `Program.cs`
  (`ServerVersion`). `--version` reports the latter and must match the
  release tag.

## Roadmap

The backlog lives in
[issue #16](../../issues/16) (`[Roadmap] v3.1+ feature candidates`); the
**v3.2** candidate set (search, label writes, `create_release`, repo
creation, `create_branch`, org-scoped listing) is tracked as issues
#38–#43.

## Use the repo as a template for your own MCP server

The repository is designed to be a **starting point** for any new MCP server
in .NET 10:

1. Fork this repo → `Settings` → enable **Template Repo** (Is template field)
   so others can clone it from a single click.
2. Rename the project under `src/ForgejoMcp/` on disk (rename the folder +
   csproj, update `Program.cs`'s `ServerName`, `ServerInfo.Name`, and
   `ServerInstructions`).
3. Swap implementations in `ForgejoMcpToolSurface` / `ForgejoResources` (keep
   DI + the `McpServerTool` attribute + REST query wiring — it stays
   identical).
4. Configuration: change the `Forgejo` section in `appsettings.json` +
   `ForgejoServerOptions` to your env (e.g. `MY_SERVICE_URL` / `TOKEN`) —
   `BuildClient()` is the only validation point.
5. Tests: `tests/ForgejoMcp.Tests` is the skeleton — `TestHttpHandler` stands
   in for the live REST endpoint, `ForgejoServerOptionsTests` shows how
   configuration is tested.
6. CI: `.github/workflows/*.yml` and `.forgejo/workflows/*.yml` are paired.
   Drop the set you don't target.

You end up with a working, tested, CI-covered MCP server in .NET 10 — no
boilerplate.

## License

MIT — see [LICENSE](LICENSE).
