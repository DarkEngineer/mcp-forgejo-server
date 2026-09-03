namespace Forgejo.Client;

/// <summary>A Forgejo user / organization. Mirrors the <c>User</c> shape of the API.</summary>
public sealed record User
{
    /// <summary>Account id.</summary>
    public long Id { get; init; }

    /// <summary>Login name (handle).</summary>
    public string Login { get; init; } = null!;

    /// <summary>Display name, when set.</summary>
    public string? FullName { get; init; }

    /// <summary>Avatar URL.</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>HTML page of the account.</summary>
    public string? HtmlUrl { get; init; }

    /// <summary>True when the account is an organization.</summary>
    public bool IsOrganization { get; init; }
}

/// <summary>A repository. Mirrors the API's <c>Repository</c> shape (subset).</summary>
public sealed record Repository
{
    /// <summary>Repository id.</summary>
    public long Id { get; init; }

    /// <summary>Repository name (owner excluded).</summary>
    public string Name { get; init; } = null!;

    /// <summary>Full path: "owner/name".</summary>
    public string FullName { get; init; } = null!;

    /// <summary>Description (nullable).</summary>
    public string? Description { get; init; }

    /// <summary>True when the repository is private.</summary>
    public bool Private { get; init; }

    /// <summary>True when the repository has no commits yet.</summary>
    public bool Empty { get; init; }

    /// <summary>True when the repository is a fork.</summary>
    public bool Fork { get; init; }

    /// <summary>Default branch name.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>Primary language, when detected.</summary>
    public string? Language { get; init; }

    /// <summary>Repository size in bytes, when reported.</summary>
    public long? Size { get; init; }

    public bool Archive { get; init; }

    /// <summary>Owner of the repository.</summary>
    public User? Owner { get; init; }

    /// <summary>Open (non-PR) issue count, when reported.</summary>
    public long? OpenIssuesCount { get; init; }

    /// <summary>Star count, when reported.</summary>
    public long? StarsCount { get; init; }

    /// <summary>Fork count, when reported.</summary>
    public long? ForksCount { get; init; }

    /// <summary>Watch count, when reported.</summary>
    public long? WatchersCount { get; init; }

    /// <summary>Created timestamp (RFC3339).</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Last updated / pushed timestamp (RFC3339).</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Cloning via HTTP.</summary>
    public string? CloneUrl { get; init; }

    /// <summary>Cloning via SSH.</summary>
    public string? SshUrl { get; init; }

    /// <summary>Repository page URL.</summary>
    public string? HtmlUrl { get; init; }
}

/// <summary>A label on an issue / pull request.</summary>
public sealed record Label
{
    /// <summary>Label id.</summary>
    public long Id { get; init; }

    /// <summary>Label name (unique within a repository).</summary>
    public string Name { get; init; } = null!;

    /// <summary>Colour, when set.</summary>
    public string? Color { get; init; }

    public string? Description { get; init; }
}

/// <summary>A milestone, when present on an issue.</summary>
public sealed record Milestone
{
    /// <summary>Milestone id.</summary>
    public long Id { get; init; }

    /// <summary>Milestone title.</summary>
    public string Title { get; init; } = null!;

    public string? Description { get; init; }

    /// <summary>State: "open" / "closed" / "completed".</summary>
    public string? State { get; init; }

    public DateTime? DueDate { get; init; }

    public long OpenIssues { get; init; }

    public long ClosedIssues { get; init; }
}

/// <summary>The git head/base branch reference of a pull request.</summary>
public sealed record PullRequestRef
{
    /// <summary>Label ("BASE" / "HEAD").</summary>
    public string? Label { get; init; }

    /// <summary>Branch name.</summary>
    public string? Ref { get; init; }

    /// <summary>Commit SHA on that branch.</summary>
    public string? Sha { get; init; }

    /// <summary>Repository that branch lives in.</summary>
    public Repository? Repo { get; init; }
}

/// <summary>
/// An issue on a repository. Forgejo models pull requests as issues that carry
/// a <see cref="PullRequestRef"/>; <see cref="IsPullRequest"/> reflects that.
/// </summary>
public sealed record Issue
{
    /// <summary>Global issue id.</summary>
    public long Id { get; init; }

