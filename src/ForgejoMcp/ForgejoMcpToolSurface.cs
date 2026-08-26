using System.ComponentModel;
using Forgejo.Client;
using ModelContextProtocol.Server;

namespace Forgejo.Mcp;

/// <summary>
/// The MCP tool surface of the server: one method per Forgejo operation, all
/// wrapping <see cref="ForgejoClient"/>.
/// </summary>
/// <remarks>
/// Conventions for every tool method:
/// <list type="bullet">
/// <item>Parameters arrive as JSON from the MCP client; names must be
/// <c>snake_case</c> — that is what clients (mcp-inspector, LLM tooling)
/// emit and match against <c>inputSchema</c>.</item>
/// <item>On success the method returns a JSON string serialized with
/// <see cref="ForgejoJson.Default"/> (snake_case property names), so the tool
/// result matches the wire shape of the Forgejo API.</item>
/// <item>On failure the <c>call_tool</c> result carries
/// <c>isError=true</c> with a stable, machine-readable error string
/// (<c>{"error":{"code":...,"message":...}</c>).</item>
/// </list>
/// The hosting app constructs the client from configuration and passes it in
/// (see <c>Program.cs</c>); no tool reads configuration itself so the surface
/// stays unit-testable with fake handlers.
/// </remarks>
[McpServerToolType]
public sealed class ForgejoMcpToolSurface
{
    private readonly ForgejoClient _client;

