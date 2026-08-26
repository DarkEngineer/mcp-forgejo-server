// SPDX-License-Identifier: MIT

namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Forgejo.Client;
using Forgejo.Mcp;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// Client tests for the Tier-2 endpoints: <c>GET /branches</c>,
/// <c>GET /branches/{name}</c>, <c>GET/POST /labels</c>,
/// <c>PATCH /issues/{index}</c>, <c>POST /issues/{index}/comments</c>,
/// and <c>POST /pulls</c>. Covers the success path, the wire format
/// (sparse PATCH body, the assignee opt-in contract, the comment <c>body</c>
/// key, the PR payload), and the error mapping (404 / 422).
/// </summary>
public class ForgejoClientTier2MutationTests
{
    private static TestHttpHandler Enqueue(StatusCode status, string body,
        string contentType = "application/json")
    {
        var h = new TestHttpHandler();
        h.Enqueue(status, body, contentType);
        return h;
    }

    private static ForgejoClient ClientFor(TestHttpHandler handler)
        => new(new Uri("https://git.example.com"), ForgejoCredentials.Anonymous(),
               maxRetries: 0, retryBaseDelay: TimeSpan.Zero, new HttpClient(handler));

    // ------------------------------------------------------------------
    // ListBranchesAsync — GET /repos/{o}/{n}/branches
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListBranches_parses_name_and_tip_commit_first_line()
    {
        var handler = Enqueue(StatusCode.OK, """
        [
          {
            "name": "main",
            "commit": {
              "id": "abc123def456",
              "message": "Initial commit\n\nA longer body line that must be dropped.",
              "url": "https://git.example.com/o/n/commit/abc123def456"
            }
          },
          {"name": "feature/x", "commit": {"id": "f00f", "message": "WIP"}}
        ]
        """);
        using var client = ClientFor(handler);

        var branches = await client.ListBranchesAsync("o", "n");

        var main = branches.Single(b => b.Name == "main");
        Assert.Equal("abc123def456", main.CommitId);
        Assert.Equal("Initial commit", main.CommitMessage);
        Assert.Equal("https://git.example.com/o/n/commit/abc123def456", main.CommitUrl);
        var feat = branches.Single(b => b.Name == "feature/x");
        Assert.Equal("f00f", feat.CommitId);
        Assert.Equal("WIP", feat.CommitMessage);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/branches", url.AbsolutePath);
        Assert.Equal("GET", handler.Requests.Single().Method.Method);
    }

    [Fact]
    public async Task ListBranches_empty_array_is_a_valid_empty_result()
    {
        var handler = Enqueue(StatusCode.OK, "[]");
        using var client = ClientFor(handler);

        var branches = await client.ListBranchesAsync("o", "n");

        Assert.NotNull(branches);
        Assert.Empty(branches);
    }

    [Fact]
    public async Task ListBranches_404_maps_to_forgejo_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.ListBranchesAsync("o", "n"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("branches", ex.Message);
    }

    // ------------------------------------------------------------------
    // GetBranchAsync — GET /repos/{o}/{n}/branches/{name}
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBranch_returns_single_branch()
    {
        var handler = Enqueue(StatusCode.OK, """
        {"name": "main", "commit": {"id": "deadbeef", "message": "hi"}}
        """);
        using var client = ClientFor(handler);

        var branch = await client.GetBranchAsync("o", "n", "main");

        Assert.Equal("main", branch.Name);
        Assert.Equal("deadbeef", branch.CommitId);
        Assert.Equal("hi", branch.CommitMessage);
        assertUri(handler, "/api/v1/repos/o/n/branches/main", "GET", hasJsonBody: false);
    }

    [Fact]
    public async Task GetBranch_escalates_slashed_branch_names()
    {
        var handler = Enqueue(StatusCode.OK, """{"name": "feature/x", "commit": {"id": "1"}}""");
        using var client = ClientFor(handler);

        await client.GetBranchAsync("o", "n", "feature/x");

        assertUri(handler, "/api/v1/repos/o/n/branches/feature%2Fx", "GET", hasJsonBody: false);
    }

