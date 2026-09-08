namespace Forgejo.Tests;

using System.Net.Http;
using System.Text;
using Forgejo.Mcp;
using Forgejo.Client;
using Xunit;
using StatusCode = System.Net.HttpStatusCode;

/// <summary>
/// Tests of the MCP tool surface against a fake Forgejo API.
/// Tool methods never throw for API-level failures — they return the
/// <c>{"error":{...}}</c> JSON payload — so assert on the payload.
/// </summary>
public class ForgejoMcpToolSurfaceTests
{
    private static (ForgejoMcpToolSurface surface, TestHttpHandler handler) Build(
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

    [Fact]
    public async Task GetRepo_success_returns_snake_case_json()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":42,"full_name":"owner/repo","name":"repo","description":"demo","default_branch":"main"}""",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.GetRepo("owner", "repo");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("owner/repo", doc.RootElement.GetProperty("full_name").GetString());
        Assert.Equal("/api/v1/repos/owner/repo", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetRepo_404_returns_error_payload_not_exception()
    {
        var (surface, _) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.NotFound)
            {
                Content = new StringContent("""{"message":"not found"}""", Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.GetRepo("owner", "missing");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        Assert.Contains("Forgejo API error", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateIssue_posts_and_returns_created_issue()
    {
        // Two HTTP calls now: GET /labels (resolve "bug" -> id) then POST /issues.
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":42,"name":"bug","color":"d63333","description":""}]""",
                    Encoding.UTF8, "application/json"),
            },
            new HttpResponseMessage(StatusCode.Created)
            {
                Content = new StringContent("""{"id":9,"title":"Bug","state":"open"}""", Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.CreateIssue(
            owner: "o", name: "n", title: "Bug",
            body: "details", labels: new[] { "bug" }, assignees: null, milestone: null);

        // First request: the labels lookup.
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/v1/repos/o/n/labels", handler.Requests[0].RequestUri!.AbsolutePath);
        // Second request: the actual issue creation, with the label SENT AS AN ID.
        var post = handler.Requests[1];
        var body = handler.Bodies[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("/api/v1/repos/o/n/issues", post.RequestUri!.AbsolutePath);
        Assert.Contains("\"title\":\"Bug\"", body);
        Assert.Contains("\"body\":\"details\"", body);
        // Names are normalized to ids on the wire — no string labels leak out.
        Assert.Contains("\"labels\":[42]", body);
        Assert.DoesNotContain("bug", body);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(9, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task CreateIssue_passes_label_ids_through_unchanged()
    {
        // A bare integer string is used directly as an id — no labels lookup.
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.Created)
            {
                Content = new StringContent("""{"id":9,"title":"Bug","state":"open"}""", Encoding.UTF8, "application/json"),
            }
        });

        await surface.CreateIssue(
            owner: "o", name: "n", title: "Bug",
            labels: new[] { "101" });

        var body = handler.Bodies.Single();
        Assert.Contains("\"labels\":[101]", body);
        Assert.Single(handler.Requests); // only the POST; no GET /labels
    }

    [Fact]
    public async Task CreateIssue_unknown_label_name_returns_label_not_found()
    {
        var (surface, _) = Build(new[]
        {
            // list_labels returns labels that do NOT include "no-such".
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":42,"name":"bug","color":"d63333"}]""",
                    Encoding.UTF8, "application/json"),
            },
        });

        var json = await surface.CreateIssue(
            owner: "o", name: "n", title: "Bug", labels: new[] { "no-such" });

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("invalid_request", error.GetProperty("code").GetString());
        Assert.Contains("label_not_found".ToLowerInvariant(),
            error.GetProperty("message").GetString()!.ToLowerInvariant());
    }

    [Fact]
    public async Task ListIssues_sends_state_and_page_query()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":1,"title":"a","state":"open"},{"id":2,"title":"b","state":"open"}]""",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.ListIssues(
            owner: "o", name: "n", state: "all", label: new[] { "x", "y" }, page: 2, limit: 10);

        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/issues", url.AbsolutePath);
        // The client treats state="all" as "no state filter" and omits it from the query.
        Assert.DoesNotContain("state=", url.Query);
        Assert.Contains("labels=x", url.Query);
        Assert.Contains("page=2&limit=10", url.Query);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ListPullRequests_hits_pulls_endpoint()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            }
        });

        await surface.ListPullRequests(owner: "o", name: "n", state: "open");
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/pulls", url.AbsolutePath);
        Assert.Contains("state=open", url.Query);
    }

    [Fact]
    public async Task ListCommits_hits_commits_endpoint_with_branch()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":1,"sha":"abc","commit":{"message":"hello"}}]""",
                    Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.ListCommits(owner: "o", name: "n", branch: "dev", page: 1, limit: 5);
        var url = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/v1/repos/o/n/commits", url.AbsolutePath);
        Assert.Contains("branch=dev", url.Query);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("abc", doc.RootElement.GetProperty("items")[0].GetProperty("sha").GetString());
    }

    [Fact]
    public async Task ListCommits_items_expose_url_and_html_url()
    {
        // Issue #25: the `ListResult<RepositoryCommit>` model previously dropped
        // the wire `url`/`html_url`, leaving `list_commits` items with no usable
        // commit link — a consumer following an item's `url` to get commit detail
        // found nothing. The fix surfaces both, and the `url` is the git-data
        // route `get_commit` resolves against.
        var wire = "[" +
            "{\"sha\":\"7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0\"," +
            "\"url\":\"https://git.example.com/api/v1/repos/o/n/git/commits/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0\"," +
            "\"html_url\":\"https://git.example.com/o/n/commit/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0\"," +
            "\"commit\":{\"message\":\"hello\"}}" +
            "]";
        var (surface, _) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent(wire, Encoding.UTF8, "application/json"),
            }
        });

        var json = await surface.ListCommits("o", "n");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(
            "https://git.example.com/api/v1/repos/o/n/git/commits/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
            item.GetProperty("url").GetString());
        Assert.Equal(
            "https://git.example.com/o/n/commit/7ed6f8a2fcdb30649689b7770d3b5c2ba6693ad0",
            item.GetProperty("html_url").GetString());
    }

    [Fact]
    public async Task GetFile_returns_text_when_utf8()
    {
        var (surface, handler) = Build(new[]
        {
            new HttpResponseMessage(StatusCode.OK)
            {
                Content = new StringContent("# Hello\nworld", Encoding.UTF8, "text/markdown"),
            }
        });

        var json = await surface.GetFile("o", "n", "README.md", branch: "dev");
        var url = handler.Requests.Single().RequestUri!;
        Assert.EndsWith("/raw/README.md", url.AbsolutePath);
        Assert.Contains("ref=dev", url.Query);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("# Hello\nworld", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("text/markdown", doc.RootElement.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task EmptyTitle_returns_invalid_request_error()
    {
        var (surface, handler) = Build();
        // CreateIssue validates title before any HTTP call — expect the stable
        // invalid_request code and zero requests sent.
        var json = await surface.CreateIssue("", "n", "   ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
    }
}
