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

/// <summary>The result of reading a file (raw bytes + decoded text when possible).</summary>
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
