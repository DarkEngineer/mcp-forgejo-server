namespace Forgejo.Client;

/// <summary>Pagination for list endpoints. Forgejo defaults to 30 per page and caps at 50.</summary>
public sealed record Page
{
    /// <summary>1-based page number. Default 1.</summary>
    public int Number { get; init; } = 1;

    /// <summary>Items per page. Default 30.</summary>
    public int Size { get; init; } = 30;

    internal string ToQuery() => $"page={Number}&limit={Size}";
}

/// <summary>Filters for issue / pull-request lists.</summary>
public sealed record IssueListOptions
{
    /// <summary>State filter: "open", "closed", or "all". Default "open".</summary>
    public string State { get; init; } = "open";

    /// <summary>Assignee usernames to filter by.</summary>
    public IReadOnlyList<string> AssignedBy { get; init; } = System.Array.Empty<string>();

    /// <summary>Label names to filter by.</summary>
    public IReadOnlyList<string> Labels { get; init; } = System.Array.Empty<string>();

    internal string ToQuery()
    {
        var seg = new List<string>();
        if (!string.IsNullOrEmpty(State) && !State.Equals("all", StringComparison.OrdinalIgnoreCase))
            seg.Add($"state={Uri.EscapeDataString(State)}");
        foreach (var a in AssignedBy.Distinct())
            seg.Add($"assigned_by={Uri.EscapeDataString(a)}");
        foreach (var l in Labels.Distinct())
            seg.Add($"labels={Uri.EscapeDataString(l)}");
        return string.Join("&", seg);
    }
}

/// <summary>Filters for commit lists (branch/SHA, in addition to paging).</summary>
public sealed record CommitListOptions
{
    /// <summary>Branch or SHA to list commits of. Default: the repository's default branch.</summary>
    public string? Branch { get; init; }
}
