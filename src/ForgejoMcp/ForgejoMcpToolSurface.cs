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
    [Description("Creates a new issue in the given repository (POST /repos/{o}/{n}/issues). `title` is required; `assignees` (usernames) and `milestone` (number) must already exist on the instance. `labels` accepts either label ids (integers) or label names (strings): the surface resolves names to ids via `list_labels` before posting, because this Forgejo instance rejects label names with HTTP 422 (its create-issue endpoint expects numeric ids, not names). An unresolvable label — neither an existing name nor a parseable id — fails early with code `label_not_found` and a hint to call `list_labels`. Returns the created issue object. Requires write permission.")]
    public async Task<string> CreateIssue(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Issue title (required).")] string title,
        [Description("Issue body in Markdown (optional).")] string? body = null,
        [Description("Label names to attach (resolved to ids via list_labels), or label ids as integers. Each must already exist in the repository; an unknown name fails with `label_not_found`.")] string[]? labels = null,
        [Description("Usernames to assign the issue to; each must exist on the instance.")] string[]? assignees = null,
        [Description("Milestone number to attach, when the repository uses milestones.")] int? milestone = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            // Validation happens INSIDE the lambda so an invalid argument
            // produces the standard {"error":{...}} payload (CallAsync catches
            // ArgumentException) instead of a protocol-level exception.
            ArgumentException.ThrowIfNullOrWhiteSpace(title, nameof(title));

            // Resolve label names -> ids. Forgejo's create-issue endpoint
            // wants numeric ids (a 422 "cannot unmarshal string into ...
            // CreateIssueOption.labels of type int64" is returned for names),
            // so we normalize here. An entry that is already a bare integer
            // string is treated as an id directly; otherwise we look it up by
            // name in the repo's label set.
            long[] resolvedIds = Array.Empty<long>();
            if (labels is { Length: > 0 })
            {
                var ids = new List<long>(labels.Length);
                var names = new List<string>();
                foreach (var entry in labels)
                {
                    if (entry is null)
                        // Skip nulls rather than fail the whole request.
                        continue;
                    if (long.TryParse(entry, out var id))
                    {
                        ids.Add(id);
                    }
                    else
                    {
                        names.Add(entry);
                    }
                }

                // Only hit the API when there's at least one name to resolve;
                // a pure-id request needs no label lookup.
                if (names.Count > 0)
                {
                    var known = await _client.ListLabelsAsync(owner, name, cancellationToken).ConfigureAwait(false);
                    var byName = known
                        .Where(l => l is not null && l!.Name is not null)
                        .ToDictionary(l => l!.Name!, l => l!.Id, StringComparer.Ordinal);
                    foreach (var nameToResolve in names)
                    {
                        if (byName.TryGetValue(nameToResolve, out var resolved))
                        {
                            ids.Add(resolved);
                        }
                        else
                        {
                            // Machine-detectable code so an agent can branch on it
                            // (see issue #12's "validate early" requirement).
                            throw new ArgumentException(
                                "label_not_found: label '" + nameToResolve + "' does not exist on " +
                                owner + "/" + name + ". Call list_labels first to see the available names and ids.",
                                nameof(labels));
                        }
                    }
                }
                resolvedIds = ids.ToArray();
            }

            var request = new CreateIssueRequest
            {
                Title = title,
                Body = body,
                Labels = resolvedIds,
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

    /// <summary>
    /// Lists the repository's branches (backing:
    /// <c>GET /repos/{owner}/{name}/branches</c>). Each entry carries the
    /// branch name plus the tip commit id and the first line of the tip
    /// commit message — enough to see what each branch points at without
    /// another call.
    /// </summary>
    [McpServerTool(Name = "list_branches", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the branches of a repository (GET /repos/{o}/{n}/branches): each entry has `name`, `commit_id` (full SHA of the tip commit), `commit_message` (first line of the tip commit subject), and `commit_url`. Not paged — a branch list is small by nature. Errors with code `not_found` (HTTP 404) when the repo is missing or not visible to the token.")]
    public async Task<string> ListBranches(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            var branches = await _client.ListBranchesAsync(owner, name, cancellationToken).ConfigureAwait(false);
            return branches;
        }, cancellationToken);

    /// <summary>
    /// Gets a single branch (backing: <c>GET /repos/{o}/{n}/branches/{name}</c>).
    /// Returns the same shape as one entry of <c>list_branches</c>.
    /// </summary>
    [McpServerTool(Name = "get_branch", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Gets one branch (GET /repos/{o}/{n}/branches/{name}): `name`, `commit_id`, `commit_message` (first line of the tip commit subject), `commit_url`. Branch names may contain slashes (e.g. `feature/x`). Errors with code `not_found` (HTTP 404) when the branch or repo does not exist for this token.")]
    public async Task<string> GetBranch(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Branch name (e.g. `main`, `feature/x`).")] string branch,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(branch, nameof(branch));
            return await _client.GetBranchAsync(owner, name, branch, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Lists the repository's issue labels (backing:
    /// <c>GET /repos/{owner}/{name}/labels</c>). Each entry carries id, name,
    /// colour, description — the id is what <c>update_issue</c> needs for its
    /// `labels` / `add_label_ids` / `remove_label_ids` arguments.
    /// </summary>
    [McpServerTool(Name = "list_labels", Destructive = false, Idempotent = true, OpenWorld = true, ReadOnly = true)]
    [Description("Lists the labels of a repository (GET /repos/{o}/{n}/labels): each entry has `id`, `name`, `color`, `description`. Use `id` when mutating labels via `update_issue` (which takes label ids, not names). Not paged — a repo's label set is small by nature. Errors with code `not_found` (HTTP 404) when the repo is missing or not visible.")]
    public async Task<string> ListLabels(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            return await _client.ListLabelsAsync(owner, name, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Creates an issue label (backing: <c>POST /repos/{owner}/{name}/labels</c>).
    /// Idempotent by name: the MCP surface checks the existing label set first
    /// and, on collision, returns
    /// <c>{created:false, existing:{id,name,color,description}}</c> without
    /// creating a duplicate — covers both the case where the API would 201 a
    /// colliding name (this acceptance instance) and the case where the API
    /// 409s it (other Forgejo/Gitea flavours).
    /// </summary>
    [McpServerTool(Name = "create_label", Destructive = true, Idempotent = true, OpenWorld = true, ReadOnly = false)]
    [Description("Creates a label in the given repository (POST /repos/{o}/{n}/labels). `name` and `color` (#rrggbb hex) are required; `description` is optional. Idempotency by name: if a label with the same `name` already exists the tool returns `{created:false, existing:{id,name,color,description}}` without creating a duplicate, regardless of whether the instance would otherwise answer 201 or 409. On success returns `{created:true, label:{…}}` with the full label object. Errors with `not_found` (404) when the repo is missing.")]
    public async Task<string> CreateLabel(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Label name (required). Distinct per repository.")] string labelName,
        [Description("Label colour in #rrggbb hex (required).")] string color,
        [Description("Human-readable description (optional).")] string? description = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(labelName, nameof(labelName));
            ArgumentException.ThrowIfNullOrWhiteSpace(color, nameof(color));
            // Idempotency by name. The acceptance instance does not enforce
            // uniqueness on POST /labels (it answers 201 and creates a
            // duplicate), so we detect the collision ourselves by listing
            // first. On flavours that DO enforce uniqueness this path also
            // works — the pre-check finds the existing label before a 409.
            var existingLabels = await _client.ListLabelsAsync(owner, name, cancellationToken).ConfigureAwait(false);
            var existing = existingLabels.FirstOrDefault(l =>
                string.Equals(l?.Name, labelName, StringComparison.Ordinal));
            if (existing is not null)
            {
                return new CreateLabelResult
                {
                    Created = false,
                    Existing = new LabelView
                    {
                        Id = existing.Id,
                        Name = existing.Name,
                        Color = existing.Color,
                        Description = existing.Description,
                    },
                };
            }
            var req = new CreateLabelRequest
            {
                Name = labelName,
                Color = color,
                Description = description,
            };
            var created = await _client.CreateLabelAsync(owner, name, req, cancellationToken).ConfigureAwait(false);
            return new CreateLabelResult
            {
                Created = true,
                Existing = null,
                Label = new LabelView
                {
                    Id = created.Id,
                    Name = created.Name,
                    Color = created.Color,
                    Description = created.Description,
                },
            };
        }, cancellationToken);

    /// <summary>
    /// Updates an issue in a repository (backing:
    /// <c>PATCH /repos/{owner}/{name}/issues/{index}</c>).
    /// <em>PATCH</em> — not POST, not PUT: the acceptance instance rejects
    /// both POST and PUT with 405 and advertises <c>Allow: GET, PATCH,
    /// <c>DELETE</c>; PATCH returns 201. See the <c>update_issue</c> notes in
    /// the README's "Mutacje i idempotentność (Tier 2)" section.
    /// </summary>
    [McpServerTool(Name = "update_issue", Destructive = true, Idempotent = false, OpenWorld = true, ReadOnly = false)]
    [Description("Updates a repository issue (PATCH /repos/{o}/{n}/issues/{index}). Only the supplied fields are modified. `index` is the issue number shown in the UI — the same number `list_issues` returns, not a global id. Accepted fields: `title`, `body`, `state` (open|closed), `assignees` (list of logins), `clear_assignees` (explicit true to unassign — empty `assignees` without the flag is dropped rather than transmitted), `milestone_id`, `labels` (list of label IDs = replace the set), `add_label_ids` (list of IDs to add), `remove_label_ids` (list of IDs to remove). At least one field must be present. Errors with `not_found` (404) when the issue doesn't exist, `unauthorized` (401) / `forbidden` (403) for auth, and `http_405` if a future instance build rejects PATCH (fallback to POST/PUT is out of scope — this build's instance requires PATCH).")]
    public async Task<string> UpdateIssue(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Issue number within the repository (>= 1) — the same number `list_issues` shows in `number`.")] int index,
        [Description("New title (optional; omit to keep).")] string? title = null,
        [Description("New body in Markdown (optional; omit to keep).")] string? body = null,
        [Description("New state: `open` or `closed` (optional).")] string? state = null,
        [Description("New assignee logins (set semantics: the issue ends up with exactly these; omit / null to keep).")] string[]? assignees = null,
        [Description("Explicit opt-in to clear the assignees to zero. Without this flag, an empty `assignees` list is dropped rather than transmitted, to avoid accidental unassignment.")] bool clear_assignees = false,
        [Description("Milestone id to set (optional).")] long? milestone_id = null,
        [Description("Label ids to set (set semantics: the issue ends up with exactly these; caller must call `list_labels` first to obtain ids).")] long[]? labels = null,
        [Description("Label ids to ADD (incremental).")] long[]? add_label_ids = null,
        [Description("Label ids to REMOVE (incremental).")] long[]? remove_label_ids = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(owner, nameof(owner));
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
            if (index < 1)
                throw new ArgumentException("index must be >= 1.", nameof(index));
            if (state is not null && state.Length > 0
                && !state.Equals("open", StringComparison.Ordinal)
                && !state.Equals("closed", StringComparison.Ordinal))
                throw new ArgumentException("state must be 'open' or 'closed'.", nameof(state));
            if (clear_assignees && assignees is { Length: > 0 })
                throw new ArgumentException("clear_assignees=true cannot be combined with a non-empty assignees list.", nameof(clear_assignees));

            var req = new UpdateIssueRequest
            {
                Title = title,
                Body = body,
                State = state,
                Assignees = assignees ?? [],
                ClearAssignees = clear_assignees,
                MilestoneId = milestone_id,
                Labels = labels,
                AddLabelIds = add_label_ids,
                RemoveLabelIds = remove_label_ids,
            };
            return await _client.UpdateIssueAsync(owner, name, index, req, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Adds a Markdown comment to an issue (or PR) — backing:
    /// <c>POST /repos/{o}/{n}/issues/{index}/comments</c>.
    /// </summary>
    [McpServerTool(Name = "add_issue_comment", Destructive = true, Idempotent = false, OpenWorld = true, ReadOnly = false)]
    [Description("Adds a comment to an issue or PR (POST /repos/{o}/{n}/issues/{index}/comments). The wire field is `content` per the README's wording, and the acceptance instance expects the request body key to be `body` (422 `[Body]: Required` when we sent `content`) — this tool uses `content` at the tool boundary and sends it as `body` on the wire. Returns the created comment object with `id`, `user`, `created_at`, `html_url`.")]
    public async Task<string> AddIssueComment(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("Issue number within the repository (>= 1).")] int index,
        [Description("Comment body in Markdown (required).")] string content,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(content, nameof(content));
            var req = new AddIssueCommentRequest { Body = content };
            return await _client.AddIssueCommentAsync(owner, name, index, req, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Adds a Markdown comment to a <b>pull request</b>. Forgejo treats PRs as
    /// issues, so the only difference from <see cref="AddIssueComment"/> is that
    /// <c>index</c> is the PR number. This reuses the existing comment plumbing
    /// end-to-end (validation, the <c>CallAsync</c> envelope, the
    /// <c>content</c>→<c>body</c> wire mapping, the client call) by delegating
    /// straight to it — no duplicated logic.
    /// </summary>
    [McpServerTool(Name = "add_pr_comment", Destructive = true, Idempotent = false, OpenWorld = true, ReadOnly = false)]
    [Description("Adds a comment to a pull request (POST /repos/{o}/{n}/issues/{index}/comments) — PRs are treated as issues on Forgejo, so this is the same endpoint and wire as `add_issue_comment`; `index` is the PR number shown in the UI. The tool boundary takes `content` (Markdown), the wire sends it as `body` (this instance 422s the `content` key with `[Body]: Required`). Returns the created comment object with `id`, `user`, `created_at`, `html_url`.")]
    public Task<string> AddPrComment(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("PR number within the repository (>= 1).")] int index,
        [Description("Comment body in Markdown (required).")] string content,
        CancellationToken cancellationToken = default)
        => AddIssueComment(owner, name, index, content, cancellationToken);

    /// <summary>
    /// Creates a pull request (backing: <c>POST /repos/{o}/{n}/pulls</c>).
    /// Minimal surface — no labels/assignees/milestone selection, per the
    /// task scope. Returns the full created PR document.
    /// </summary>
    [McpServerTool(Name = "create_pull_request", Destructive = true, Idempotent = false, OpenWorld = true, ReadOnly = false)]
    [Description("Creates a pull request in the given repository (POST /repos/{o}/{n}/pulls). `title`, `base` (target branch), `head` (source branch or `owner:branch` for cross-repo) are required; `body` (Markdown) and `draft` (bool) are optional. No labels/assignees/milestone — the surface is deliberately minimal. On success returns the created PR's identity: `id`, `number`, `html_url`, `state`, `draft`, `title`, `base_ref`, `head_ref`, `created_at`. Missing branch: the acceptance instance answers 404 (some flavours 422) with its `errors[]` body — the result is an error envelope with code `not_found` / `http_422`, the server's message verbatim, and a hint to check both branches with `list_branches`.")]
    public async Task<string> CreatePullRequest(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string name,
        [Description("PR title (required).")] string title,
        [Description("Target branch name (required).")] string @base,
        [Description("Source branch — bare name for same-repo `base` or `owner:branch` for cross-repo (required).")] string head,
        [Description("PR body in Markdown (optional).")] string? body = null,
        [Description("Open as draft (optional, defaults to false on the wire).")] bool? draft = null,
        CancellationToken cancellationToken = default)
        => await CallAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title, "title");
            ArgumentException.ThrowIfNullOrWhiteSpace(@base, "base");
            ArgumentException.ThrowIfNullOrWhiteSpace(head, "head");
            var req = new CreatePullRequestRequest
            {
                Title = title,
                Body = body,
                Base = @base,
                Head = head,
                Draft = draft,
            };
            try
            {
                var created = await _client.CreatePullRequestAsync(owner, name, req, cancellationToken).ConfigureAwait(false);
                return new CreatePullRequestResult
                {
                    Id = created.Id,
                    Number = created.Number,
                    HtmlUrl = created.HtmlUrl,
                    State = created.State,
                    Draft = created.Draft,
                    Title = created.Title,
                    BaseRef = created.Base?.Ref,
                    HeadRef = created.Head?.Ref,
                    CreatedAt = created.CreatedAt,
                };
            }
            catch (ForgejoException e) when (e.StatusCode is 404 or 422)
            {
                // Missing-branch / missing-repo: rethrow with an actionable
                // hint so the standard error envelope (CallAsync → ErrorJson)
                // reports code `not_found` / `http_422` plus the server's
                // verbatim message and a concrete next step the agent can relay.
                var hint = e.StatusCode == 404
                    ? " The `head` or `base` branch does not exist on the instance — verify with list_branches before retrying, and check that `head` has at least one commit not on `base`."
                    : " The PR body/refs were rejected by the server — inspect the server response above and fix the offending field.";
                throw new ForgejoException(e.Message + hint, e.StatusCode, e.ErrorCode, e.ResponseBody, e);
            }
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
