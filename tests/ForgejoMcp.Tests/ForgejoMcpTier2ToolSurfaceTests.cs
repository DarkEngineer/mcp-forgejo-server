// SPDX-License-Identifier: MIT

namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using Forgejo.Client;
using Forgejo.Mcp;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// MCP tool-surface tests for the Tier-2 tools: <c>list_branches</c>,
/// <c>get_branch</c>, <c>list_labels</c>, <c>create_label</c> (including the
/// idempotency path), <c>update_issue</c> (including the clear_assignees
/// opt-in contract), <c>add_issue_comment</c>, <c>create_pull_request</c>
/// (including the missing-branch error envelope). Tools never throw for
/// API-level failures — they emit the <c>{"error":{...}}</c> payload.
/// </summary>
public class ForgejoMcpTier2ToolSurfaceTests
{
    private static (ForgejoMcpToolSurface Surface, TestHttpHandler Handler) Build(
        IEnumerable<HttpResponseMessage>? responses = null)
    {
        var handler = new TestHttpHandler(responses);
        var client = new ForgejoClient(
            new Uri("https://git.example.com"),
            ForgejoCredentials.Anonymous(),
            retryBaseDelay: TimeSpan.Zero,
            httpClient: new HttpClient(handler));
        return (new ForgejoMcpToolSurface(client), handler);
    }

