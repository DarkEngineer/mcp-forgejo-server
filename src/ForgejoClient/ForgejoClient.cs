namespace Forgejo.Client;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// A typed, retrying HTTP client for the Forgejo REST API (v1).
/// </summary>
/// <remarks>
/// Behaviour:
/// <list type="bullet">
/// <item>Auth: access token (<c>Authorization: token …</c>) or HTTP basic, per
/// <see cref="ForgejoCredentials"/>.</item>
/// <item>Retries: transient failures (HTTP 429, HTTP 5xx, transport errors) are
/// retried up to <c>maxRetries</c> times with exponential backoff; a
/// <c>Retry-After</c> response header (delta or date) takes precedence as the
/// wait for that round.</item>
/// <item>Errors: non-2xx responses (after the retry budget) and transport
/// failures surface as <see cref="ForgejoException"/> carrying status + body.</item>
/// </list>
/// Tests and advanced callers may inject a pre-configured <see cref="HttpClient"/>
/// (e.g. over a fake <see cref="HttpMessageHandler"/>); the client then does not
/// own it and will not dispose it.
/// </remarks>
public sealed class ForgejoClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _baseUrl;
    private readonly ForgejoCredentials _credentials;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;

    /// <summary>Maximum number of retry attempts after the first request.</summary>
    public int MaxRetries { get; }

    /// <summary>The Forgejo instance this client talks to.</summary>
    public Uri BaseUrl => _baseUrl;

    /// <summary>The credentials in use.</summary>
    public ForgejoCredentials Credentials => _credentials;

    /// <summary>
    /// Creates a client for the Forgejo instance at <paramref name="baseUrl"/>.
    /// </summary>
    /// <param name="baseUrl">Instance root, e.g. <c>https://git.home.internal</c> (a trailing slash is tolerated).</param>
    /// <param name="credentials">Auth material; <see cref="ForgejoCredentials.Anonymous"/> for public data.</param>
    /// <param name="maxRetries">
    /// Retries after the first attempt for transient failures (default 3 ⇒ 4 total attempts).
    /// </param>
    /// <param name="retryBaseDelay">
    /// Base backoff delay: attempt n waits <c>retryBaseDelay * 2^n</c> (default 1s).
    /// <see cref="TimeSpan.Zero"/> disables artificial delays (tests).
    /// </param>
    /// <param name="httpClient">Optional pre-configured HTTP client; when omitted the client creates and owns one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl"/> or <paramref name="credentials"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetries"/> &lt; 0 or <paramref name="retryBaseDelay"/> &lt; 0.</exception>
    public ForgejoClient(
        Uri baseUrl,
        ForgejoCredentials credentials,
        int maxRetries = 3,
        TimeSpan? retryBaseDelay = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(credentials);
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "maxRetries must be >= 0.");
        var baseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryBaseDelay), "retryBaseDelay must be >= TimeSpan.Zero.");

        _baseUrl = NormalizeApiRoot(baseUrl);
        _credentials = credentials;
        _maxRetries = maxRetries;
        _retryBaseDelay = baseDelay;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        _ownsHttp = httpClient is null;
        MaxRetries = maxRetries;
    }

    // ---------------------------------------------------------------------------
    // Public endpoint surface (wrapped by the MCP tools)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Lists the repositories visible to the authenticated user — the backing
    /// endpoint of the <c>list_repos</c> MCP tool.
    /// </summary>
    /// <remarks>
    /// Backing call: <c>GET /user/repos</c> (requires a token; unauthenticated
    /// calls fail with 401). The response carries a <c>total_count</c> header
    /// which is surfaced as <see cref="ListResult{T}.Total"/>.
    /// </remarks>
    public Task<ListResult<Repository>> ListRepositoriesAsync(
        Page? page = default,
        CancellationToken ct = default)
    {
        page ??= new Page();
        return SendPagedAsync<Repository>("/user/repos", page.ToQuery(), page, null, ct);
    }

    /// <summary>
    /// Gets a single repository by owner and name — backing of <c>get_repo</c>.
    /// Errors: 404 (not found / no permission), 401 (missing auth), 403 (forbidden).
    /// </summary>
    public Task<Repository> GetRepositoryAsync(string owner, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SendAsync<Repository>(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}", ct: ct);
    }

    /// <summary>
    /// Creates an issue in <c>owner/name</c> from <paramref name="req"/> — backing of
    /// <c>create_issue</c> (a mutating operation; see <see cref="CreateIssueRequest"/> for
    /// the request shape, including the required <c>Title</c> field).
    /// </summary>
    /// <remarks>Back: <c>POST /repos/{owner}/{name}/issues</c>. Requires write permission.</remarks>
    public Task<Issue> CreateIssueAsync(string owner, string name, CreateIssueRequest req, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ArgumentException("CreateIssueRequest.Title must not be empty.", nameof(req));
        return SendAsync<Issue>(HttpMethod.Post, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues", req, ct);
    }

    /// <summary>
    /// Lists the issues of <c>owner/name</c> — backing of <c>list_issues</c>.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{r}/issues</c>. Pull requests are included by
    /// default in Forgejo's issue list; the client surface exposes them via
    /// <c>list_pull_requests</c> as the dedicated endpoint.</remarks>
    public Task<ListResult<Issue>> ListIssuesAsync(
        string owner,
        string name,
        IssueListOptions? filters = default,
        Page? page = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        filters ??= new IssueListOptions();
        page ??= new Page();
        var q = filters.ToQuery();
        var full = q.Length == 0 ? page.ToQuery() : $"{q}&{page.ToQuery()}";
        return SendPagedAsync<Issue>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues", full, page, null, ct);
    }

    /// <summary>
    /// Lists the pull requests of <c>owner/name</c> — backing of <c>list_pull_requests</c>.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{r}/pulls</c>.</remarks>
    public Task<ListResult<PullRequest>> ListPullRequestsAsync(
        string owner,
        string name,
        IssueListOptions? filters = default,
        Page? page = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        filters ??= new IssueListOptions();
        page ??= new Page();
        var q = filters.ToQuery();
        var full = q.Length == 0 ? page.ToQuery() : $"{q}&{page.ToQuery()}";
        return SendPagedAsync<PullRequest>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls", full, page, null, ct);
    }

    /// <summary>
    /// Reads the <em>raw</em> content of a file at <paramref name="path"/> on
    /// <paramref name="branch"/> — backing of <c>get_file</c>.
    /// </summary>
    /// <remarks>
    /// Back: <c>GET /repos/{o}/{r}/raw/{path}?ref={branch}</c> (defaults to the
    /// repository's default branch when <paramref name="branch"/> is null).
    /// The payload may be any binary; <see cref="FileContent.Text"/> is only set
    /// when the bytes are valid UTF-8.
    /// </remarks>
    public Task<FileContent> GetFileContentAsync(
        string owner,
        string name,
        string path,
        string? branch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var relative = path.StartsWith('/') ? path[1..] : path;
        var qs = branch is null ? string.Empty : $"?ref={Uri.EscapeDataString(branch)}";
        return SendRawAsync(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/raw/{relative}{qs}", ct)
            .ContinueWith(t =>
            {
                var (bytes, contentType) = t.GetAwaiter().GetResult();
                string? text = null;
                if (IsLikelyText(bytes, contentType))
                    text = Encoding.UTF8.GetString(bytes);
                return new FileContent { Path = path, Bytes = bytes, Text = text ?? string.Empty, ContentType = contentType, Size = bytes.Length };
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Lists commits of <c>owner/name</c> on <paramref name="options.Branch"/> (or the
    /// default branch) — backing of <c>list_commits</c>.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{r}/commits</c>.</remarks>
    public Task<ListResult<RepositoryCommit>> ListCommitsAsync(
        string owner,
        string name,
        Page? page = default,
        CommitListOptions? options = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        page ??= new Page();
        options ??= new CommitListOptions();
        var b = string.IsNullOrEmpty(options.Branch) ? "" : $"&branch={Uri.EscapeDataString(options.Branch)}";
        return SendPagedAsync<RepositoryCommit>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commits?{page.ToQuery()}{b}", null, page, null, ct);
    }

    /// <summary>
    /// Gets a single issue (or pull request, in Forgejo's model) — backing of
    /// <c>get_issue</c>. Errors: 404 (not found / no permission).
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{n}/issues/{index}</c>.</remarks>
    public Task<Issue> GetIssueAsync(string owner, string name, int index, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (index < 1)
            throw new ArgumentOutOfRangeException(nameof(index), index, "issue index must be >= 1.");
        return SendAsync<Issue>(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{index}", ct: ct);
    }

    /// <summary>
    /// Gets a single pull request — backing of <c>get_pull_request</c>.
    /// Errors: 404 (not found / no permission).
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{n}/pulls/{index}</c>.</remarks>
    public Task<PullRequest> GetPullRequestAsync(string owner, string name, int index, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (index < 1)
            throw new ArgumentOutOfRangeException(nameof(index), index, "pull request index must be >= 1.");
        return SendAsync<PullRequest>(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{index}", ct: ct);
    }

    /// <summary>
    /// Lists the changed files of a pull request with per-file churn stats —
    /// backing of <c>get_pull_request_files</c>. Per-file <c>patch</c> is set
    /// only when the instance embeds it (the acceptance instance omits it).
    /// Errors: 404 (not found / no permission).
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{n}/pulls/{index}/files</c>.</remarks>
    public async Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string name, int index, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (index < 1)
            throw new ArgumentOutOfRangeException(nameof(index), index, "pull request index must be >= 1.");
        var json = await SendAsyncCore(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{index}/files", null, null, ct).ConfigureAwait(false);
        return ForgejoJson.FromJson<IReadOnlyList<PullRequestFile>>(json)
            ?? throw new ForgejoException($"PR files response for {PathOnly($"/repos/{owner}/{name}/pulls/{index}/files")} was not a JSON array.");
    }

    /// <summary>
    /// Fetches the combined unified diff of a pull request as raw text —
    /// the single-round-trip alternative to per-file patches. Backs the
    /// <c>diff</c> output of <c>get_pull_request_files</c>.
    /// </summary>
    /// <remarks>
    /// Back: <c>GET /repos/{o}/{n}/pulls/{index}.diff</c> (Forgejo/Gitea).
    /// Returns the raw unified-patch body; 404 if the PR is missing.
    /// </remarks>
    public async Task<string> GetPullRequestDiffAsync(string owner, string name, int index, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (index < 1)
            throw new ArgumentOutOfRangeException(nameof(index), index, "pull request index must be >= 1.");
        return await SendTextAsync(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{index}.diff", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists repository releases (asset metadata only; bodies are not
    /// fetched) — backing of <c>list_releases</c>. Errors: 404.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{n}/releases</c>; paged.</remarks>
    public Task<ListResult<Release>> ListReleasesAsync(
        string owner, string name, Page? page = default, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        page ??= new Page();
        return SendPagedAsync<Release>($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/releases", page.ToQuery(), page, null, ct);
    }

    /// <summary>
    /// Lists the contents of a directory (or the repository root) — backing
    /// of <c>list_file_tree</c>. Each entry carries name/type/path/sha/size.
    /// </summary>
    /// <remarks>
    /// Back: <c>GET /repos/{o}/{n}/contents/{path}</c> (empty path = root).
    /// Returns the single-page array — repository directories are flat in a
    /// single Gitea/Forgejo response, so no page cursor.
    /// </remarks>
    public async Task<IReadOnlyList<ContentEntry>> ListFileTreeAsync(
        string owner, string name, string? path = null, string? branch = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var rel = (path ?? string.Empty).TrimStart('/');
        var qs = branch is null ? "" : $"?ref={Uri.EscapeDataString(branch)}";
        var p = $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/contents/{rel}{qs}";
        var json = await SendAsyncCore(HttpMethod.Get, p, null, null, ct).ConfigureAwait(false);
        return ForgejoJson.FromJson<IReadOnlyList<ContentEntry>>(json)
            ?? throw new ForgejoException($"Contents response for {PathOnly(p)} was not a JSON array.");
    }

    /// <summary>
    /// Lists the repository's branches — backing of <c>list_branches</c>.
    /// Each entry carries the branch name plus tip-commit id/message (subject
    /// line only) so an agent can navigate without a second call.
    /// </summary>
    /// <remarks>
    /// Back: <c>GET /repos/{o}/{n}/branches</c>(?limit=100). The endpoint is
    /// not paged on the API (a single array is returned), so this returns a
    /// plain list, not a <c>ListResult</c> envelope. Errors: 404 (repo not
    /// found / no permission).
    /// </remarks>
    public async Task<IReadOnlyList<Branch>> ListBranchesAsync(
        string owner, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var json = await SendAsyncCore(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/branches?limit=100", null, null, ct).ConfigureAwait(false);
        var raw = ForgejoJson.FromJson<IReadOnlyList<BranchWire>>(json)
            ?? throw new ForgejoException($"{PathOnly($"/repos/{owner}/{name}/branches")} response was not a JSON array.");
        return raw.Select(ToBranch).ToList();
    }

    /// <summary>
    /// Gets a single branch — backing of <c>get_branch</c>.
    /// Errors: 404 (branch / repo not found or no permission).
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{n}/branches/{name}</c>. Branch names
    /// may contain slashes (e.g. <c>feature/x</c>); only the first segment is
    /// split into the path, the remainder is re-escaped in place.</remarks>
    public async Task<Branch> GetBranchAsync(string owner, string name, string branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        var clean = branch.TrimStart('/');
        var json = await SendAsyncCore(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/branches/{Uri.EscapeDataString(clean)}", null, null, ct).ConfigureAwait(false);
        var wire = ForgejoJson.FromJson<BranchWire>(json)
            ?? throw new ForgejoException($"{PathOnly($"/repos/{owner}/{name}/branches/{branch}")} response was not an object.");
        return ToBranch(wire);
    }

    private static Branch ToBranch(BranchWire w)
    {
        var message = string.IsNullOrEmpty(w.Commit?.Message)
            ? null
            : (w.Commit.Message.Split('\n')[0].TrimEnd('\r').Trim());
        return new Branch
        {
            Name = w.Name,
            CommitId = w.Commit?.Id ?? string.Empty,
            CommitMessage = string.IsNullOrEmpty(message) ? null : message,
            CommitUrl = w.Commit?.Url,
        };
    }

    /// <summary>
    /// Lists the repository's issue labels — backing of <c>list_labels</c>.
    /// Returns id, name, colour, description.
    /// </summary>
    /// <remarks>Back: <c>GET /repos/{o}/{n}/labels</c>. Single-array, not paged
    /// (a repository's label set is small); a plain list is returned. Errors: 404.</remarks>
    public async Task<IReadOnlyList<Label>> ListLabelsAsync(
        string owner, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var json = await SendAsyncCore(HttpMethod.Get, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/labels", null, null, ct).ConfigureAwait(false);
        return ForgejoJson.FromJson<IReadOnlyList<Label>>(json)
            ?? throw new ForgejoException($"{PathOnly($"/repos/{owner}/{name}/labels")} response was not a JSON array.");
    }

    /// <summary>
    /// Creates an issue label — backing of <c>create_label</c> (mutation).
    /// </summary>
    /// <remarks>
    /// Back: <c>POST /repos/{o}/{n}/labels</c> (201 on success). Some
    /// Forgejo/Gitea flavours answer a duplicate name with <c>409</c>; others
    /// (including the acceptance instance) allow duplicate names and would
    /// otherwise return 201 — so the idempotency guarantee lives on the MCP
    /// surface (dedupe against <see cref="ListLabelsAsync"/> before POST),
    /// not here. Errors: 404 (repo), 409 (name collision, flavour-dependent).
    /// </remarks>
    public Task<Label> CreateLabelAsync(string owner, string name, CreateLabelRequest req, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("CreateLabelRequest.Name must not be empty.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Color))
            throw new ArgumentException("CreateLabelRequest.Color must not be empty (use a #rrggbb hex colour).", nameof(req));
        return SendAsync<Label>(HttpMethod.Post, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/labels", req, ct);
    }

    /// <summary>
    /// Updates an issue — backing of <c>update_issue</c> (mutation).
    /// </summary>
    /// <remarks>
    /// Back: <c>PATCH /repos/{o}/{n}/issues/{index}</c>. Verb discovery on the
    /// acceptance instance (live, 2026-08-26): <c>POST</c> and <c>PUT</c> both
    /// return <c>405 Method Not Allowed</c> with <c>Allow: GET, PATCH,
    /// DELETE</c>; <c>PATCH</c> returns <c>201</c> with the updated issue.
    /// This Gitea-compatible build therefore uses PATCH — not POST, not PUT —
    /// and the MCP <c>update_issue</c> tool documents that choice.
    /// The <see cref="UpdateIssueRequest"/> payload is sparse: only the
    /// supplied fields change. Label mutation is by <em>id</em>
    /// (<c>labels</c> = replace the set, <c>add_label_ids</c> /
    /// <c>remove_label_ids</c> = incremental). Assignees are logins; an empty
    /// <c>assignees</c> list is only sent when the caller explicitly cleared
    /// them (the MCP surface enforces that opt-in). Errors: 404, 422 (bad
    /// state/assignee/label).
    /// </remarks>
    public Task<Issue> UpdateIssueAsync(string owner, string name, int index, UpdateIssueRequest req, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(req);
        if (index < 1)
            throw new ArgumentOutOfRangeException(nameof(index), index, "issue number must be >= 1.");

        // Build a sparse wire body: only present (non-null / non-empty) fields
        // are sent, so an update to just `state` does not accidentally clear
        // the title or reset labels. `ClearAssignees` is a client-side opt-in
        // flag and never goes on the wire.
        var wire = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(req.Title))
            wire["title"] = req.Title;
        if (req.Body is not null)
            wire["body"] = req.Body;
        if (!string.IsNullOrWhiteSpace(req.State))
            wire["state"] = req.State;
        if (req.Assignees is { Count: > 0 })
            wire["assignees"] = req.Assignees;
        else if (req.Assignees is { Count: 0 } && req.ClearAssignees)
            wire["assignees"] = System.Array.Empty<string>();
        if (req.MilestoneId.HasValue)
            wire["milestone_id"] = req.MilestoneId.Value;
        if (req.Labels is not null)
            wire["labels"] = req.Labels;
        if (req.AddLabelIds is { Count: > 0 })
            wire["add_label_ids"] = req.AddLabelIds;
        if (req.RemoveLabelIds is { Count: > 0 })
            wire["remove_label_ids"] = req.RemoveLabelIds;
        if (wire.Count == 0)
            throw new ArgumentException(
                "UpdateIssueRequest has no mutable field set. Provide at least one of: title, body, state, assignees (with clear_assignees=true to unassign), milestone_id, labels, add_label_ids, remove_label_ids.",
                nameof(req));

        return SendAsync<Issue>(HttpMethod.Patch, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{index}", wire, ct);
    }

    /// <summary>
   /// Adds a comment to an issue (or PR) — backing of <c>add_issue_comment</c>
    /// (mutation).
    /// </summary>
    /// <remarks>
    /// Back: <c>POST /repos/{o}/{n}/issues/{index}/comments</c> (201). The
    /// wire field is <c>body</c> (Markdown) — the acceptance instance
    /// rejects <c>{"content": …}</c> with <c>422 [Body]: Required</c>, so
    /// <see cref="AddIssueCommentRequest"/> carries <c>Body</c>. Returns the
    /// created comment (id, user, created_at, html_url). Errors: 404 (issue).
    /// </remarks>
    public Task<IssueComment> AddIssueCommentAsync(string owner, string name, int index, AddIssueCommentRequest req, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(req);
        if (index < 1)
            throw new ArgumentOutOfRangeException(nameof(index), index, "issue number must be >= 1.");
        if (string.IsNullOrEmpty(req.Body))
            throw new ArgumentException("AddIssueCommentRequest.Body must not be empty (the API demands the `body` field).", nameof(req));
        return SendAsync<IssueComment>(HttpMethod.Post, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{index}/comments", req, ct);
    }

    /// <summary>
    /// Creates a pull request — backing of <c>create_pull_request</c>
    /// (mutation).
    /// </summary>
    /// <remarks>
    /// Back: <c>POST /repos/{o}/{n}/pulls</c> (201 with the created PR).
    /// When the <c>head</c> (or <c>base</c>) branch does not exist the
    /// acceptance instance answers <c>404</c> with an <c>errors[]</c> array
    /// (<c>could not find '…' to be a commit, branch or tag</c>); other
    /// flavours may answer <c>422</c>. Both surface as
    /// <see cref="ForgejoException"/> (code <c>not_found</c> /
    /// <c>http_422</c>) and the MCP tool maps them to a structured
    /// error envelope. Minimal payload: title, body, base, head, draft — no
    /// labels / assignees / milestone.
    /// </remarks>
    public Task<PullRequest> CreatePullRequestAsync(string owner, string name, CreatePullRequestRequest req, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ArgumentException("CreatePullRequestRequest.Title must not be empty.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Base))
            throw new ArgumentException("CreatePullRequestRequest.Base must not be empty (target branch name).", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Head))
            throw new ArgumentException("CreatePullRequestRequest.Head must not be empty (source branch, or owner:branch).", nameof(req));
        return SendAsync<PullRequest>(HttpMethod.Post, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls", req, ct);
    }


    // ---------------------------------------------------------------------------
    // Wire / retry / error pipeline
    // ---------------------------------------------------------------------------

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var json = await SendAsyncCore(method, path, body, null, ct).ConfigureAwait(false);
        if (typeof(T) == typeof(string))
            return (T)(object)json;
        if (string.IsNullOrWhiteSpace(json))
            throw new ForgejoException($"Empty response deserialising to {typeof(T).Name} from {method} {PathOnly(path)}.");
        try
        {
            var v = ForgejoJson.FromJson<T>(json);
            if (v is null)
                throw new ForgejoException($"Null deserialising {typeof(T).Name} from {method} {PathOnly(path)}.");
            return v;
        }
        catch (JsonException je)
        {
            throw new ForgejoException($"Forgejo response for {method} {PathOnly(path)} is not valid {typeof(T).Name}: {je.Message}", responseBody: json, inner: je);
        }
    }

    private async Task<ListResult<T>> SendPagedAsync<T>(string path, string? query, Page page, Action<HttpRequestMessage>? extra, CancellationToken ct)
    {
        var full = AppendQuery(path, query);
        var (json, total, link) = await SendAsyncCoreWithTotal(HttpMethod.Get, full, null, extra, ct).ConfigureAwait(false);
        var items = ForgejoJson.FromJson<IReadOnlyList<T>>(json)
            ?? throw new ForgejoException($"List response for {path} was not a JSON array.");
        return new ListResult<T>(items, total)
        {
            NextPageHint = ComputeNextPage(page, link, items.Count),
        };
    }

    private async Task<(byte[] bytes, string contentType)> SendRawAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var (bytes, status, contentType, _, _) = await SendAsyncCoreBytes(method, path, null, ct).ConfigureAwait(false);
        bytes ??= [];
        if (status < 200 || status >= 300)
            throw new ForgejoException($"GET {PathOnly(path)} failed with HTTP {status} {HttpDescription(status)}.", status, responseBody: Encoding.UTF8.GetString(bytes));
        if (bytes.Length == 0)
            throw new ForgejoException($"GET {PathOnly(path)} returned no body.");
        return (bytes, contentType ?? string.Empty);
    }

    private async Task<string> SendAsyncCore(
        HttpMethod method, string path, object? body, Action<HttpRequestMessage>? extra, CancellationToken ct)
        => (await SendAsyncCoreWithTotal(method, path, body, null!, ct).ConfigureAwait(false)).Json;

    /// <summary>
    /// Sends a JSON request and returns the response body decoded as UTF-8
    /// text — for endpoints whose payload is not a typed document (e.g. the
    /// <c>.diff</c> unified-patch endpoint). Errors: <see cref="ForgejoException"/>
    /// for non-2xx statuses / transport failures (same contract as <see cref="SendAsync{T}"/>).
    /// </summary>
    private Task<string> SendTextAsync(HttpMethod method, string path, CancellationToken ct)
        => SendAsyncCore(method, path, null, null, ct);

    private async Task<(string Json, long? Total, string? Link)> SendAsyncCoreWithTotal(
        HttpMethod method, string path, object? body, Action<HttpRequestMessage>? extra, CancellationToken ct)
    {
        var (bytes, status, contentType, totalCountHeader, link) = await SendAsyncCoreBytes(method, path, body, ct, extra).ConfigureAwait(false);
        if (status < 200 || status >= 300)
            throw new ForgejoException(
                $"{method} {PathOnly(path)} failed with HTTP {status} {HttpDescription(status)}.",
                status,
                TryExtractErrorCode(bytes) ?? null,
                Encoding.UTF8.GetString(bytes ?? Array.Empty<byte>()));

        var json = bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
        // Prefer the instance's paging header (the canonical source of truth);
        // fall back to an embedded "total" field for shapes that carry one.
        long? total = null;
        if (!string.IsNullOrEmpty(totalCountHeader) && long.TryParse(totalCountHeader, out var t))
            total = t;
        else if (TryExtractTotal(bytes, out var t2))
            total = t2;
        return (json, total, link);
    }

    private async Task<(byte[]? bytes, int status, string contentType, string? totalCount, string? link)> SendAsyncCoreBytes(
        HttpMethod method, string path, object? body, CancellationToken ct, Action<HttpRequestMessage>? extra = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var url = BuildUrl(path);
        var totalAttempts = _maxRetries + 1;
        TimeSpan? lastRetryAfter = null;
        Exception? lastTransport = null;

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ComputeDelay(attempt - 1);
                if (lastRetryAfter is { } ra && ra > delay)
                    delay = ra;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await SendOnceAsync(method, url, body, extra, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // User cancellation: don't retry.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                lastTransport = ex;
                if (attempt == totalAttempts - 1)
                {
                    throw new ForgejoException(
                        $"Transport error contacting {method} {PathOnly(path)} after {totalAttempts} attempt(s): {ex.Message}",
                        inner: ex);
                }
                // Retryable: loop.
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var text = bytes is null || bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);

                // Capture paging + retry hints before the response is disposed.
                lastRetryAfter = ParseRetryAfter(response);
                var totalCount = response.Headers.TryGetValues("x-total-count", out var tc)
                    ? string.Join(",", tc)
                    : null;
                var link = response.Headers.TryGetValues("Link", out var lk)
                    ? string.Join(",", lk)
                    : null;

                if (status < 200 || status >= 300)
                {
                    if (IsRetryable(status) && attempt < totalAttempts - 1)
                        continue; // transient failure: loop
                    throw new ForgejoException(
                        $"{method} {PathOnly(path)} failed with HTTP {status} {HttpDescription(status)}.",
                        statusCode: status,
                        errorCode: TryExtractErrorCode(bytes),
                        responseBody: text);
                }
                return (bytes ?? Array.Empty<byte>(), status, contentType, totalCount, link);
            }
        }

        throw new ForgejoException($"Retries exhausted contacting {method} {PathOnly(path)}.", inner: lastTransport);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, Uri url, object? body, Action<HttpRequestMessage>? extra, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Headers = { Accept = { new MediaTypeWithQualityHeaderValue("application/json") } },
        };
        if (body is not null)
        {
            var json = body is string s ? s : ForgejoJson.ToJson(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        _credentials.ApplyTo(request);
        extra?.Invoke(request);

        // SendAsync owns the response; caller disposes.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private TimeSpan ComputeDelay(int n) =>
        _retryBaseDelay * Math.Pow(2, Math.Min(10, n));

    private Uri BuildUrl(string path)
    {
        var trimmed = path.StartsWith('/') ? path[1..] : path;
        if (string.IsNullOrEmpty(trimmed))
            return _baseUrl;
        return new Uri(_baseUrl, trimmed);
    }

    /// <summary>
    /// Normalizes an instance root (e.g. <c>https://git.home.internal</c> or
    /// <c>https://git.home.internal/forgejo</c>) to the REST API root
    /// <c>https://git.home.internal/api/v1/</c>. Idempotent: URLs that already
    /// end in <c>/api/v1</c> (or <c>/api/v1/</c>) are left unchanged.
    /// </summary>
    private static Uri NormalizeApiRoot(Uri instanceRoot)
    {
        // Strip any trailing slash, then check for /api/v1.
        var builder = new UriBuilder(instanceRoot);
        var path = builder.Path ?? "/";
        // Remove trailing slash(es) for comparison.
        var trailing = path.TrimEnd('/');
        var basePath = trailing.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? trailing
            : trailing + "/api/v1";
        builder.Path = basePath + "/";
        return builder.Uri;
    }

    private static string PathOnly(string path)
    {
        var q = path.IndexOf('?');
        return q < 0 ? path : path[..q];
    }

    private static string AppendQuery(string path, string? query)
    {
        if (string.IsNullOrEmpty(query))
            return path;
        var sep = path.Contains('?') ? "&" : "?";
        return $"{path}{sep}{query}";
    }

    /// <summary>
    /// Derives the 1-based next-page hint for a list response.
    /// Preference order:
    /// <list type="number">
    /// <item>A <c>Link: rel="next"</c> header (the canonical source) — extract
    /// its <c>page</c> query parameter.</item>
    /// <item>Heuristic: the current page came back full (<paramref name="count"/>
    /// equals the page <c>Size</c>), so a next page may exist → <c>Number + 1</c>.</item>
    /// <item>A short page (<paramref name="count"/> &lt; <c>Size</c>) → no
    /// successor → <c>null</c>.</item>
    /// </list>
    /// </summary>
    private static int? ComputeNextPage(Page page, string? link, int count)
    {
        // 1) Link header wins.
        if (!string.IsNullOrEmpty(link) && TryParseLinkPage(link, out var linkPage) && linkPage >= 1)
            return linkPage;
        // 2) Heuristic.
        if (count < page.Size)
            return null;
        return page.Number + 1;
    }

    /// <summary>
    /// Extracts a 1-based <c>page</c> query parameter from the first
    /// <c>rel="next"</c> target in a <c>Link</c> header value. Returns
    /// <c>false</c> when the header has no <c>rel="next"</c> target or its
    /// <c>page</c> parameter is absent/non-numeric.
    /// </summary>
    private static bool TryParseLinkPage(string link, out int page)
    {
        page = 0;
        // The standard shape is <https://…/…?page=2&limit=30>; rel="next".
        // Scan each comma-separated field and pick the one tagged rel="next".
        var match = LinkNextRegex.Match(link);
        if (!match.Success)
            return false;
        var uriPart = match.Groups[1].Value.Trim().Trim('<', '>');
        var q = uriPart.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
            return false;
        foreach (var pair in uriPart[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 &&
                string.Equals(kv[0], "page", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(kv[1], out var p))
            {
                page = p;
                return true;
            }
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex LinkNextRegex =
        new(@"<([^>]*?)>\s*;\s*rel=""next""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsRetryable(int status)
        => status == 429 || (status >= 500 && status < 600);

    private static string HttpDescription(int status)
    {
        try
        {
            return ((HttpStatusCode)status).ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null)
            return null;
        if (ra.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra.Date is { } date)
        {
            var span = date.ToUniversalTime() - DateTimeOffset.UtcNow;
            return span < TimeSpan.Zero ? TimeSpan.Zero : span;
        }
        return null;
    }

    private static string? TryExtractErrorCode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var key in new[] { "error", "code", "message" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.String)
                    return el.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryExtractTotal(byte[]? bytes, out long total)
    {
        total = 0;
        if (bytes is null || bytes.Length == 0)
            return false;
        // Forgejo returns a total_count on the page envelope for some endpoints;
        // list endpoints return an array and total_count in the response header
        // "Total-Count", so this helper is currently used as a no-op fallback
        // for shapes that embed "total" in the JSON.
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var key in new[] { "total_count", "totalCount", "total" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) && el.TryGetInt64(out var v))
                {
                    total = v;
                    return true;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsLikelyText(byte[]? bytes, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("text/", StringComparison.Ordinal) ||
                ct.Contains("json", StringComparison.Ordinal) ||
                ct.Contains("xml", StringComparison.Ordinal) ||
                ct.Contains("javascript", StringComparison.Ordinal) ||
                ct.Contains("yaml", StringComparison.Ordinal) ||
                ct.Contains("csv", StringComparison.Ordinal) ||
                ct.Contains("plain", StringComparison.Ordinal) ||
                ct.Contains("html", StringComparison.Ordinal))
                return true;
            return false;
        }
        if (bytes is null || bytes.Length == 0)
            return true;
        // Heuristic: no NUL byte in the first 1KB ⇒ treat as text.
        var n = Math.Min(1024, bytes.Length);
        for (var i = 0; i < n; i++)
            if (bytes[i] == 0)
                return false;
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Mutation payload for <see cref="ForgejoClient.CreateIssueAsync"/>.
/// Maps to the <c>POST /repos/{o}/{r}/issues</c> body.
/// </summary>
public sealed record CreateIssueRequest
{
    /// <summary>Issue title (required).</summary>
    public string Title { get; init; } = null!;

    /// <summary>Issue body (Markdown; nullable).</summary>
    public string? Body { get; init; }

    /// <summary>Label names to attach (must exist in the repo).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Assignee usernames (must exist on the instance).</summary>
    public IReadOnlyList<string> Assignees { get; init; } = []
;

    /// <summary>Milestone number, when the repository has milestones.</summary>
    public long? Milestone { get; init; }
}

/// <summary>
/// Wire form of a branch endpoint entry: <c>{ name, commit: { id, message,
/// url, … } }</c>. The public <see cref="Branch"/> is a flatter, first-line-only
/// projection of this shape. Kept as raw JSON so future Forgejo revisions
/// keep deserialising even if they add fields.
/// </summary>
public sealed record BranchWire
{
    /// <summary>Branch name.</summary>
    public string Name { get; init; } = null!;

    /// <summary>Nested tip-commit (id/message/url).</summary>
    public BranchWireCommit? Commit { get; init; }
}

/// <summary>Nested commit of <see cref="BranchWire"/> (subset of fields).</summary>
public sealed record BranchWireCommit
{
    /// <summary>Full SHA.</summary>
    public string? Id { get; init; }

    /// <summary>Full commit message (subject + body).</summary>
    public string? Message { get; init; }

    /// <summary>Commit page URL.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// Mutation payload for <see cref="ForgejoClient.UpdateIssueAsync"/>.
/// Maps to the sparse <c>PATCH /repos/{o}/{n}/issues/{index}</c> body.
/// Only the non-<c>null</c> fields are sent. Labels are mutated by <em>id</em>
/// (callers are expected to use <c>list_labels</c> first to obtain ids).
/// </summary>
public sealed record UpdateIssueRequest
{
    /// <summary>New title (set to non-empty to change).</summary>
    public string? Title { get; init; }

    /// <summary>New body (Markdown; set to non-null to change).</summary>
    public string? Body { get; init; }

    /// <summary>New state: <c>open</c> or <c>closed</c>.</summary>
    public string? State { get; init; }

    /// <summary>
    /// Replace the issue's assignee set with exactly these logins.
    /// An empty list is only valid here when <see cref="ClearAssignees"/> is
    /// also true (see the MCP surface opt-in contract) — otherwise passing
    /// an empty list without the flag is rejected on the tool side.
    /// </summary>
    public IReadOnlyList<string>? Assignees { get; init; }

    /// <summary>
    /// Explicit opt-in to clear the assignee set to zero. When <c>true</c>,
    /// <see cref="Assignees"/> (even empty) is transmitted as-is. When
    /// <c>false</c> (default), an empty <see cref="Assignees"/> list is
    /// dropped from the payload to avoid accidental unassignment.
    /// </summary>
    public bool ClearAssignees { get; init; }

    /// <summary>
    /// Milestone id to set (id; obtain from the instance's milestones or use
    /// the issue's current milestone id to preserve it).
    /// </summary>
    public long? MilestoneId { get; init; }

    /// <summary>
    /// Replace the issue's label set with exactly these label <em>ids</em>
    /// (caller must <c>list_labels</c> first). When <c>null</c>, labels are
    /// left untouched.
    /// </summary>
    public IReadOnlyList<long>? Labels { get; init; }

    /// <summary>Label ids to ADD (incremental; do not remove others).</summary>
    public IReadOnlyList<long>? AddLabelIds { get; init; }

    /// <summary>Label ids to REMOVE (incremental; do not change others).</summary>
    public IReadOnlyList<long>? RemoveLabelIds { get; init; }
}