    /// <summary>Issue number within the repository (shown in the UI).</summary>
    public int Number { get; init; }

    /// <summary>Issue title.</summary>
    public string Title { get; init; } = null!;

    /// <summary>Issue body (Markdown).</summary>
    public string? Body { get; init; }

    /// <summary>State: "open" or "closed".</summary>
    public string? State { get; init; }

    /// <summary>Labels attached.</summary>
    public IReadOnlyList<Label> Labels { get; init; } = [];

    /// <summary>Milestone, when set.</summary>
    public Milestone? Milestone { get; init; }

    /// <summary>Author of the issue.</summary>
    public User? User { get; init; }

    /// <summary>Assignees.</summary>
    public IReadOnlyList<User> Assignees { get; init; } = [];

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    /// <summary>Closed timestamp, when the issue is closed.</summary>
    public DateTime? ClosedAt { get; init; }

    /// <summary>True when this issue is actually a pull request.</summary>
    public bool IsPullRequest { get; init; }

    /// <summary>Merge metadata present on PR entries in some API versions.</summary>
    public bool Merged { get; init; }

    /// <summary>Merged timestamp, when a PR has been merged.</summary>
    public DateTime? MergedAt { get; init; }

    public string? HtmlUrl { get; init; }

    /// <summary>True when the issue is a pinned repository issue.</summary>
    public bool Pinned { get; init; }
}

/// <summary>A pull request (the list / detail shape from /pulls).</summary>
public sealed record PullRequest
{
    /// <summary>Global issue id of the PR.</summary>
    public long Id { get; init; }

    /// <summary>PR number within the repository.</summary>
    public int Number { get; init; }

    public string Title { get; init; } = null!;

    public string? Body { get; init; }

    /// <summary>State of the PR's underlying issue: "open" / "closed".</summary>
    public string? State { get; init; }

    /// <summary>True when the PR has been merged.</summary>
    public bool Merged { get; init; }

    public DateTime? MergedAt { get; init; }

    /// <summary>True when the PR is a draft.</summary>
    public bool Draft { get; init; }

    public IReadOnlyList<Label> Labels { get; init; } = [];

    public Milestone? Milestone { get; init; }

    /// <summary>Source branch reference.</summary>
    public PullRequestRef? Head { get; init; }

    /// <summary>Target branch reference.</summary>
    public PullRequestRef? Base { get; init; }

    /// <summary>Author.</summary>
    public User? User { get; init; }

    /// <summary>Assignees.</summary>
    public IReadOnlyList<User> Assignees { get; init; } = [];

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public string? HtmlUrl { get; init; }

    /// <summary>Comments on the PR, when reported.</summary>
    public long Comments { get; init; }

    /// <summary>True when the PR needs conflict resolution.</summary>
    public bool HasFilesConflicts { get; init; }
}

/// <summary>The commit metadata (author / committer / message) of a commit.</summary>
public sealed record GitCommit
{
    /// <summary>Commit message (subject + body).</summary>
    public string Message { get; init; } = null!;

    /// <summary>Git author name / email / date (client-side identity).</summary>
    public GitActor? Author { get; init; }

    /// <summary>Git committer identity.</summary>
    public GitActor? Committer { get; init; }

    /// <summary>Tree SHA, when reported.</summary>
    public string? TreeSha { get; init; }
}

/// <summary>A git identity (name / email / date) inside a commit object.</summary>
public sealed record GitActor
{
    public string? Name { get; init; }
    public string? Email { get; init; }

    /// <summary>ISO-8601 timestamp as reported.</summary>
    public DateTime Date { get; init; }
}

/// <summary>A repository commit entry (list + detail shape).</summary>
public sealed record RepositoryCommit
{
    /// <summary>Full commit SHA.</summary>
    public string Sha { get; init; } = null!;

    /// <summary>Commit date.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>The Forgejo account that authored the commit, when it could be resolved.</summary>
    public User? Author { get; init; }