    private static HttpResponseMessage Ok(string body)
        => new(StatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Created(string body)
        => new(StatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Notfound(string message)
        => new(StatusCode.NotFound)
        { Content = new StringContent("{\"message\":\"" + message + "\"}", Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------
    // list_branches
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListBranches_returns_branch_objects()
    {
        var (surface, handler) = Build(new[] { Ok("""
            [
              {"name": "main", "commit": {"id": "abc", "message": "First\nbody"}},
              {"name": "dev", "commit": {"id": "def", "message": "WIP"}}
            ]
            """) });

        var json = await surface.ListBranches("o", "n");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, arr.Count);
        Assert.Equal("main", arr[0].GetProperty("name").GetString());
        Assert.Equal("abc", arr[0].GetProperty("commit_id").GetString());
        Assert.Equal("First", arr[0].GetProperty("commit_message").GetString());
    }

    [Fact]
    public async Task ListBranches_404_returns_not_found_envelope()
    {
        var (surface, _) = Build(new[] { Notfound("reponoaccess") });
        var json = await surface.ListBranches("o", "n");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListBranches_blank_owner_is_invalid_request()
    {
        var (surface, handler) = Build();
        var json = await surface.ListBranches("  ", "n");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // get_branch
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBranch_returns_a_single_branch_object()
    {
        var (surface, handler) = Build(new[] { Ok("""
            {"name": "main", "commit": {"id": "cafebabe", "message": "tip"}}
            """) });
        var json = await surface.GetBranch("o", "n", "main");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("main", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("cafebabe", doc.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal("/api/v1/repos/o/n/branches/main", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetBranch_404_returns_not_found_envelope()
    {
        var (surface, _) = Build(new[] { Notfound("nobranch") });
        var json = await surface.GetBranch("o", "n", "nope");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("branches/nope", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetBranch_blank_branch_is_invalid_request()
    {
        var (surface, handler) = Build();
        var json = await surface.GetBranch("o", "n", "");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // list_labels
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListLabels_returns_id_name_color_description()
    {
        var (surface, handler) = Build(new[] { Ok("""
            [{"id": 1, "name": "bug", "color": "#ee0000", "description": "yikes"}]
            """) });
        var json = await surface.ListLabels("o", "n");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var label = doc.RootElement.EnumerateArray().Single();
        Assert.Equal(1, label.GetProperty("id").GetInt32());
        Assert.Equal("bug", label.GetProperty("name").GetString());
        Assert.Equal("#ee0000", label.GetProperty("color").GetString());
        Assert.Equal("yikes", label.GetProperty("description").GetString());
        Assert.Equal("/api/v1/repos/o/n/labels", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListLabels_empty_array_is_a_valid_result()
    {
        var (surface, _) = Build(new[] { Ok("[]") });
        var json = await surface.ListLabels("o", "n");
        Assert.Equal("[]", json);
    }

    // ------------------------------------------------------------------
    // create_label — success + idempotency
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateLabel_creates_when_absent()
    {
        var list = Ok("[]");
        var created = Created(
            "{\"id\": 42, \"name\": \"new\", \"color\": \"#112233\", \"description\": \"d\"}");
        var (surface, handler) = Build(new[] { list, created });
        var json = await surface.CreateLabel("o", "n", "new", "#112233", "d");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal(new[] { "GET", "POST" },
            handler.Requests.Select(r => r.Method.Method).ToArray());
        Assert.Equal("#112233",
            doc.RootElement.GetProperty("label").GetProperty("color").GetString());
    }

    [Fact]
    public async Task CreateLabel_existing_name_returns_created_false_not_an_error()
    {
        // The idempotency path: the pre-check finds an existing label with
        // the same name → created=false, existing=<the label> — no POST is
        // issued and no error envelope is produced.
        var existingList = Ok(
            "[{\"id\": 7, \"name\": \"dup\", \"color\": \"#aabbcc\", \"description\": \"already here\"}]");
        var (surface, handler) = Build(new[] { existingList });
        var json = await surface.CreateLabel("o", "n", "dup", "#999999");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("created").GetBoolean());
        var existing = doc.RootElement.GetProperty("existing");
        Assert.Equal(7, existing.GetProperty("id").GetInt32());
        Assert.Equal("dup", existing.GetProperty("name").GetString());
        Assert.Equal("#aabbcc", existing.GetProperty("color").GetString());
        // Only the list call hit the wire — the POST was prevented.
        Assert.Single(handler.Requests);
        Assert.Equal("GET", handler.Requests.Single().Method.Method);
    }

    [Fact]
    public async Task CreateLabel_rejects_blank_color_before_http()
    {
        var (surface, handler) = Build(new[] { Ok("[]"), Created("{}") });
        var json = await surface.CreateLabel("o", "n", "x", "   ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // update_issue — sparse body, clear_assignees opt-in, error paths
    // ------------------------------------------------------------------

    private const string IssueWire = """
        {"id": 10, "number": 5, "title": "t", "state": "open",
         "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z"}
        """;

    [Fact]
    public async Task UpdateIssue_sends_patch_with_only_the_fields_supplied()
    {
        var (surface, handler) = Build(new[] { Created(IssueWire) });
        await surface.UpdateIssue("o", "n", 5, state: "closed");
        var req = handler.Requests.Single();
        Assert.Equal("PATCH", req.Method.Method);
        var body = System.Text.Json.JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("closed", body.RootElement.GetProperty("state").GetString());
        Assert.False(body.RootElement.TryGetProperty("title", out _));
        Assert.False(body.RootElement.TryGetProperty("body", out _));
        Assert.False(body.RootElement.TryGetProperty("assignees", out _));
    }

    [Fact]
    public async Task UpdateIssue_invalid_state_is_invalid_request()
    {
        var (surface, handler) = Build();
        var json = await surface.UpdateIssue("o", "n", 1, state: "weird");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateIssue_404_returns_not_found_envelope()
    {
        var (surface, _) = Build(new[] { Notfound("noissue") });
        var json = await surface.UpdateIssue("o", "n", 999, state: "closed");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("issues/999", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateIssue_422_returns_http_422_envelope()
    {
        var bad = new HttpResponseMessage(StatusCode.UnprocessableEntity)
        { Content = new StringContent("{\"message\":\"invalid state\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(new[] { bad });
        var json = await surface.UpdateIssue("o", "n", 5, state: "open");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("http_422", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("invalid state", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    // ------------------------------------------------------------------
    // add_issue_comment
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddIssueComment_posts_and_returns_comment_identity()
    {
        var (surface, handler) = Build(new[] { Created("""
            {"id": 3, "body": "tier-2 smoke", "user": {"login": "hermes-agent"},
             "created_at": "2026-08-26T10:00:00Z", "html_url": "https://g.example/o/n/issues/1#issuecomment-3"}
            """) });
        var json = await surface.AddIssueComment("o", "n", 1, "tier-2 smoke");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // The tool's output shape (IssueComment) carries exactly the identity
        // fields the task spec asks for — id, user, created_at, html_url.
        // The comment body itself is not echoed back by the model.
        Assert.Equal(3, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("hermes-agent", doc.RootElement.GetProperty("user").GetProperty("login").GetString());
        Assert.Equal("2026-08-26T10:00:00Z",
            doc.RootElement.GetProperty("created_at").GetString());
        Assert.Equal("https://g.example/o/n/issues/1#issuecomment-3",
            doc.RootElement.GetProperty("html_url").GetString());
        Assert.Equal("POST", handler.Requests.Single().Method.Method);
        // The API on this instance 422s the `content` key; assert we send `body`.
        Assert.Equal("tier-2 smoke",
            System.Text.Json.JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("body").GetString());
        var sent = System.Text.Json.JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.False(sent.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task AddIssueComment_blank_content_is_invalid_request()
    {
        var (surface, handler) = Build();
        var json = await surface.AddIssueComment("o", "n", 1, "");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // add_pr_comment (delegates to add_issue_comment's plumbing)
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddPrComment_posts_to_issues_comments_and_returns_comment_identity()
    {
        // The live API accepts PR numbers on the issues-comments endpoint, so
        // the acceptance round-trip posts against a real PR number.
        var (surface, handler) = Build(new[] { Created("""
            {"id": 7, "body": "pr review note", "user": {"login": "hermes-agent"},
             "created_at": "2026-09-01T21:30:00Z", "html_url": "https://g.example/o/n/pulls/15#issuecomment-7"}
            """) });
        var json = await surface.AddPrComment("o", "n", 15, "pr review note");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("hermes-agent", doc.RootElement.GetProperty("user").GetProperty("login").GetString());
        Assert.Equal("2026-09-01T21:30:00Z",
            doc.RootElement.GetProperty("created_at").GetString());
        Assert.Equal("https://g.example/o/n/pulls/15#issuecomment-7",
            doc.RootElement.GetProperty("html_url").GetString());
        // Wire contract: POST, same issues-comments endpoint, `body` key (not `content`).
        var (req, body) = (handler.Requests.Single(), handler.Bodies.Single());
        Assert.Equal("POST", req.Method.Method);
        Assert.EndsWith("/repos/o/n/issues/15/comments", req.RequestUri!.AbsolutePath);
        var sent = System.Text.Json.JsonDocument.Parse(body).RootElement;
        Assert.Equal("pr review note", sent.GetProperty("body").GetString());
        Assert.False(sent.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task AddPrComment_blank_content_is_invalid_request_and_never_hits_the_wire()
    {
        var (surface, handler) = Build();
        var json = await surface.AddPrComment("o", "n", 15, "  ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // create_pull_request
    // ------------------------------------------------------------------

    private const string PrWire = """
        {"id": 50, "number": 8, "title": "t", "state": "open", "draft": true,
         "base": {"ref": "main"}, "head": {"ref": "feat/x"},
         "created_at": "2026-08-26T10:00:00Z", "updated_at": "2026-08-26T10:00:00Z",
         "html_url": "https://g.example/o/n/pulls/8"}
        """;

    [Fact]
    public async Task CreatePullRequest_returns_identity_envelope()
    {
        var (surface, handler) = Build(new[] { Created(PrWire) });
        var json = await surface.CreatePullRequest("o", "n", "t", @base: "main", head: "feat/x",
            body: "b", draft: true);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(50, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(8, doc.RootElement.GetProperty("number").GetInt32());
        Assert.Equal("open", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("main", doc.RootElement.GetProperty("base_ref").GetString());
        Assert.Equal("feat/x", doc.RootElement.GetProperty("head_ref").GetString());
        Assert.True(doc.RootElement.GetProperty("draft").GetBoolean());
        Assert.Equal("POST", handler.Requests.Single().Method.Method);
        var body = System.Text.Json.JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("main", body.GetProperty("base").GetString());
        Assert.Equal("feat/x", body.GetProperty("head").GetString());
    }

    [Fact]
    public async Task CreatePullRequest_404_missing_head_returns_not_found_envelope_with_hint()
    {
        var miss = new HttpResponseMessage(StatusCode.NotFound)
        { Content = new StringContent(
            "{\"message\":\"could not find 'ghost' to be a commit, branch or tag\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(new[] { miss });
        var json = await surface.CreatePullRequest("o", "n", "t", @base: "main", head: "ghost");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString();
        Assert.Contains("could not find 'ghost'", message);
        Assert.Contains("list_branches", message);
    }

    [Fact]
    public async Task CreatePullRequest_422_returns_http_422_envelope()
    {
        var bad = new HttpResponseMessage(StatusCode.UnprocessableEntity)
        { Content = new StringContent("{\"message\":\"already exists, or title empty\"}", Encoding.UTF8, "application/json") };
        var (surface, _) = Build(new[] { bad });
        var json = await surface.CreatePullRequest("o", "n", "t", @base: "main", head: "h");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("http_422", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreatePullRequest_missing_title_is_invalid_request()
    {
        var (surface, handler) = Build();
        var json = await surface.CreatePullRequest("o", "n", "", @base: "main", head: "h");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }
}