    [Fact]
    public async Task GetBranch_blanks_are_rejected()
    {
        var handler = Enqueue(StatusCode.OK, "{}");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBranchAsync("o", "n", "  "));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetBranch_404_maps_to_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.GetBranchAsync("o", "n", "nope"));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // ListLabelsAsync — GET /repos/{o}/{n}/labels
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListLabels_parses_id_name_color_description()
    {
        var handler = Enqueue(StatusCode.OK, """
        [
          {"id": 1, "name": "bug", "color": "#ee0000", "description": "a bug"},
          {"id": 2, "name": "help wanted"}
        ]
        """);
        using var client = ClientFor(handler);

        var labels = await client.ListLabelsAsync("o", "n");

        var bug = labels.Single(l => l.Name == "bug");
        Assert.Equal(1, bug.Id);
        Assert.Equal("#ee0000", bug.Color);
        Assert.Equal("a bug", bug.Description);
        Assert.Null(labels.Single(l => l.Name == "help wanted").Color);
        assertUri(handler, "/api/v1/repos/o/n/labels", "GET", hasJsonBody: false);
    }

    // ------------------------------------------------------------------
    // CreateLabelAsync — POST /repos/{o}/{n}/labels
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateLabel_sends_name_color_and_optional_description()
    {
        var handler = Enqueue(StatusCode.Created, """
        {"id": 9, "name": "mcp-label", "color": "#336699", "description": "made by the agent"}
        """);
        using var client = ClientFor(handler);

        var label = await client.CreateLabelAsync("o", "n", new CreateLabelRequest
        {
            Name = "mcp-label",
            Color = "#336699",
            Description = "made by the agent",
        });

        Assert.Equal(9, label.Id);
        Assert.Equal("mcp-label", label.Name);
        Assert.Equal("#336699", label.Color);
        Assert.Equal("made by the agent", label.Description);
        assertUri(handler, "/api/v1/repos/o/n/labels", "POST", hasJsonBody: true);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("mcp-label", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("#336699", body.RootElement.GetProperty("color").GetString());
        Assert.Equal("made by the agent", body.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task CreateLabel_rejects_blank_name_or_color()
    {
        var handler = Enqueue(StatusCode.Created, "{}");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateLabelAsync("o", "n", new CreateLabelRequest { Name = "", Color = "#112233" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateLabelAsync("o", "n", new CreateLabelRequest { Name = "ok", Color = "  " }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateLabel_404_maps_to_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.CreateLabelAsync("o", "n", new CreateLabelRequest { Name = "x", Color = "#112233" }));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // UpdateIssueAsync — PATCH /repos/{o}/{n}/issues/{index}
    // ------------------------------------------------------------------

    private const string IssueDoc = """
        {
          "id": 100, "number": 7, "title": "new title", "body": "new body",
          "state": "open", "labels": [{"id": 3, "name": "p1"}],
          "assignees": [{"login": "alice"}],
          "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-02T00:00:00Z"
        }
        """;

    [Fact]
    public async Task UpdateIssue_uses_patch_with_sparse_body_state_only()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        var issue = await client.UpdateIssueAsync("o", "n", 7,
            new UpdateIssueRequest { State = "open" });

        Assert.Equal("new title", issue.Title);
        Assert.Equal("open", issue.State);
        var req = handler.Requests.Single();
        Assert.Equal("PATCH", req.Method.Method);
        Assert.Equal("/api/v1/repos/o/n/issues/7", req.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("open", body.RootElement.GetProperty("state").GetString());
        // Sparse-payload contract: the body must contain exactly `state`.
        var props = body.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "state" }, props);
    }

    [Fact]
    public async Task UpdateIssue_title_body_state_and_assignees_serialize()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        await client.UpdateIssueAsync("o", "n", 12, new UpdateIssueRequest
        {
            Title = "t",
            Body = "b",
            State = "closed",
            Assignees = ["alice", "bob"],
        });

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("t", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("b", body.RootElement.GetProperty("body").GetString());
        Assert.Equal("closed", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("assignees").GetArrayLength());
        // The opt-in flag is a client-side construct and never goes on the wire.
        Assert.False(body.RootElement.TryGetProperty("clear_assignees", out _));
    }

    [Fact]
    public async Task UpdateIssue_empty_assignees_without_clear_flag_are_stripped()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        await client.UpdateIssueAsync("o", "n", 5, new UpdateIssueRequest
        {
            State = "open",
            Assignees = [],
            // ClearAssignees left at its default (false) — the empty list
            // must be dropped from the wire so an accidental unassignment
            // cannot happen.
        });

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("open", body.RootElement.GetProperty("state").GetString());
        Assert.False(body.RootElement.TryGetProperty("assignees", out _));
    }

    [Fact]
    public async Task UpdateIssue_clear_assignees_true_sends_empty_list()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        await client.UpdateIssueAsync("o", "n", 5, new UpdateIssueRequest
        {
            State = "open",
            Assignees = [],
            ClearAssignees = true,
        });

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(0, body.RootElement.GetProperty("assignees").GetArrayLength());
    }

    [Fact]
    public async Task UpdateIssue_label_and_milestone_wire_fields_use_api_key_names()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        await client.UpdateIssueAsync("o", "n", 9, new UpdateIssueRequest
        {
            Labels = [3, 7],
            MilestoneId = 4,
            AddLabelIds = [8],
            RemoveLabelIds = [2],
        });

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal(2, root.GetProperty("labels").GetArrayLength());
        Assert.Equal(4, root.GetProperty("milestone_id").GetInt32());
        Assert.Equal(1, root.GetProperty("add_label_ids").GetArrayLength());
        Assert.Equal(1, root.GetProperty("remove_label_ids").GetArrayLength());
    }

    [Fact]
    public async Task UpdateIssue_rejects_an_empty_request()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UpdateIssueAsync("o", "n", 3, new UpdateIssueRequest()));

        Assert.Contains("no mutable field", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateIssue_rejects_invalid_index()
    {
        var handler = Enqueue(StatusCode.Created, IssueDoc);
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.UpdateIssueAsync("o", "n", 0, new UpdateIssueRequest { State = "open" }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateIssue_404_maps_to_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.UpdateIssueAsync("o", "n", 999, new UpdateIssueRequest { State = "closed" }));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task UpdateIssue_422_maps_to_exception()
    {
        var handler = Enqueue(StatusCode.UnprocessableEntity,
            """{"message":"422: invalid state","errors":[{"field":"state"}]}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.UpdateIssueAsync("o", "n", 3, new UpdateIssueRequest { State = "weird" }));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("422", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // AddIssueCommentAsync — POST /repos/{o}/{n}/issues/{index}/comments
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddIssueComment_posts_body_key_not_content()
    {
        var handler = Enqueue(StatusCode.Created, """
        {
          "id": 55, "body": "tier-2 smoke",
          "user": {"login": "hermes-agent", "id": 21},
          "created_at": "2026-08-26T10:00:00Z",
          "html_url": "https://git.example.com/o/n/issues/7#issuecomment-55"
        }
        """);
        using var client = ClientFor(handler);

        var comment = await client.AddIssueCommentAsync("o", "n", 7,
            new AddIssueCommentRequest { Body = "tier-2 smoke" });

        Assert.Equal(55, comment.Id);
        Assert.Equal("hermes-agent", comment.User!.Login);
        Assert.Equal(DateTimeKind.Utc, comment.CreatedAt.Kind);
        Assert.Equal("https://git.example.com/o/n/issues/7#issuecomment-55", comment.HtmlUrl);
        assertUri(handler, "/api/v1/repos/o/n/issues/7/comments", "POST", hasJsonBody: true);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("tier-2 smoke", body.RootElement.GetProperty("body").GetString());
        // The API on this instance 422s the `content` key; assert we don't send it.
        Assert.False(body.RootElement.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task AddIssueComment_rejects_blank_body_and_bad_index()
    {
        var handler = Enqueue(StatusCode.Created, "{}");
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.AddIssueCommentAsync("o", "n", 7, new AddIssueCommentRequest { Body = "" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.AddIssueCommentAsync("o", "n", 0, new AddIssueCommentRequest { Body = "x" }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AddIssueComment_404_maps_to_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """{"message":"not found"}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.AddIssueCommentAsync("o", "n", 999, new AddIssueCommentRequest { Body = "x" }));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // CreatePullRequestAsync — POST /repos/{o}/{n}/pulls
    // ------------------------------------------------------------------

    private const string PullDoc = """
        {
          "id": 200, "number": 11, "title": "smoke PR", "body": "details",
          "state": "open", "merged": false, "draft": true,
          "base": {"label": "o:main", "ref": "main"},
          "head": {"label": "o:feat/t2", "ref": "feat/t2"},
          "created_at": "2026-08-26T10:05:00Z", "updated_at": "2026-08-26T10:05:00Z",
          "html_url": "https://git.example.com/o/n/pulls/11"
        }
        """;

    [Fact]
    public async Task CreatePullRequest_sends_minimal_payload()
    {
        var handler = Enqueue(StatusCode.Created, PullDoc);
        using var client = ClientFor(handler);

        var pr = await client.CreatePullRequestAsync("o", "n", new CreatePullRequestRequest
        {
            Title = "smoke PR",
            Body = "details",
            Base = "main",
            Head = "feat/t2",
            Draft = true,
        });

        Assert.Equal(11, pr.Number);
        Assert.Equal("open", pr.State);
        Assert.True(pr.Draft);
        assertUri(handler, "/api/v1/repos/o/n/pulls", "POST", hasJsonBody: true);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("smoke PR", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("details", body.RootElement.GetProperty("body").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Equal("feat/t2", body.RootElement.GetProperty("head").GetString());
        Assert.True(body.RootElement.GetProperty("draft").GetBoolean());
        // Minimal by design: labels/assignees/milestone are not sent.
        Assert.False(body.RootElement.TryGetProperty("labels", out _));
        Assert.False(body.RootElement.TryGetProperty("assignees", out _));
    }

    [Fact]
    public async Task CreatePullRequest_rejects_missing_required_fields()
    {
        var handler = Enqueue(StatusCode.Created, PullDoc);
        using var client = ClientFor(handler);

        var req = new CreatePullRequestRequest { Title = "t", Base = "main", Head = "h" };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreatePullRequestAsync("o", "n", new CreatePullRequestRequest { Title = "", Base = "main", Head = "h" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreatePullRequestAsync("o", "n", new CreatePullRequestRequest { Title = "t", Base = "", Head = "h" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreatePullRequestAsync("o", "n", new CreatePullRequestRequest { Title = "t", Base = "main", Head = "" }));
        Assert.Empty(handler.Requests);
        _ = req;
    }

    [Fact]
    public async Task CreatePullRequest_404_body_is_preserved_on_the_exception()
    {
        var handler = Enqueue(StatusCode.NotFound, """
        {"message":"could not find 'ghost' to be a commit, branch or tag","errors":[{"field":"head"}]}
        """);
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.CreatePullRequestAsync("o", "n", new CreatePullRequestRequest
            {
                Title = "t", Base = "main", Head = "ghost",
            }));

        Assert.Equal(404, ex.StatusCode);
        // The raw server response must survive so the caller can surface the
        // actionable "could not find '...'" guidance verbatim.
        Assert.Contains("could not find 'ghost'", ex.ResponseBody ?? "");
    }

    [Fact]
    public async Task CreatePullRequest_422_maps_to_exception_with_status()
    {
        var handler = Enqueue(StatusCode.UnprocessableEntity,
            """{"message":"PR already exists or title empty","errors":[{"field":"title"}]}""");
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ForgejoException>(() =>
            client.CreatePullRequestAsync("o", "n", new CreatePullRequestRequest
            {
                Title = "t", Base = "main", Head = "h",
            }));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("title", ex.ResponseBody ?? "");
    }

    private static void assertUri(TestHttpHandler h, string path, string method, bool hasJsonBody)
    {
        var req = h.Requests.Single();
        Assert.Equal(method, req.Method.Method);
        Assert.Equal(path, req.RequestUri!.AbsolutePath);
        if (hasJsonBody)
            Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        else
        {
            // GET requests must carry no body — either a null content or an
            // empty string (the serializer emits "{}" only when a payload is
            // supplied).
            Assert.True(req.Content is null || req.Content.Headers.ContentLength is 0,
                "GETs should carry no body");
        }
    }
}