    /// <summary>The Forgejo account that committed the change, when it could be resolved.</summary>
    public User? Committer { get; init; }

    /// <summary>The git commit metadata.</summary>
    public GitCommit? Commit { get; init; }
}

/// <summary>
/// One changed file in a pull request: churn stats plus the per-file unified
/// patch when the instance embeds it in the <c>/pulls/{index}/files</c>
/// listing (<c>Patch</c> is null when the API omits it — the self-hosted
/// instance used for acceptance omits per-file patches, so use the combined
/// <c>.diff</c> payload from the sibling method on <c>ForgejoClient</c>).</summary>
public sealed record PullRequestFile
{
    /// <summary>Path of the file in the repository.</summary>
    public string Filename { get; init; } = null!;

    /// <summary>Gitea/Forgejo status: "added" / "removed" / "modified" (reported as "changed" on this instance).</summary>
    public string? Status { get; init; }

    /// <summary>Lines added.</summary>
    public long Additions { get; init; }

    /// <summary>Lines deleted.</summary>
    public long Deletions { get; init; }

    /// <summary>Total line changes (additions + deletions), when reported.</summary>
    public long? Changes { get; init; }

    /// <summary>
    /// The unified diff patch for this file when the instance includes it;
    /// <c>null</c> when the API omits it (as on the acceptance instance).
    /// </summary>
    public string? Patch { get; init; }
}

/// <summary>A repository release (asset bodies are never fetched).</summary>
public sealed record Release
{
    /// <summary>Release id.</summary>
    public long Id { get; init; }