    public ForgejoMcpToolSurface(ForgejoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Lists the repositories visible to the authenticated identity on the
    /// configured Forgejo instance (backing endpoint: <c>GET /user/repos</c>;
    /// requires an access token).
    /// </summary>
    /// <param name="page">1-based page number; defaults to 1.</param>
    /// <param name="limit">Items per page; the instance default (30) applies when omitted, capped at the instance maximum (50).</param>
    [McpServerTool(Name = "list_repos", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the repositories visible to the authenticated identity (GET /user/repos). Requires an access token. Returns an object with `items` (array of repository objects), `count`, `total` (when the instance reports it) and `next_page` (null when the last page was returned — see the Pagination section of the README).")]
    public async Task<string> ListRepos(
        [Description("1-based page number of the result page to fetch.")] int page = 1,
        [Description("Maximum number of repositories to return in this page (default 30; instance cap 50).")] int? limit = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(() =>
            _client.ListRepositoriesAsync(new Page { Number = page, Size = limit ?? 30 }, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Gets metadata for a single repository (backing: <c>GET
    /// /repos/{owner}/{name}</c>). The same document is available as the
    /// <c>forgejo://repo/{owner}/{name}</c> resource.
    /// </summary>
    [McpServerTool(Name = "get_repo", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Gets metadata for a single repository (GET /repos/{owner}/{name}): name, full_name, description, owner, default branch, clone URLs, visibility, language, and timestamps. Errors with HTTP 401/403/404 code when the repository is missing or not visible to the token.")]
    public async Task<string> GetRepo(
        [Description("Repository owner: GitHub-style login or organization.")] string owner,
        [Description("Repository name (without the owner).")] string name,
        CancellationToken cancellationToken = default)
        => await CallAsync(() => _client.GetRepositoryAsync(owner, name, cancellationToken), cancellationToken);

    /// <summary>
    /// Creates an issue in a repository (mutating; backing:
    /// <c>POST /repos/{owner}/{name}/issues</c>).
    /// </summary>
    [McpServerTool(Name = "create_issue", Destructive = true, Idempotent = false, OpenWorld = true, ReadOnly = false)]
    [Description("Creates a new issue in the given repository (POST /repos/{owner}/{name}/issues). `title` is required; `labels` and `assignees` must exist on the instance. Returns the created issue object. Requires write permission.")]
    public async Task<string> CreateIssue(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Issue title (required).")] string title,
        [Description("Issue body in Markdown (optional).")] string? body = null,
        [Description("Names of labels to attach; each must already exist in the repository.")] string[]? labels = null,
        [Description("Usernames to assign the issue to; each must exist on the instance.")] string[]? assignees = null,
        [Description("Milestone number to attach, when the repository uses milestones.")] int? milestone = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            // Validation happens INSIDE the lambda so an invalid argument
            // produces the standard {"error":{...}} payload (CallAsync catches
            // ArgumentException) instead of a protocol-level exception.
            ArgumentException.ThrowIfNullOrWhiteSpace(title, nameof(title));
            var request = new CreateIssueRequest
            {
                Title = title,
                Body = body,
                Labels = labels ?? Array.Empty<string>(),
                Assignees = assignees ?? Array.Empty<string>(),
                Milestone = milestone,
            };
            return await _client.CreateIssueAsync(owner, name, request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Lists issues of a repository (backing:
    /// <c>GET /repos/{owner}/{name}/issues</c>). Forgejo includes pull
    /// requests in the issue list; use <c>list_pull_requests</c> for the
    /// dedicated pulls endpoint.
    /// </summary>
    [McpServerTool(Name = "list_issues", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the issues of a repository (GET /repos/{o}/{n}/issues). Filter by `state` (open/closed/all), `assigned_by` and `label`. Note: Forgejo includes pull requests in the issue list; use list_pull_requests for the dedicated endpoint. Returns `items`, `count`, `total`, `next_page`.")]
    public async Task<string> ListIssues(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Issue state filter: `open`, `closed`, or `all` (default `open`).")] string state = "open",
        [Description("Filter issues assigned to these usernames.")] string[]? assigned_by = null,
        [Description("Filter issues with any of these label names.")] string[]? label = null,
        [Description("1-based page number of the result page to fetch.")] int page = 1,
        [Description("Maximum number of issues to return in this page (default 30; instance cap 50).")] int? limit = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(() => _client.ListIssuesAsync(
            owner, name,
            new IssueListOptions { State = state, AssignedBy = assigned_by ?? [], Labels = label ?? [] },
            new Page { Number = page, Size = limit ?? 30 }, cancellationToken), cancellationToken);

    /// <summary>
    /// Lists pull requests of a repository (backing:
    /// <c>GET /repos/{owner}/{name}/pulls</c>).
    /// </summary>
    [McpServerTool(Name = "list_pull_requests", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the pull requests of a repository (GET /repos/{o}/{n}/pulls). Filter by `state` (open/closed/all), `assigned_by` and `label`. Returns `items`, `count`, `total`, `next_page`.")]
    public async Task<string> ListPullRequests(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("PR issue-state filter: `open`, `closed`, or `all` (default `open`).")] string state = "open",
        [Description("Filter PRs assigned to these usernames.")] string[]? assigned_by = null,
        [Description("Filter PRs with any of these label names.")] string[]? label = null,
        [Description("1-based page number of the result page to fetch.")] int page = 1,
        [Description("Maximum number of pull requests to return in this page (default 30; instance cap 50).")] int? limit = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(() => _client.ListPullRequestsAsync(
            owner, name,
            new IssueListOptions { State = state, AssignedBy = assigned_by ?? [], Labels = label ?? [] },
            new Page { Number = page, Size = limit ?? 30 }, cancellationToken), cancellationToken);

    /// <summary>
    /// Reads the raw content of a file in a repository (backing:
    /// <c>GET /repos/{owner}/{name}/raw/{path}?ref=...</c>). Text files come
    /// back with the decoded string in <c>text</c>; binary payloads report
    /// <c>size</c> and <c>content_type</c> with <c>text</c> left empty.
    /// </summary>
    [McpServerTool(Name = "get_file", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Reads the raw content of a single file (GET /repos/{o}/{n}/raw/{path}). `branch` defaults to the repository's default branch. Returns `{path, size, content_type, text}` — `text` is populated when the payload decodes as UTF-8.")]
    public async Task<string> GetFile(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Path of the file in the repository, e.g. `README.md`.")] string path,
        [Description("Branch (or tag/SHA) to read from; the repository default branch when omitted.")] string? branch = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            var file = await _client.GetFileContentAsync(owner, name, path, branch, cancellationToken).ConfigureAwait(false);
            // `bytes` is intentionally out of the MCP payload — it would be
            // base64 bloat for a protocol whose content channel is text.
            return new
            {
                path = file.Path,
                size = file.Size,
                content_type = file.ContentType,
                text = file.Text,
            };
        }, cancellationToken);

    /// <summary>
    /// Lists commits of a repository (backing:
    /// <c>GET /repos/{owner}/{name}/commits</c>).
    /// </summary>
    [McpServerTool(Name = "list_commits", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the commits of a repository (GET /repos/{o}/{n}/commits), newest first. `branch` defaults to the repository's default branch. Returns `items` (commit objects: sha, commit.message/author/committer, author, committer), `count`, `total`, `next_page`.")]
    public async Task<string> ListCommits(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Branch to list commits of; the repository default branch when omitted.")] string? branch = null,
        [Description("1-based page number of the result page to fetch.")] int page = 1,
        [Description("Maximum number of commits to return in this page (default 30; instance cap 50).")] int? limit = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(() => _client.ListCommitsAsync(
            owner, name,
            new Page { Number = page, Size = limit ?? 30 },
            new CommitListOptions { Branch = branch },
            cancellationToken), cancellationToken);

    /// <summary>
    /// Gets a single issue by its repository index (backing:
    /// <c>GET /repos/{owner}/{name}/issues/{index}</c>). Forgejo's issue
    /// endpoint also returns pull requests, so this is the unified form.
    /// </summary>
    [McpServerTool(Name = "get_issue", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Gets one issue by number (GET /repos/{o}/{n}/issues/{index}): id, number, title, body, state, labels, milestone, assignees, created_at, updated_at, html_url. Errors with code `not_found` (HTTP 404) when the issue number does not exist.")]
    public Task<string> GetIssue(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Issue number within the repository (>= 1).")] int index,
        CancellationToken cancellationToken = default)
        => CallAsync(() => _client.GetIssueAsync(owner, name, index, cancellationToken), cancellationToken);

    /// <summary>
    /// Gets a single pull request (backing:
    /// <c>GET /repos/{owner}/{name}/pulls/{index}</c>).
    /// </summary>
    [McpServerTool(Name = "get_pull_request", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Gets one pull request by number (GET /repos/{o}/{n}/pulls/{index}): id, number, title, body, state, merged, merged_at, base/head refs, labels, milestone, assignees, comments count, timestamps, html_url. Errors with code `not_found` (HTTP 404) when the PR number does not exist.")]
    public Task<string> GetPullRequest(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Pull request number within the repository (>= 1).")] int index,
        CancellationToken cancellationToken = default)
        => CallAsync(() => _client.GetPullRequestAsync(owner, name, index, cancellationToken), cancellationToken);

    /// <summary>
    /// Lists the changed files of a pull request (backing:
    /// <c>GET /repos/{owner}/{name}/pulls/{index}/files</c>). With
    /// <c>show_diff</c> the combined unified patch is fetched in one extra
    /// call (<c>GET /repos/{owner}/{name}/pulls/{index}.diff</c>) and returned
    /// as the <c>diff</c> field — no N+1 per-file requests.
    /// </summary>
    [McpServerTool(Name = "get_pull_request_files", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the files changed by a pull request (GET /repos/{o}/{n}/pulls/{index}/files): each entry has filename, status, additions, deletions, changes, and patch (omitted when the instance omits per-file patches). Set `show_diff` to true to also receive the combined unified diff as text in the `diff` field (one extra round trip, fetched via .diff). Returns `{files, diff?}`. Errors with code `not_found` (HTTP 404) when the PR number does not exist.")]
    public async Task<string> GetPullRequestFiles(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Pull request number within the repository (>= 1).")] int index,
        [Description("Also fetch the combined unified diff as text in the `diff` field (one extra call to .diff). Defaults to false.")] bool show_diff = false,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            var files = await _client.GetPullRequestFilesAsync(owner, name, index, cancellationToken).ConfigureAwait(false);
            var diff = show_diff
                ? await _client.GetPullRequestDiffAsync(owner, name, index, cancellationToken).ConfigureAwait(false)
                : null;
            return new { files, diff };
        }, cancellationToken);

    /// <summary>
    /// Lists the releases of a repository (backing:
    /// <c>GET /repos/{owner}/{name}/releases</c>). Asset metadata only —
    /// bodies are never fetched.
    /// </summary>
    [McpServerTool(Name = "list_releases", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the releases of a repository (GET /repos/{o}/{n}/releases): each entry has tag_name, name, body, draft, prerelease, created_at, published_at, html_url, and assets (name, url, size only — asset bodies are never fetched). Paged: pass `page` and `limit`; continue with the returned `next_page` while it is non-null.")]
    public Task<string> ListReleases(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("1-based page number of the result page to fetch.")] int page = 1,
        [Description("Maximum number of releases to return in this page (default 30; instance cap 50).")] int? limit = null,
        CancellationToken cancellationToken = default)
        => CallAsync(() => _client.ListReleasesAsync(
            owner, name,
            new Page { Number = page, Size = limit ?? 30 }, cancellationToken), cancellationToken);

    /// <summary>
    /// Lists the contents of a repository directory (backing:
    /// <c>GET /repos/{owner}/{name}/contents/{path}</c>). The flat directory
    /// listing an agent needs to navigate a repo without guessing filenames.
    /// </summary>
    [McpServerTool(Name = "list_file_tree", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the entries of a repository directory (GET /repos/{o}/{n}/contents/{path}): each entry has name, type (file/dir/symlink), path, sha, size, html_url. `path` is the directory relative to the repository root (empty or omitted = root); this is the file-browser endpoint, not a recursive tree. `branch` defaults to the repository's default branch. Returns `{path, entries}`.")]
    public async Task<string> ListFileTree(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Directory path relative to the repository root (empty = root).")] string? path = null,
        [Description("Branch (or tag/SHA) to list from; the repository default branch when omitted.")] string? branch = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            var entries = await _client.ListFileTreeAsync(owner, name, path, branch, cancellationToken).ConfigureAwait(false);
            return new
            {
                path = (path ?? string.Empty).TrimStart('/'),
                entries,
            };
        }, cancellationToken);

    // ------------------------------------------------------------------
    // Error handling: surface a stable, machine-readable error payload
    // instead of throwing (a throw yields a protocol error, which is a
    // different class of failure from "the API told us the object does
    // not exist").
    // ------------------------------------------------------------------

    private static async Task<string> CallAsync<T>(Func<Task<T>> apiCall, CancellationToken cancellationToken)
    {
        try
        {
            var value = await apiCall().ConfigureAwait(false);
            return ForgejoJson.ToJson(value);
        }
        catch (ForgejoException e)
        {
            return ErrorJson(e);
        }
        catch (ArgumentException e)
        {
            return ErrorJson("invalid_request", "Invalid tool input: " + e.Message);
        }
    }

    private static string ErrorJson(string code, string message)
        => ForgejoJson.ToJson(new { error = new { code, message } });

    private static string ErrorJson(ForgejoException e)
    {
        var code = e.StatusCode switch
        {
            null => "transport_error",
            401 => "unauthorized",
            403 => "forbidden",
            404 => "not_found",
            409 => "conflict",
            _ => $"http_{e.StatusCode}",
        };
        var message = $"Forgejo API error: {e.Message}" +
                      (string.IsNullOrEmpty(e.ErrorCode) ? "" : $" (server code: {e.ErrorCode})") +
                      (e.ResponseBody is { Length: > 0 } body ? $"\nServer response: {body.Trim()}" : "");
        return ForgejoJson.ToJson(new { error = new { code, message } });
    }
}