    /// <summary>Git tag the release points at.</summary>
    public string? TagName { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Markdown body.</summary>
    public string? Body { get; init; }

    /// <summary>True when the release is not yet public.</summary>
    public bool Draft { get; init; }

    /// <summary>True for pre-releases.</summary>
    public bool Prerelease { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Publish timestamp (equals creation for non-draft releases).</summary>
    public DateTime? PublishedAt { get; init; }

    /// <summary>Repository page for the release.</summary>
    public string? HtmlUrl { get; init; }

    /// <summary>Attached assets (metadata only — bodies are not fetched).</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
}

/// <summary>Metadata for a release attachment (name / url / size only).</summary>
public sealed record ReleaseAsset
{
    /// <summary>Asset id.</summary>
    public long Id { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Direct download URL.</summary>
    public string? Url { get; init; }

    /// <summary>Size in bytes, when reported.</summary>
    public long? Size { get; init; }
}

/// <summary>One entry in a repository file tree (<c>GET /repos/{o}/{n}/contents/{path}</c>).</summary>
public sealed record ContentEntry
{
    /// <summary>Entry name (leaf of the path).</summary>
    public string Name { get; init; } = null!;

    /// <summary>API type: "file", "dir", or "symlink".</summary>
    public string? Type { get; init; }

    /// <summary>Path relative to the repository root.</summary>
    public string Path { get; init; } = null!;

    /// <summary>Git blob/tree SHA of the entry.</summary>
    public string? Sha { get; init; }

    /// <summary>Size in bytes (0 for directories).</summary>
    public long Size { get; init; }

    /// <summary>Repository page for the entry.</summary>
    public string? HtmlUrl { get; init; }
}
public sealed record FileContent
{
    /// <summary>File path in the repository.</summary>
    public string Path { get; init; } = null!;

    /// <summary>Raw bytes of the file.</summary>
    public byte[] Bytes { get; init; } = [];

    /// <summary>Decoded text, when the payload is UTF-8 text.</summary>
    public string Text { get; init; } = null!;

    /// <summary>HTTP content type the server reported.</summary>
    public string? ContentType { get; init; }

    public long Size { get; init; }
}

/// <summary>
/// A repository branch (<c>GET /repos/{o}/{n}/branches[/...]</c>).
/// Tier-2 read surface: branch name, tip commit id, and the first line of
/// the tip commit message.
/// </summary>
public sealed record Branch
{
    /// <summary>Branch name (e.g. <c>main</c>).</summary>
    public string Name { get; init; } = null!;

    /// <summary>Full SHA of the tip commit.</summary>
    public string CommitId { get; init; } = null!;

    /// <summary>First line of the tip commit message (subject; may be empty).</summary>
    public string? CommitMessage { get; init; }

    /// <summary>Commit page URL, when the instance reports it.</summary>
    public string? CommitUrl { get; init; }
}

/// <summary>
/// An issue / pull-request comment (<c>POST /repos/{o}/{n}/issues/{index}/comments</c>).
/// Carries the minimal identity the caller needs: who wrote it, when, and
/// how to reach it.
/// </summary>
public sealed record IssueComment
{
    /// <summary>Global comment id.</summary>
    public long Id { get; init; }

    /// <summary>The account that authored the comment.</summary>
    public User? User { get; init; }

    /// <summary>Creation timestamp (RFC3339).</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Comment page URL, when the instance reports it.</summary>
    public string? HtmlUrl { get; init; }

    /// <summary>API URL, when the instance reports it.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// Wire payload for <see cref="ForgejoClient.CreateLabelAsync"/>
/// (<c>POST /repos/{o}/{n}/labels</c>).
/// </summary>
public sealed record CreateLabelRequest
{
    /// <summary>Label name (required). Distinct names per repository.</summary>
    public string Name { get; init; } = null!;

    /// <summary>Label colour. Accepts <c>#rrggbb</c> or a bare <c>rrggbb</c>.</summary>
    public string Color { get; init; } = null!;

    /// <summary>Human-readable description (optional).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Wire payload for <see cref="ForgejoClient.AddIssueCommentAsync"/>
/// (<c>POST /repos/{o}/{n}/issues/{index}/comments</c>). The API field on
/// this instance is <c>body</c> (Markdown) — NOT <c>content</c>; the model
/// keeps the conventional <c>Body</c> property name and the serializer emits
/// <c>body</c>.
/// </summary>
public sealed record AddIssueCommentRequest
{
    /// <summary>Comment body in Markdown (required on this instance).</summary>
    public string Body { get; init; } = null!;
}

/// <summary>
/// Wire payload for <see cref="ForgejoClient.CreatePullRequestAsync"/>
/// (<c>POST /repos/{o}/{n}/pulls</c>). Deliberately minimal — no labels /
/// assignees / milestone selection: the instance surface does not need them
/// for a first-class PR create.
/// </summary>
public sealed record CreatePullRequestRequest
{
    /// <summary>PR title (required).</summary>
    public string Title { get; init; } = null!;

    /// <summary>PR body in Markdown (optional).</summary>
    public string? Body { get; init; }

    /// <summary>Target branch name (required).</summary>
    public string Base { get; init; } = null!;

    /// <summary>Source branch. Bare = same repo; <c>owner:branch</c> = cross-repo.</summary>
    public string Head { get; init; } = null!;

    /// <summary>Draft flag. Omitted from the wire when <c>null</c> (server default).</summary>
    public bool? Draft { get; init; }
}

/// <summary>
/// A label as projected to the MCP surface: the stable identity fields an
/// agent needs (id for <c>update_issue</c> label mutation, plus name / colour
/// / description for display).
/// </summary>
public sealed record LabelView
{
    public long Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Color { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Result envelope of the <c>create_label</c> tool. <see cref="Created"/> is
/// true on a fresh label (see <see cref="Label"/>) and false on the
/// idempotency path (see <see cref="Existing"/>), which fires when a label
/// with the same name already exists — on this instance the API itself would
/// happily 201 a duplicate, and on other builds it returns 409 — so the
/// surface resolves both cases to the same shape.
/// </summary>
public sealed record CreateLabelResult
{
    /// <summary>True when a new label was created by this call.</summary>
    public bool Created { get; init; }

    /// <summary>The created label, when <see cref="Created"/> is true.</summary>
    public LabelView? Label { get; init; }

    /// <summary>The pre-existing label, when <see cref="Created"/> is false.</summary>
    public LabelView? Existing { get; init; }
}

/// <summary>
/// Result envelope of the <c>create_pull_request</c> tool: the identity
/// fields of the created PR — id, number, html_url, state (plus a few
/// context fields) — the minimal set an agent needs to report back or chain
/// a follow-up, without shipping the full PR document.
/// </summary>
public sealed record CreatePullRequestResult
{
    /// <summary>Global issue id of the new PR.</summary>
    public long Id { get; init; }

    /// <summary>PR number within the repository.</summary>
    public int Number { get; init; }

    /// <summary>Web URL of the PR, when the instance reports one.</summary>
    public string? HtmlUrl { get; init; }

    /// <summary>PR state: "open" / "closed".</summary>
    public string? State { get; init; }

    /// <summary>True when the PR was opened as a draft.</summary>
    public bool Draft { get; init; }

    /// <summary>The PR title (echoed for convenience).</summary>
    public string? Title { get; init; }

    /// <summary>Target branch name.</summary>
    public string? BaseRef { get; init; }

    /// <summary>Source branch name.</summary>
    public string? HeadRef { get; init; }

    /// <summary>Creation timestamp (RFC3339).</summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// A milestone on a repository, as returned by the milestone endpoints
/// (<c>GET/POST /repos/{o}/{n}/milestones</c>). This is the wire shape of the
/// standalone milestone endpoints, distinct from
/// <see cref="Milestone"/> (the projection embedded on issues/PRs).
/// Verified against the acceptance instance (2026-09-03): entries carry
/// <c>id</c> (a <em>global</em> id — the Gitea/Forgejo wire has no
/// repository-local <c>number</c>), <c>title</c>, optional <c>description</c>,
/// <c>state</c> ("open" / "closed"), <c>open_issues</c>,
/// <c>closed_issues</c>, <c>created_at</c>, <c>updated_at</c>,
/// <c>closed_at</c> (null while open), and optional <c>due_on</c>.
/// </summary>
public sealed record RepositoryMilestone
{
    /// <summary>The milestone's global id (the lookup key for the
    /// <c>/milestones/{id}</c> endpoint).</summary>
    public long Id { get; init; }

    /// <summary>Milestone title (unique per repository on create).</summary>
    public string Title { get; init; } = null!;

    /// <summary>Optional description (null when unset).</summary>
    public string? Description { get; init; }

    /// <summary>State: "open" / "closed".</summary>
    public string? State { get; init; }

    /// <summary>Issues currently open against this milestone.</summary>
    public long OpenIssues { get; init; }

    /// <summary>Issues closed against this milestone.</summary>
    public long ClosedIssues { get; init; }

    /// <summary>Creation timestamp (RFC3339).</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Last update timestamp (RFC3339).</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Closed timestamp (null while the milestone is open).</summary>
    public DateTime? ClosedAt { get; init; }

    /// <summary>
    /// Due date as an ISO-8601 string exactly as reported on the wire (e.g.
    /// <c>2026-10-01T00:00:00Z</c>); null when the milestone has no due date.
    /// Kept a string passthrough so the tool round-trips the value verbatim.
    /// </summary>
    public string? DueOn { get; init; }
}

/// <summary>
/// Wire payload for <see cref="ForgejoClient.CreateMilestoneAsync"/>
/// (<c>POST /repos/{o}/{n}/milestones</c>). Required: <see cref="Title"/>.
/// Optional: <see cref="Description"/> and <see cref="DueOn"/> — both are
/// omitted from the wire when null (the shared <see cref="ForgejoJson"/>
/// options drop nulls), so a title-only create sends exactly
/// <c>{"title":"…"}</c>.
/// </summary>
public sealed record CreateMilestoneRequest
{
    /// <summary>Milestone title (required; distinct per repository).</summary>
    public string Title { get; init; } = null!;

    /// <summary>Optional description (omitted from the wire when null).</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional due date (ISO-8601 string, e.g. <c>2026-10-01T00:00:00Z</c>;
    /// omitted from the wire when null). Sent verbatim in the <c>due_on</c>
    /// field — the API accepts RFC3339 / date-only forms and echoes the value
    /// back as a string.
    /// </summary>
    public string? DueOn { get; init; }
}
